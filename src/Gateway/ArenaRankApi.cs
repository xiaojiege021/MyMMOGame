using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// B11 竞技场赛季段位的 HTTP 只读口与领外观口。网关只做三件事：认令牌、复核角色归属、
/// 生成事务号。分数怎么加、档位怎么分、外观发哪一件全在角色服的
/// config/arena_rank.json；季归全局服务，客户端带不进季、分数与档位。
/// 段位只影响荣誉与外观，不改竞技场模板属性（P2-3a 一字不变）。
/// </summary>
public static class ArenaRankApi
{
    public sealed record ClaimBody(ulong? CharacterId, string? TierKey);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var channel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(channel);

        // 只读：这一季多少分、当前哪一档、下一档差多少、每档外观领没领。
        app.MapGet("/arena/rank", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            var characterId = ParseId(context.Request.Query["character_id"].ToString());
            if (characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var reply = await character.DescribeArenaRankAsync(
                    new DescribeArenaRankRequest { CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("arena_rank account=" + account
                    + " id=" + characterId
                    + " ok=" + reply.Ok
                    + " reason=" + reply.Reason
                    + " season=" + reply.SeasonId
                    + " score=" + reply.Score
                    + " tier=" + reply.TierKey
                    + " wins=" + reply.Wins
                    + " tiers=" + reply.Tiers.Count);

                await WriteAsync(context, 200, new
                {
                    ok = reply.Ok,
                    reason = reply.Reason,
                    season_id = reply.SeasonId,
                    score = reply.Score,
                    tier_key = reply.TierKey,
                    tier_name = reply.TierName,
                    wins = reply.Wins,
                    next_tier_key = reply.NextTierKey,
                    next_tier_min_score = reply.NextTierMinScore,
                    tiers = reply.Tiers.Select(t => new
                    {
                        key = t.Key,
                        name = t.Name,
                        min_score = t.MinScore,
                        appearance_item_id = t.AppearanceItemId,
                        reached = t.Reached,
                        claimed = t.Claimed,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("arena_rank_reject account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, ex.StatusCode == StatusCode.NotFound ? 404 : 502,
                    new { error = ex.Status.Detail });
            }
        });

        // 只读：本季段位排行前 N 名。N 由角色服按配置截断，客户端只能要更少。
        app.MapGet("/arena/leaderboard", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            var limit = 0u;
            _ = uint.TryParse(context.Request.Query["limit"].ToString(), out limit);

            try
            {
                var reply = await character.DescribeArenaLeaderboardAsync(
                    new DescribeArenaLeaderboardRequest { Limit = limit },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("arena_leaderboard account=" + account
                    + " ok=" + reply.Ok
                    + " season=" + reply.SeasonId
                    + " limit=" + reply.Limit
                    + " rows=" + reply.Entries.Count);

                await WriteAsync(context, 200, new
                {
                    ok = reply.Ok,
                    reason = reply.Reason,
                    season_id = reply.SeasonId,
                    limit = reply.Limit,
                    entries = reply.Entries.Select(e => new
                    {
                        rank = e.Rank,
                        character_id = e.CharacterId,
                        name = e.Name,
                        class_id = e.ClassId,
                        score = e.Score,
                        tier_key = e.TierKey,
                        tier_name = e.TierName,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("arena_leaderboard_reject account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        // 领这一档的专属外观。门槛只认库里记下的分数；一季一档只领一次。
        app.MapPost("/arena/appearance/claim", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            ClaimBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<ClaimBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.TierKey))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var txnId = "arena_claim:" + Guid.NewGuid().ToString("N");
                var result = await character.ClaimArenaAppearanceAsync(
                    new ClaimArenaAppearanceRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TierKey = body.TierKey!.Trim(),
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("arena_appearance_claim account=" + account
                    + " id=" + body.CharacterId.Value
                    + " season=" + result.SeasonId
                    + " tier=" + result.TierKey
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " item=" + result.ItemId
                    + " slot=" + result.BagSlot
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    season_id = result.SeasonId,
                    tier_key = result.TierKey,
                    item_id = result.ItemId,
                    bag_slot = result.BagSlot,
                    bound = true,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("arena_appearance_claim_reject account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, ex.StatusCode == StatusCode.NotFound ? 404 : 502,
                    new { error = ex.Status.Detail });
            }
        });
    }

    static ulong ParseId(string raw) =>
        ulong.TryParse(raw, out var value) ? value : 0;

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
