using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// 商城的 HTTP 入口。第 14 节第 41 行只要求「清单外的商品拒绝，且不扣元宝」，
/// 所以这里只做三件事：认令牌、复核角色归属、把商品编号转给角色服按清单判定。
/// 请求体只有角色号与商品编号——标价、数量、种类、绑定态客户端一个都带不进来。
/// 发货在 P1-5c2 起逐种接，届时按技术架构第 3 节把发货决策交给全局服务。
/// </summary>
public static class ShopApi
{
    public sealed record BuyBody(ulong? CharacterId, string? ItemId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(characterChannel);

        app.MapPost("/shop/buy", async context =>
        {
            if (!Authorize(context, tokens, "shop_buy", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            BuyBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<BuyBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.ItemId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                // 复用角色服已有的只读背包口复核归属，不为商城另起一套身份数据。
                if (!await OwnsAsync(character, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var txnId = "shop_buy:" + Guid.NewGuid().ToString("N");
                var result = await character.BuyShopItemAsync(
                    new BuyShopItemRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        ItemId = body.ItemId!.Trim(),
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("shop_buy account=" + account
                    + " id=" + body.CharacterId.Value
                    + " item=" + body.ItemId!.Trim()
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " kind=" + result.Kind
                    + " price=" + result.PriceYuanbao
                    + " balance=" + result.Balance
                    + " charged=" + result.ChargedYuanbao
                    + " delivered=" + result.Delivered);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    item_id = result.ItemId,
                    kind = result.Kind,
                    name = result.Name,
                    price_yuanbao = result.PriceYuanbao,
                    currency = result.Currency,
                    balance = result.Balance,
                    enough = result.Enough,
                    charged_yuanbao = result.ChargedYuanbao,
                    delivered = result.Delivered,
                    state = result.State,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("shop_buy_error account=" + account + " detail=" + ex.Status.Detail);
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
