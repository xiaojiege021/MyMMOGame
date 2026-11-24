using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// N13：境界子阶只读 HTTP 口。网关只认令牌、复核归属、生成事务号，
/// 再把 N10 已有的 <c>DescribeRealmPassive</c> / <c>PromoteRealmPhase</c> 转给角色服。
/// 段内阶名与被动万分比只在角色服按配置算，网关不读 <c>config/realms.json</c>、不本地算；
/// 伤害公式不接被动，客户端也不自己算。
/// </summary>
public static class RealmPhaseApi
{
    public sealed record PromoteBody(ulong? CharacterId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var character = new CharacterService.CharacterServiceClient(
            GrpcChannel.ForAddress(config.CharacterUpstream));

        // 只读：段内阶与被动事实值，一个角色一条。
        app.MapGet("/realm/phase", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (!await GatewayAuth.OwnsAsync(character, account, characterId, context))
            {
                await WriteAsync(context, 404, new { error = "character_not_found" });
                return;
            }

            try
            {
                var reply = await character.DescribeRealmPassiveAsync(
                    new DescribeRealmPassiveRequest { CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                if (!reply.Ok)
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                await WriteAsync(context, 200, new
                {
                    ok = true,
                    character_id = characterId,
                    realm = reply.Realm,
                    realm_phase = reply.RealmPhase,
                    phase_name = reply.PhaseName,
                    passive_pct_bps = reply.PassivePctBps,
                });
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
            }
        });

        // 升阶：事务号由网关生成，客户端不带；门槛与流水全在角色服。
        app.MapPost("/realm/phase/promote", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            PromoteBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<PromoteBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null or 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
            {
                await WriteAsync(context, 404, new { error = "character_not_found" });
                return;
            }

            var txnId = "realm_phase:" + Guid.NewGuid().ToString("N");
            try
            {
                var reply = await character.PromoteRealmPhaseAsync(
                    new PromoteRealmPhaseRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                // 业务拒绝走 HTTP 200 + ok=false，和别的 HTTP 口一致。
                await WriteAsync(context, 200, new
                {
                    ok = reply.Ok,
                    reason = reply.Reason,
                    txn_id = txnId,
                    character_id = body.CharacterId.Value,
                    from_phase = reply.FromPhase,
                    to_phase = reply.ToPhase,
                    phase_name = reply.PhaseName,
                    passive_pct_bps = reply.PassivePctBps,
                    level = reply.Level,
                    level_required = reply.LevelRequired,
                    duplicate = reply.Duplicate,
                });
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
            }
        });
    }
    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
