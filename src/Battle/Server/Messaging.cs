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
    /// <summary>结算结果、队友状态、阶段和收尾发全场：技术架构写的是「把结果发给本里的人」。</summary>
    Task BroadcastAsync(Party party, ServerMessage message, CancellationToken token) =>
        BroadcastAsync(party, message, token, exceptSocket: null);

    /// <summary>
    /// Q8：把服务端算好的归属转成一条回包。Winner=0 的行是「全员放弃」，客户端
    /// 只显示「无人需求」，不能据此认为自己可以捡——那一行没有发给任何人。
    /// </summary>
    static LootAssigned ToLootAssigned(LootAllocation allocation)
    {
        var message = new LootAssigned { Mode = allocation.Mode };
        foreach (var line in allocation.Lines)
        {
            message.Lines.Add(new LootAssignment
            {
                ItemId = line.ItemId,
                Count = line.Count,
                WinnerCharacterId = line.Winner,
            });
        }

        return message;
    }

    async Task BroadcastAsync(Party party, ServerMessage message, CancellationToken token, WebSocket? exceptSocket)
    {
        // P3-3a4：城战胜负只按服务端结算出来的有效伤害累计，按队伍归到这一场的
        // 两个桶里。不改 Encounter 的伤害公式、不进结算 / 经验 / 发奖；领地表缺失
        // （老卡临时配置）时连这一步都不做，老链路一条断言都不受影响。
        if (!config.SiegeTerritory.IsMissing
            && party.Key.StartsWith("siege-", StringComparison.Ordinal)
            && message.Result is { Hit: true, Damage: > 0 } result)
        {
            uint team = 0;
            lock (_siegeGate)
            {
                _siegeTeams.TryGetValue(result.CasterId, out team);
            }
            if (team <= 1)
            {
                lock (_gate)
                {
                    party.SiegeDamage[team] += result.Damage;
                }
            }
        }

        // P3-5a：世界 Boss 按宗门累计有效伤害。只用来在结束时挑「记住谁的名字」，
        // 不改伤害公式、不进结算 / 经验 / 发奖；属性里也没有任何归属项。
        // 键 0 是没宗门的人，照样累计（只算总伤害），只是不会被记成归属。
        if (party.WorldBossWindow.Length > 0
            && message.Result is { Hit: true, Damage: > 0 } bossHit)
        {
            ulong hitSect = 0;
            lock (_gate)
            {
                party.WorldBossSects.TryGetValue(bossHit.CasterId, out hitSect);
                party.WorldBossDamage.TryGetValue(hitSect, out var acc);
                party.WorldBossDamage[hitSect] = acc + bossHit.Damage;
            }
        }

        PartyConn[] conns;
        lock (_gate)
        {
            conns = [.. party.Connections.Where(c => c.Ready && c.Socket != exceptSocket)];
        }

        foreach (var conn in conns)
        {
            await SendAsync(conn.Socket, conn.Send, message, token);
        }
    }

    static async Task SendAsync(
        WebSocket socket, SemaphoreSlim send, ServerMessage message, CancellationToken token)
    {
        var bytes = message.ToByteArray();
        // WebSocket 不允许两条 SendAsync 并发，tick 和收包都会发，串起来。
        await send.WaitAsync(token);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    token);
            }
        }
        finally
        {
            send.Release();
        }
    }

    /// <summary>正常关一条连接。进本被拒时先说原因，再走这里。</summary>
    static async Task CloseAsync(WebSocket socket, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token);
        }
        catch (Exception)
        {
            // 对面已经断了就算了。
        }
    }
}
