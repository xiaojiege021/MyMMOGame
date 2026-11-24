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

    // P3-2a：灵石挂单只带角色、档位、数量与金币标价；税率、税额、档位集合、
    // 到账与事务号都不在请求体里（事务号由网关生成）。
    public sealed record StoneListBody(
        ulong? CharacterId, string? StoneKind, long? StoneAmount, long? PriceGold);

    // 买只带角色与挂单号。
    public sealed record StoneBuyBody(ulong? CharacterId, ulong? ListingId);

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
            var account = GatewayAuth.AccountOf(context);

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
                if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
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
            var account = GatewayAuth.AccountOf(context);

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
                if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
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
            var account = GatewayAuth.AccountOf(context);

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
                if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
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
            var account = GatewayAuth.AccountOf(context);

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
                if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
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

        // P3-2a：任务发放的灵石挂单。灵石在货币行不在背包，请求只带档位、数量与标价；
        // 归属沿用角色服只读背包口复核，事务号由网关生成，客户端带不进税率、税额或到账。
        app.MapPost("/auction/stone/list", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            StoneListBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<StoneListBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.StoneKind)
                || body.StoneAmount is null || body.StoneAmount <= 0
                || body.PriceGold is null || body.PriceGold <= 0)
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

                var txnId = "stone_list:" + Guid.NewGuid().ToString("N");
                var result = await trade.ListStoneAuctionAsync(
                    new ListStoneAuctionRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        StoneKind = body.StoneKind!.Trim(),
                        StoneAmount = body.StoneAmount.Value,
                        PriceGold = body.PriceGold.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("stone_list account=" + account
                    + " id=" + body.CharacterId.Value
                    + " kind=" + body.StoneKind!.Trim()
                    + " amount=" + body.StoneAmount.Value
                    + " price=" + body.PriceGold.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " listing=" + result.ListingId
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    listing_id = result.ListingId,
                    seller_id = result.SellerId,
                    stone_kind = result.StoneKind,
                    stone_amount = result.StoneAmount,
                    price_gold = result.PriceGold,
                    state = result.State,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("stone_list_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        // P3-2a：买灵石。只带买方角色与挂单号；档位、数量、标价、税率与到账都在服务端。
        app.MapPost("/auction/stone/buy", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            StoneBuyBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<StoneBuyBody>(
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
                if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var txnId = "stone_buy:" + Guid.NewGuid().ToString("N");
                var result = await trade.BuyStoneAuctionAsync(
                    new BuyStoneAuctionRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        ListingId = body.ListingId.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("stone_buy account=" + account
                    + " id=" + body.CharacterId.Value
                    + " listing=" + body.ListingId.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " kind=" + result.StoneKind
                    + " amount=" + result.StoneAmount
                    + " price=" + result.PriceGold
                    + " tax=" + result.TaxGold
                    + " net=" + result.NetGold
                    + " buyer_stone=" + result.BuyerStone
                    + " seller_stone=" + result.SellerStone
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    listing_id = result.ListingId,
                    stone_kind = result.StoneKind,
                    stone_amount = result.StoneAmount,
                    price_gold = result.PriceGold,
                    tax_gold = result.TaxGold,
                    net_gold = result.NetGold,
                    buyer_gold = result.BuyerGold,
                    seller_gold = result.SellerGold,
                    buyer_stone = result.BuyerStone,
                    seller_stone = result.SellerStone,
                    tax_rate_pct = result.TaxRatePct,
                    state = result.State,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("stone_buy_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
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
