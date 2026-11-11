using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.Trade;

namespace Gateway;

/// <summary>
/// 拍卖行的 HTTP 入口。账号只从令牌取，归属沿用角色服的只读背包口复核，
/// 事务号由网关生成；税率、税额、标价以外的东西客户端一个都带不进来。
/// 挂单与撮合在全局服务，货币与物品在角色服的一笔事务里。
/// </summary>
public static class AuctionApi
{
    public sealed record ListBody(ulong? CharacterId, uint? Slot, uint? Count, long? PriceGold);

    public sealed record BuyBody(ulong? CharacterId, ulong? ListingId);

    public sealed record IngotListBody(ulong? CharacterId, long? IngotAmount, long? PriceGold);

    public sealed record IngotBuyBody(ulong? CharacterId, ulong? ListingId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var tradeChannel = GrpcChannel.ForAddress(config.GlobalUpstream);
        var trade = new TradeService.TradeServiceClient(tradeChannel);
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(characterChannel);

        app.MapPost("/auction/list", async context =>
        {
            if (!Authorize(context, tokens, "auction_list", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            ListBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<ListBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.Slot is null || body.Slot == 0
                || body.Count is null || body.Count == 0
                || body.PriceGold is null || body.PriceGold <= 0)
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

                var txnId = "auction_list:" + Guid.NewGuid().ToString("N");
                var result = await trade.ListAuctionAsync(
                    new ListAuctionRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        Count = body.Count.Value,
                        PriceGold = body.PriceGold.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("auction_list account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " count=" + body.Count.Value
                    + " price=" + body.PriceGold.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " listing=" + result.ListingId);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    listing_id = result.ListingId,
                    seller_id = result.SellerId,
                    slot = result.Slot,
                    count = result.Count,
                    price_gold = result.PriceGold,
                    state = result.State,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("auction_list_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        app.MapPost("/auction/buy", async context =>
        {
            if (!Authorize(context, tokens, "auction_buy", out var account))
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
                || body.ListingId is null || body.ListingId == 0)
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

                var txnId = "auction_buy:" + Guid.NewGuid().ToString("N");
                var result = await trade.BuyAuctionAsync(
                    new BuyAuctionRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        ListingId = body.ListingId.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("auction_buy account=" + account
                    + " id=" + body.CharacterId.Value
                    + " listing=" + body.ListingId.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " price=" + result.PriceGold
                    + " tax=" + result.TaxGold
                    + " net=" + result.NetGold
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    listing_id = result.ListingId,
                    price_gold = result.PriceGold,
                    tax_gold = result.TaxGold,
                    net_gold = result.NetGold,
                    buyer_gold = result.BuyerGold,
                    seller_gold = result.SellerGold,
                    tax_rate_pct = result.TaxRatePct,
                    item_id = result.ItemId,
                    count = result.Count,
                    state = result.State,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("auction_buy_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        // P1-5b：元宝寄售挂单。元宝没有格位，请求只带角色、元宝数量与金币总价。
        app.MapPost("/auction/ingot/list", async context =>
        {
            if (!Authorize(context, tokens, "auction_ingot_list", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            IngotListBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<IngotListBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.IngotAmount is null || body.IngotAmount < 1
                || body.PriceGold is null || body.PriceGold < 1)
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

                var txnId = "auction_ingot_list:" + Guid.NewGuid().ToString("N");
                var result = await trade.ListIngotAsync(
                    new ListIngotRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        IngotAmount = body.IngotAmount.Value,
                        PriceGold = body.PriceGold.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("auction_ingot_list account=" + account
                    + " id=" + body.CharacterId.Value
                    + " ingot=" + body.IngotAmount.Value
                    + " price=" + body.PriceGold.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " listing=" + result.ListingId);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    listing_id = result.ListingId,
                    seller_id = result.SellerId,
                    ingot_amount = result.IngotAmount,
                    price_gold = result.PriceGold,
                    state = result.State,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("auction_ingot_list_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        // P1-5b：买元宝。只带买方角色与挂单号；标价、元宝、税率与到账都带不进来。
        app.MapPost("/auction/ingot/buy", async context =>
        {
            if (!Authorize(context, tokens, "auction_ingot_buy", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            IngotBuyBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<IngotBuyBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.ListingId is null || body.ListingId == 0)
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

                var txnId = "auction_ingot_buy:" + Guid.NewGuid().ToString("N");
                var result = await trade.BuyIngotAsync(
                    new BuyIngotRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        ListingId = body.ListingId.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("auction_ingot_buy account=" + account
                    + " id=" + body.CharacterId.Value
                    + " listing=" + body.ListingId.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " ingot=" + result.IngotAmount
                    + " price=" + result.PriceGold
                    + " tax=" + result.TaxGold
                    + " net=" + result.NetGold
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    listing_id = result.ListingId,
                    ingot_amount = result.IngotAmount,
                    price_gold = result.PriceGold,
                    tax_gold = result.TaxGold,
                    net_gold = result.NetGold,
                    buyer_gold = result.BuyerGold,
                    seller_gold = result.SellerGold,
                    buyer_ingot = result.BuyerIngot,
                    seller_ingot = result.SellerIngot,
                    tax_rate_pct = result.TaxRatePct,
                    state = result.State,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("auction_ingot_buy_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });
    }

    // 复用角色服已有的只读背包口复核归属，不为拍卖另起一套身份数据。
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
