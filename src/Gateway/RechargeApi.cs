using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// 内部充值的 HTTP 入口（P1-5d）。P0 不接真实支付渠道：这里两个口子，
/// 一个建单、一个模拟渠道回调。元宝数由角色服按 config/recharge.json 决定，
/// 请求里只能带商品编号与订单号；同一笔订单回调两次只发一次元宝。
///
/// 真正接渠道时，回调这条要换成渠道签名校验，不再用用户令牌；P0 先用令牌 +
/// 角色服侧「订单是不是这个账号的」双重核对，够核对用。
/// </summary>
public static class RechargeApi
{
    public sealed record OrderBody(ulong? CharacterId, string? ProductId);

    public sealed record CallbackBody(string? OrderId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(characterChannel);

        // 建单：只带 character_id 与 product_id，元宝数由角色服按配置决定。
        app.MapPost("/recharge/order", async context =>
        {
            if (!Authorize(context, tokens, "recharge_order", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            OrderBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<OrderBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.ProductId))
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

                var result = await character.CreateRechargeOrderAsync(
                    new CreateRechargeOrderRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        ProductId = body.ProductId!.Trim(),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("recharge_order account=" + account
                    + " id=" + body.CharacterId.Value
                    + " product=" + body.ProductId!.Trim()
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " order=" + result.OrderId
                    + " yuanbao=" + result.Yuanbao);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    order_id = result.OrderId,
                    product_id = result.ProductId,
                    yuanbao = result.Yuanbao,
                    status = result.Status,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("recharge_order_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        // 模拟渠道回调：同一笔订单重复到达只发一次。
        app.MapPost("/recharge/callback", async context =>
        {
            if (!Authorize(context, tokens, "recharge_callback", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            CallbackBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CallbackBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (string.IsNullOrWhiteSpace(body?.OrderId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var result = await character.SettleRechargeOrderAsync(
                    new SettleRechargeOrderRequest
                    {
                        Account = account,
                        OrderId = body.OrderId!.Trim(),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("recharge_callback account=" + account
                    + " order=" + body.OrderId!.Trim()
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " status=" + result.Status
                    + " yuanbao=" + result.Yuanbao
                    + " balance=" + result.Balance
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    order_id = result.OrderId,
                    status = result.Status,
                    yuanbao = result.Yuanbao,
                    balance = result.Balance,
                    paid_at_ms = result.PaidAtMs,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("recharge_callback_error account=" + account
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
