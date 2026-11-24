using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.Trade;
using Mmo.Proto.World;
using CharacterClient = Mmo.Proto.Character.CharacterService.CharacterServiceClient;
using TradeClient = Mmo.Proto.Trade.TradeService.TradeServiceClient;
using WorldClient = Mmo.Proto.World.WorldService.WorldServiceClient;

namespace Gateway;

/// <summary>
/// B5 摆摊的 HTTP 入口。账号只从令牌取，归属沿用角色服的只读背包口复核，
/// 事务号与摊位号由网关注生成：
///   · 开摊 / 关摊 / 上架 / 看摊 → 角色服（等级门槛、格数、归属都在那儿）；
///   · 摊位显示 → 世界服（只存摘要，读不到角色库）；
///   · 成交 → 与拍卖同一条链（TradeService.BuyAuction → 角色服 SettleAsync），
///     税按挂单 channel 取 stall.json，世界服不碰背包。
/// </summary>
public static class StallApi
{
    public sealed record OpenBody(
        ulong? CharacterId, string? RegionId, double? PosX, double? PosY, string? Title);

    public sealed record CloseBody(ulong? CharacterId, string? StallId);

    public sealed record ListBody(
        ulong? CharacterId, string? StallId, uint? Slot, uint? Count, long? PriceGold);

    public sealed record BuyBody(ulong? CharacterId, ulong? ListingId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterClient(characterChannel);
        var worldChannel = GrpcChannel.ForAddress(config.WorldUpstream);
        var world = new WorldClient(worldChannel);
        var tradeChannel = GrpcChannel.ForAddress(config.GlobalUpstream);
        var trade = new TradeClient(tradeChannel);

        app.MapPost("/stall/open", async context =>
        {
            var account = GatewayAuth.AccountOf(context);
            OpenBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<OpenBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.RegionId))
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

                var txnId = "stall_open:" + Guid.NewGuid().ToString("N");
                var stallId = "stall_" + Guid.NewGuid().ToString("N");
                var result = await character.OpenStallAsync(
                    new OpenStallRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        RegionId = body.RegionId!.Trim(),
                        PosX = body.PosX ?? 0,
                        PosY = body.PosY ?? 0,
                        Title = (body.Title ?? "").Trim(),
                        TxnId = txnId,
                        StallId = stallId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                // 世界服只在角色服认下这个摊之后才登记显示；它不读角色库、不改背包。
                var shown = false;
                if (result.Ok)
                {
                    var display = await world.OpenStallAsync(
                        new WorldStallOpenRequest
                        {
                            StallId = result.StallId,
                            OwnerId = body.CharacterId.Value,
                            OwnerName = result.OwnerName,
                            RegionId = result.RegionId,
                            PosX = result.PosX,
                            PosY = result.PosY,
                            Title = result.Title,
                            LiveListings = result.LiveListings,
                        },
                        deadline: DateTime.UtcNow.AddSeconds(5),
                        cancellationToken: context.RequestAborted);
                    shown = display.Ok;
                }

                Console.WriteLine("stall_open account=" + account
                    + " id=" + body.CharacterId.Value
                    + " stall=" + result.StallId
                    + " region=" + result.RegionId
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " shown=" + (shown ? "true" : "false"));

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    stall_id = result.StallId,
                    owner_name = result.OwnerName,
                    region_id = result.RegionId,
                    pos_x = result.PosX,
                    pos_y = result.PosY,
                    title = result.Title,
                    live_listings = result.LiveListings,
                    slots = result.Slots,
                    min_level = result.MinLevel,
                    tax_rate_pct = result.TaxRatePct,
                    shown,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("stall_open_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteErrorAsync(context, ex);
            }
        });

        app.MapPost("/stall/close", async context =>
        {
            var account = GatewayAuth.AccountOf(context);
            CloseBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CloseBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.StallId))
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

