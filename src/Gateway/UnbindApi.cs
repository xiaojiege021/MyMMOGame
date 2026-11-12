using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// 解绑道具的 HTTP 入口（P1-5c8）。网关只做三件事：认令牌、复核角色归属、
/// 生成事务号后把角色号与格子转给角色服判定。张数、档位、绑定年龄全在
/// config/unbind_item.json 与角色库，请求体里一个都带不进来；绑定态只由
/// 角色服那一笔事务改，网关不写库。
///
/// 客户端界面入口等 U6（经济与商城 4 屏），这里只提供服务端可核对的通道。
/// </summary>
public static class UnbindApi
{
    public sealed record UseBody(ulong? CharacterId, int? Slot, string? ItemId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(characterChannel);

        app.MapPost("/unbind/use", async context =>
        {
            if (!Authorize(context, tokens, "unbind_use", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            UseBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<UseBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.Slot is null || body.Slot <= 0
                || string.IsNullOrWhiteSpace(body.ItemId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                // 复用角色服已有的只读背包口复核归属，不为这一张道具另起身份数据。
                if (!await OwnsAsync(character, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var txnId = "unbind_use:" + Guid.NewGuid().ToString("N");
                var result = await character.UseUnbindItemAsync(
                    new UseUnbindItemRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        ItemId = body.ItemId!.Trim(),
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("unbind_use account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " item=" + body.ItemId!.Trim()
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " target=" + result.TargetItemId
                    + " cost=" + result.Cost
                    + " left=" + result.CountLeft
                    + " age_hours=" + result.AgeHours
                    + " floor_hit=" + result.FloorHit);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    item_id = result.ItemId,
                    slot = result.Slot,
                    target_item_id = result.TargetItemId,
                    cost = result.Cost,
                    count_left = result.CountLeft,
                    age_hours = result.AgeHours,
                    floor_hit = result.FloorHit,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("unbind_use_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });
    }

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
