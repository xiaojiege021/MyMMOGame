using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：进本 / 温泉 / 经验结算。</summary>
public static partial class CharacterApi
{
    static void RegisterProgression(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // 进本。主线 1 人、不扣疲劳不占份额；人数与规则都在角色服的副本表里。
        app.MapPost("/dungeon/enter", async context =>
        {
            if (!TryAuthorize(context, tokens, "dungeon_enter", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            EnterDungeonBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<EnterDungeonBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.PartySize is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var result = await client.EnterDungeonAsync(
                    new EnterDungeonRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        DungeonId = body.DungeonId ?? "main",
                        PartySize = body.PartySize.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("dungeon_enter account=" + account
                    + " id=" + body.CharacterId.Value
                    + " dungeon=" + (body.DungeonId ?? "main")
                    + " party=" + body.PartySize.Value
                    + " allowed=" + result.Allowed
                    + " reason=" + result.Reason
                    + " fatigue=" + result.Fatigue
                    + " cost=" + result.FatigueCost
                    + " cap=" + result.FatigueDailyCap
                    + " share=" + result.WeeklyShare
                    + " share_cost=" + result.WeeklyShareCost
                    + " share_left=" + result.WeeklyShareRemaining
                    + " rewards=" + result.RewardShares
                    + " min_players=" + result.MinPlayers
                    + " hard_entries_used=" + result.HardEntriesUsed
                    + " hard_entries_cap=" + result.HardEntriesCap
                    + " hard_entries_left=" + result.HardEntriesRemaining
                    + " deserter_s=" + result.DeserterRemainingS);

                await WriteAsync(context, 200, new
                {
                    allowed = result.Allowed,
                    reason = result.Reason,
                    max_players = result.MaxPlayers,
                    fatigue = result.Fatigue,
                    weekly_share = result.WeeklyShare,
                    entry_id = result.EntryId,
                    fatigue_cost = result.FatigueCost,
                    fatigue_daily_cap = result.FatigueDailyCap,
                    weekly_share_cost = result.WeeklyShareCost,
                    weekly_share_remaining = result.WeeklyShareRemaining,
                    weekly_share_cap = result.WeeklyShareCap,
                    reward_shares = result.RewardShares,
                    min_players = result.MinPlayers,
                    hard_entries_used = result.HardEntriesUsed,
                    hard_entries_cap = result.HardEntriesCap,
                    hard_entries_remaining = result.HardEntriesRemaining,
                    // Q9：逃跑惩罚还剩多少秒。0 表示没被罚；>0 时 allowed 一定是 false。
                    deserter_remaining_s = result.DeserterRemainingS,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "dungeon_enter", account, ex);
            }
        });

        // 温泉。恢复多少由角色服按配置定，请求里只有角色编号；网关不碰库。
        app.MapPost("/hot-spring", async context =>
        {
            if (!TryAuthorize(context, tokens, "hot_spring", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            HotSpringBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<HotSpringBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var result = await client.UseHotSpringAsync(
                    new UseHotSpringRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("hot_spring account=" + account
                    + " id=" + body.CharacterId.Value
                    + " before=" + result.FatigueBefore
                    + " recovered=" + result.Recovered
                    + " fatigue=" + result.Fatigue
                    + " cap=" + result.FatigueDailyCap
                    + " share=" + result.WeeklyShare);

                await WriteAsync(context, 200, new
                {
                    fatigue_before = result.FatigueBefore,
                    recovered = result.Recovered,
                    fatigue = result.Fatigue,
                    fatigue_daily_cap = result.FatigueDailyCap,
                    recovery_limit = result.RecoveryLimit,
                    weekly_share = result.WeeklyShare,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "hot_spring", account, ex);
            }
        });

        // 等级经验结算。账号从令牌取，数值全在角色服的 config/exp.json；
        // 网关只鉴权与转发，多传的经验/加成字段不会被读进请求。
        app.MapPost("/exp/settle", async context =>
        {
            if (!TryAuthorize(context, tokens, "exp_settle", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            SettleExpBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SettleExpBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.PartySize is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var result = await client.SettleExpAsync(
                    new SettleExpRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = (body.TxnId ?? "").Trim(),
                        Source = (body.Source ?? "").Trim(),
                        PartySize = body.PartySize.Value,
                        // P2-1c：把整队成员透传给角色服判同门；网关不解析谁同门。
                        MemberIds = { body.MemberIds ?? new List<ulong>() },
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("exp_settle account=" + account
                    + " id=" + body.CharacterId.Value
                    + " txn=" + (body.TxnId ?? "").Trim()
                    + " source=" + (body.Source ?? "").Trim()
                    + " party=" + body.PartySize.Value
                    + " duplicate=" + result.Duplicate
                    + " base=" + result.BaseExp
                    + " bonus=" + result.PartyBonus
                    + " same_sect=" + result.SameSectBonus
                    + " gain=" + result.ExpGain
                    + " level=" + result.LevelBefore + "->" + result.Level
                    + " exp=" + result.Exp
                    + " cap=" + result.LevelCap
                    + " booster_pct=" + result.BoosterPct
                    + " booster_exp=" + result.BoosterExp);

                await WriteAsync(context, 200, new
                {
                    duplicate = result.Duplicate,
                    base_exp = result.BaseExp,
                    party_bonus = result.PartyBonus,
                    // P2-1c：同宗门这一截额外经验（独立字段，只读事实值）。
                    same_sect_bonus = result.SameSectBonus,
                    exp_gain = result.ExpGain,
                    level_before = result.LevelBefore,
                    level = result.Level,
                    exp = result.Exp,
                    level_cap = result.LevelCap,
                    at_cap = result.AtCap,
                    party_size = result.PartySize,
                    source = result.Source,
                    // P1-5c7：这一笔生效的道具倍率与它额外带来的经验，只读事实值。
                    booster_pct = result.BoosterPct,
                    booster_exp = result.BoosterExp,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "exp_settle", account, ex);
            }
        });
    }
}