                var txnId = "stall_close:" + Guid.NewGuid().ToString("N");
                var result = await character.CloseStallAsync(
                    new CloseStallRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        StallId = body.StallId!.Trim(),
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                if (result.Ok)
                {
                    await world.CloseStallAsync(
                        new WorldStallCloseRequest { StallId = result.StallId },
                        deadline: DateTime.UtcNow.AddSeconds(5),
                        cancellationToken: context.RequestAborted);
                }

                Console.WriteLine("stall_close account=" + account
                    + " id=" + body.CharacterId.Value
                    + " stall=" + result.StallId
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " cancelled=" + result.CancelledListings);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    stall_id = result.StallId,
                    cancelled_listings = result.CancelledListings,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("stall_close_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteErrorAsync(context, ex);
            }
        });

        app.MapPost("/stall/list", async context =>
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
                || string.IsNullOrWhiteSpace(body.StallId)
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

                var txnId = "stall_list:" + Guid.NewGuid().ToString("N");
                var result = await character.ListStallItemAsync(
                    new ListStallItemRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        StallId = body.StallId!.Trim(),
                        Slot = body.Slot.Value,
                        Count = body.Count.Value,
                        PriceGold = body.PriceGold.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                if (result.Ok)
                {
                    await world.RenewStallAsync(
                        new WorldStallRenewRequest
                        {
                            StallId = result.StallId,
                            LiveListings = result.LiveListings,
                        },
                        deadline: DateTime.UtcNow.AddSeconds(5),
                        cancellationToken: context.RequestAborted);
                }

                Console.WriteLine("stall_list account=" + account
                    + " id=" + body.CharacterId.Value
                    + " stall=" + result.StallId
                    + " slot=" + body.Slot.Value
                    + " count=" + body.Count.Value
                    + " price=" + body.PriceGold.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " listing=" + result.ListingId
                    + " live=" + result.LiveListings);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    listing_id = result.ListingId,
                    stall_id = result.StallId,
                    slot = result.Slot,
                    item_id = result.ItemId,
                    count = result.Count,
                    price_gold = result.PriceGold,
                    live_listings = result.LiveListings,
                    slots = result.Slots,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("stall_list_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteErrorAsync(context, ex);
            }
        });

        // 看摊是只读：谁都能看，价格与数量由角色服从库里读。
        app.MapGet("/stall/view", async context =>
        {
            // 管道已经挡过令牌；这里取一次账号只为日志与「每条路由都认账号」这一条形状。
            var account = GatewayAuth.AccountOf(context);
            var stallId = (context.Request.Query["stall_id"].ToString() ?? "").Trim();
            if (stallId.Length == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var result = await character.ViewStallAsync(
                    new ViewStallRequest { StallId = stallId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    found = result.Found,
                    stall_id = result.StallId,
                    owner_id = result.OwnerId,
                    owner_name = result.OwnerName,
                    region_id = result.RegionId,
                    pos_x = result.PosX,
                    pos_y = result.PosY,
                    title = result.Title,
                    state = result.State,
                    live_listings = result.LiveListings,
                    slots = result.Slots,
                    listings = result.Listings.Select(row => new
                    {
                        listing_id = row.ListingId,
                        slot = row.Slot,
                        item_id = row.ItemId,
                        item_name = row.ItemName,
                        count = row.Count,
                        price_gold = row.PriceGold,
                        bound = row.Bound,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("stall_view_error account=" + account
                    + " stall=" + stallId + " detail=" + ex.Status.Detail);
                await WriteErrorAsync(context, ex);
            }
        });

        // 摊位显示走世界服：它只有内存里的摘要，不读角色库。
        app.MapGet("/stalls", async context =>
        {
            // 同上：令牌由管道挡，这里取账号只为日志与形状。
            var account = GatewayAuth.AccountOf(context);
            var regionId = (context.Request.Query["region_id"].ToString() ?? "").Trim();
            try
            {
                var result = await world.ListStallsAsync(
                    new ListStallsRequest { RegionId = regionId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    count = result.Count,
                    region_id = regionId,
                    stalls = result.Stalls.Select(s => new
                    {
                        stall_id = s.StallId,
                        owner_id = s.OwnerId,
                        owner_name = s.OwnerName,
                        region_id = s.RegionId,
                        pos_x = s.PosX,
                        pos_y = s.PosY,
                        title = s.Title,
                        live_listings = s.LiveListings,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("stalls_error account=" + account
                    + " region=" + regionId + " detail=" + ex.Status.Detail);
                await WriteErrorAsync(context, ex);
            }
        });

        // 成交与拍卖同一条链：TradeService.BuyAuction → 角色服 SettleAuction。
        // 摆摊税按挂单 channel 取 stall.json；世界服随后只刷新「还剩几件」。
        app.MapPost("/stall/buy", async context =>
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

                var txnId = "stall_buy:" + Guid.NewGuid().ToString("N");
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

                if (result.Ok && result.StallId.Length > 0)
                {
                    await world.RenewStallAsync(
                        new WorldStallRenewRequest
                        {
                            StallId = result.StallId,
                            LiveListings = result.LiveListings,
                        },
                        deadline: DateTime.UtcNow.AddSeconds(5),
                        cancellationToken: context.RequestAborted);
                }

                Console.WriteLine("stall_buy account=" + account
                    + " id=" + body.CharacterId.Value
                    + " listing=" + body.ListingId.Value
                    + " stall=" + result.StallId
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " price=" + result.PriceGold
                    + " tax=" + result.TaxGold
                    + " net=" + result.NetGold
                    + " live=" + result.LiveListings);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    listing_id = result.ListingId,
                    stall_id = result.StallId,
                    price_gold = result.PriceGold,
                    tax_gold = result.TaxGold,
                    net_gold = result.NetGold,
                    buyer_gold = result.BuyerGold,
                    seller_gold = result.SellerGold,
                    tax_rate_pct = result.TaxRatePct,
                    item_id = result.ItemId,
                    count = result.Count,
                    state = result.State,
                    live_listings = result.LiveListings,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("stall_buy_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteErrorAsync(context, ex);
            }
        });
    }

    static async Task WriteErrorAsync(HttpContext context, RpcException ex)
    {
        var http = ex.StatusCode switch
        {
            StatusCode.InvalidArgument => 400,
            StatusCode.NotFound => 404,
            StatusCode.AlreadyExists => 409,
            StatusCode.FailedPrecondition => 412,
            StatusCode.Unavailable => 503,
            _ => 502,
        };
        await WriteAsync(context, http, new { error = ex.Status.Detail });
    }

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(body, Json);
    }
}
