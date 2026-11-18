using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// P3-4a2 通行证的 HTTP 入口。网关只做三件事：认令牌、复核角色归属、生成事务号；
/// 哪一季归全局服务、两轨发什么归 config/season_pass.json、物品怎么入包归角色服。
/// 请求里只有角色号、轨与阶——季、物品编号、数量、绑定态客户端一个都带不进来。
/// </summary>
public static class PassApi
{
    public sealed record ClaimBody(ulong? CharacterId, string? Track, uint? Stage);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(characterChannel);

        // 只读：这一季两轨每阶发什么、领没领、付费轨开没开。
        app.MapGet("/season/pass", async context =>
        {
            if (!Authorize(context, tokens, "season_pass", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            var characterId = ParseId(context.Request.Query["character_id"].ToString());
            if (characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var reply = await character.DescribeSeasonPassAsync(
                    new DescribeSeasonPassRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("season_pass account=" + account
                    + " id=" + characterId
                    + " season=" + reply.SeasonId
                    + " found=" + reply.Found
                    + " paid=" + reply.Paid
                    + " price=" + reply.PriceYuanbao
                    + " points=" + reply.Points
                    + " per_stage=" + reply.PointsPerStage
                    + " stages=" + reply.Rewards.Count);

                await WriteAsync(context, 200, new
                {
                    ok = reply.Ok,
                    reason = reply.Reason,
                    found = reply.Found,
                    season_id = reply.SeasonId,
                    paid = reply.Paid,
                    price_yuanbao = reply.PriceYuanbao,
                    protect_item_id = reply.ProtectItemId,
                    // Q6/J4：本季点数与每阶门槛都是服务端事实值，界面只显示。
                    points = reply.Points,
                    points_per_stage = reply.PointsPerStage,
                    rewards = reply.Rewards.Select(r => new
                    {
                        track = r.Track,
                        stage = r.Stage,
                        item_id = r.ItemId,
                        count = r.Count,
                        claimed = r.Claimed,
                        required_points = r.RequiredPoints,
                        unlocked = r.Unlocked,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("season_pass_reject account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, ex.StatusCode == StatusCode.NotFound ? 404 : 502,
                    new { error = ex.Status.Detail });
            }
        });

        // 领一阶：季由角色服向全局服务确认，物品与数量全从配置读。
        app.MapPost("/season/pass/claim", async context =>
        {
            if (!Authorize(context, tokens, "season_pass_claim", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

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
                || string.IsNullOrWhiteSpace(body.Track) || body.Stage is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(character, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var txnId = "pass_claim:" + Guid.NewGuid().ToString("N");
                var result = await character.ClaimSeasonPassRewardAsync(
                    new ClaimSeasonPassRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Track = body.Track!.Trim(),
                        Stage = body.Stage.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("season_pass_claim account=" + account
                    + " id=" + body.CharacterId.Value
                    + " season=" + result.SeasonId
                    + " track=" + body.Track!.Trim()
                    + " stage=" + body.Stage.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " item=" + result.ItemId
                    + " count=" + result.Count
                    + " slot=" + result.BagSlot
                    + " bound=" + result.Bound
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    season_id = result.SeasonId,
                    track = result.Track,
                    stage = result.Stage,
                    item_id = result.ItemId,
                    count = result.Count,
                    bag_slot = result.BagSlot,
                    bound = result.Bound,
                    duplicate = result.Duplicate,
                    claimed = result.Claimed,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("season_pass_claim_reject account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, ex.StatusCode == StatusCode.NotFound ? 404 : 502,
                    new { error = ex.Status.Detail });
            }
        });
    }

    static ulong ParseId(string raw) =>
        ulong.TryParse(raw, out var value) ? value : 0;

    static async Task<bool> OwnsAsync(
        CharacterService.CharacterServiceClient client,
        string account, ulong characterId, HttpContext context)
    {
        try
        {
            await client.ListInventoryAsync(
                new ListInventoryRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: context.RequestAborted);
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }

    static bool Authorize(HttpContext context, TokenStore tokens, string what, out string account)
    {
        var token = context.Request.Query["token"].ToString();
        if (string.IsNullOrEmpty(token))
        {
            var header = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                token = header[prefix.Length..].Trim();
            }
        }

        if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out account, out _))
        {
            Console.WriteLine(what + "_reject reason=token");
            return false;
        }

        return true;
    }

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
