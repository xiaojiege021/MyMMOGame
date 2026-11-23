using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mmo.Proto.Battle;


namespace Battle;

// 14.8 D3a/D3b：BattleServer 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class BattleServer
{
    async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var query = context.Request.Query;

        var classId = QueryOrNull(query, "class") ?? config.DefaultClass;
        if (!config.Classes.TryGet(classId, out var cls))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var charRaw = QueryOrNull(query, "char");
        if (!ulong.TryParse(charRaw, out var charId) || charId == 0)
        {
            charId = config.Caster.Id;
        }

        // Q9/H4：逃跑惩罚挡在所有战斗入口的最前面（副本 / 竞技场 / 战场 / 城战 /
        // 世界 Boss 都算），不是只挡副本——否则换个模式就能绕过去。被罚的人连握手
        // 都不给，直接 403 带剩余秒数；重连回同一场的人不在惩罚表里，不受影响。
        if (ReconnectOn)
        {
            var remaining = await DeserterRemainingMsAsync(charId);
            if (remaining > 0)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "deserter_penalty",
                    remaining_s = (long)Math.Ceiling(remaining / 1000.0),
                });
                Console.WriteLine(
                    $"join_reject=deserter_penalty character={charId} remaining_ms={remaining}");
                return;
            }
        }

        // P3-5a：世界 Boss。入口在世界服，进去是战斗服的一场实例：按 config/world_boss.json
        // 的时刻出现，同一时刻一场（场次钥匙由服务端按自己的时钟算），首发上限走配置，
        // 一个人也能进。与副本 / 竞技场 / 战场 / 城战的参数互斥，混着带直接拒。
        var worldBossRaw = QueryOrNull(query, "worldboss");
        if (worldBossRaw is not null)
        {
            if (QueryOrNull(query, "dungeon") is not null
                || QueryOrNull(query, "arena_mode") is not null
                || QueryOrNull(query, "battlefield") is not null
                || QueryOrNull(query, "siege") is not null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=worldboss_with_other_mode entrance=" + worldBossRaw);
                return;
            }

            await HandleWorldBossAsync(context, worldBossRaw.Trim(), charRaw, cls);
            return;
        }

        // 城战：40 对 40，装备全额生效，不压同队战力、不套竞技场模板。
        // 与副本、竞技场、战场互斥；两支队伍共用同一场 Encounter，技能仍由本进程结算。
        var siegeRaw = QueryOrNull(query, "siege");
        if (siegeRaw is not null)
        {
            if (QueryOrNull(query, "dungeon") is not null
                || QueryOrNull(query, "arena_mode") is not null
                || QueryOrNull(query, "battlefield") is not null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=siege_with_other_mode match=" + siegeRaw);
                return;
            }

            if (config.Siege.IsMissing)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=siege_table_missing");
                return;
            }

            var siegeTeamRaw = (QueryOrNull(query, "team") ?? "").Trim().ToLowerInvariant();
            var siegeTeam = siegeTeamRaw switch { "a" => 0, "b" => 1, _ => -1 };
            if (siegeTeam < 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=siege_team_invalid raw=" + siegeTeamRaw);
                return;
            }

            // 城战属性以角色库为准，所以必须给角色编号：拿 caster 兜底会查到别人。
            if (!ulong.TryParse(charRaw, out var siegeCharId) || siegeCharId == 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=siege_character_missing");
                return;
            }

            await HandleSiegeAsync(context, siegeRaw.Trim(), siegeTeam, siegeCharId, cls);
            return;
        }

        // 战场：10 对 10，装备生效后按同队 1.3 倍压缩。与副本、竞技场互斥。
        var battlefieldRaw = QueryOrNull(query, "battlefield");
        if (battlefieldRaw is not null)
        {
            if (QueryOrNull(query, "dungeon") is not null || QueryOrNull(query, "arena_mode") is not null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=battlefield_with_other_mode match=" + battlefieldRaw);
                return;
            }

            if (config.Battlefield.IsMissing)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=battlefield_table_missing");
                return;
            }

            var teamRaw = (QueryOrNull(query, "team") ?? "").Trim().ToLowerInvariant();
            var team = teamRaw switch { "a" => 0, "b" => 1, _ => -1 };
            if (team < 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=battlefield_team_invalid raw=" + teamRaw);
                return;
            }

            // 战场属性以角色库为准，所以必须给角色编号：拿 caster 兜底会查到别人。
            if (!ulong.TryParse(charRaw, out var battleCharId) || battleCharId == 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=battlefield_character_missing");
                return;
            }

            await HandleBattlefieldAsync(context, battlefieldRaw.Trim(), team, battleCharId, cls);
            return;
        }
        // 剩下的副本 / 竞技场那一段在建组的文件里（D3a 拆出）。
        await JoinPartyAsync(context, query, charId, classId, cls);
    }

    // ---- 小工具：query 取值与同场判定（原样搬）----
    static string? QueryOrNull(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var values) && values.Count > 0 ? values[0] : null;

    /// <summary>同一个队伍钥匙只能待在一个副本里。空表示练习场。</summary>
    static bool SameDungeon(DungeonDef? left, DungeonDef? right) =>
        string.Equals(left?.Id ?? "", right?.Id ?? "", StringComparison.Ordinal);

    /// <summary>同一个队伍钥匙只能有一种竞技场模式。空表示不是竞技场。</summary>
    static bool SameArena(ArenaModeDef? left, ArenaModeDef? right) =>
        string.Equals(left?.Id ?? "", right?.Id ?? "", StringComparison.Ordinal);

    /// <summary>时间戳加进程内序号。重启后也不会和上一轮的场次撞号。</summary>
    static ulong NextSessionId()
    {
        var ms = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seq = (ulong)Interlocked.Increment(ref _sessionSeq) & 0xFFFFUL;
        return (ms << 16) | seq;
    }
}
