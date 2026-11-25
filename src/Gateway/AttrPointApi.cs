using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// A9：属性加点的 HTTP 口。A8 的只读口（GET /character/attr-points）在这个文件里一起管：
/// 令牌、归属复核、事务号生成在网关，四属性的键白名单、点数够不够、单属性上限与台账幂等
/// 全在角色服；网关不读 config/attr_points.json、不本地算，也不自己判「能不能洗点」——
/// 本版没有洗点口，方案写定的「不做免费洗点」在角色服的配置里硬校验。
/// </summary>
public static class AttrPointApi
{
    public sealed record AllocateBody(ulong? CharacterId, string? Attribute, int? Points);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var character = new CharacterService.CharacterServiceClient(
            GrpcChannel.ForAddress(config.CharacterUpstream));

        // 只读：手上还剩多少点、四属性各加了多少。基础值由客户端按职业表相加，
        // 服务端只回事实值（A8 的口沿用到这里，键名不变）。
        app.MapGet("/character/attr-points", async context =>
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
                var reply = await character.DescribeAttributePointsAsync(
                    new DescribeAttributePointsRequest { CharacterId = characterId },
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
                    class_id = reply.ClassId,
                    level = reply.Level,
                    attr_points = reply.AttrPoints,
                    strength = reply.Strength,
                    agility = reply.Agility,
                    spirit = reply.Spirit,
                    vitality = reply.Vitality,
                });
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
            }
        });

        // 加一次点：事务号由网关生成，客户端不带（重放不会重复扣点）。
        app.MapPost("/character/attr-points/allocate", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            AllocateBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<AllocateBody>(
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

            var attribute = (body.Attribute ?? "").Trim();
            var points = body.Points ?? 0;
            if (attribute.Length == 0 || points < 1)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
            {
                await WriteAsync(context, 404, new { error = "character_not_found" });
                return;
            }

            var txnId = "attr_point:" + Guid.NewGuid().ToString("N");
            try
            {
                var reply = await character.AllocateAttributePointsAsync(
                    new AllocateAttributePointsRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                        Attribute = attribute,
                        Points = points,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                // 业务拒绝走 HTTP 200 + ok=false，和别的 HTTP 口一致。
                await WriteAsync(context, 200, new
                {
                    ok = reply.Ok,
                    reason = reply.Reason,
                    duplicate = reply.Duplicate,
                    txn_id = txnId,
                    character_id = body.CharacterId.Value,
                    attribute,
                    points,
                    class_id = reply.ClassId,
                    level = reply.Level,
                    attr_points = reply.AttrPoints,
                    strength = reply.Strength,
                    agility = reply.Agility,
                    spirit = reply.Spirit,
                    vitality = reply.Vitality,
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
