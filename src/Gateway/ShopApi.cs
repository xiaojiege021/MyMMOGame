using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// 商城的 HTTP 入口。网关只做三件事：认令牌、复核角色归属、生成事务号后把商品编号
/// 转给角色服判定。扣款、入包、绑定全在角色服那一笔事务里，网关不碰钱也不碰背包。
/// 请求体只有角色号与商品编号——数量、标价、绑定态客户端一个都带不进来。
/// 发货按种类逐张卡接：P1-5c2 起只有保护券真发，其余仍是 delivery_pending。
/// </summary>
public static class ShopApi
{
    public sealed record BuyBody(ulong? CharacterId, string? ItemId);

    // K2+Q13：NPC 商店四件事的请求体。都只有编号与格号——价格、库存、回收价
    // 客户端一个都带不进来，判定与算钱全在角色服。
    public sealed record NpcBuyBody(ulong? CharacterId, string? ShopId, string? ItemId);

    public sealed record NpcSellBody(ulong? CharacterId, string? ShopId, int? BagSlot, int? Count);

    public sealed record NpcBuybackBody(ulong? CharacterId, string? ShopId, long? BuybackId);

    public sealed record NpcRepairBody(ulong? CharacterId);

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
                    + " delivered=" + result.Delivered
                    + " goods=" + result.DeliveryItemId
                    + " slot=" + result.BagSlot
                    + " bound=" + result.Bound
                    + " duplicate=" + result.Duplicate
                    + " character_slots=" + result.CharacterSlots
                    + " bag_slots=" + result.BagSlots);

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
                    delivery_item_id = result.DeliveryItemId,
                    bag_slot = result.BagSlot,
                    bound = result.Bound,
                    duplicate = result.Duplicate,
                    season_id = result.SeasonId,
                    pass_owned = result.PassOwned,
                    character_slots = result.CharacterSlots,
                    // B7+B8：背包扩容买完之后的格数（按角色的事实值）。别的商品恒 0。
                    bag_slots = result.BagSlots,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("shop_buy_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        // ---------------------------------------------------------------- K2+Q13 NPC 商店
        // 四件事一个面板：看货架、买、卖、回购、修理。网关只认令牌、复核归属、生成事务号，
        // 标价 / 库存 / 回收价 / 修理单价一个都不在这里算，全在角色服按配置与库判定。

        app.MapGet("/shop/npc", async context =>
        {
            if (!Authorize(context, tokens, "npc_shop_describe", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            if (!ulong.TryParse(context.Request.Query["character_id"], out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var shopId = context.Request.Query["shop_id"].ToString().Trim();

            try
            {
                if (!await OwnsAsync(character, account, characterId, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var result = await character.DescribeNpcShopAsync(
                    new DescribeNpcShopRequest { CharacterId = characterId, ShopId = shopId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    shop_id = result.ShopId,
                    npc_id = result.NpcId,
                    currency = result.Currency,
                    refresh = result.Refresh,
                    balance = result.Balance,
                    sell_balance = result.SellBalance,
                    repair_price_per_point = result.RepairPricePerPoint,
                    repair_currency = result.RepairCurrency,
                    goods = result.Goods.Select(g => new
                    {
                        item_id = g.ItemId,
                        name = g.Name,
                        price = g.Price,
                        stock_left = g.StockLeft,
                        unlimited = g.Unlimited,
                        bought_today = g.BoughtToday,
                        limit_per_character = g.LimitPerCharacter,
                        enough = g.Enough,
                        sold_out = g.SoldOut,
                        limit_reached = g.LimitReached,
                    }),
                    sell = result.Sell.Select(x => new
                    {
                        item_id = x.ItemId,
                        name = x.Name,
                        price = x.Price,
                    }),
                    buyback = result.Buyback.Select(x => new
                    {
                        id = x.Id,
                        item_id = x.ItemId,
                        name = x.Name,
                        count = x.Count,
                        unit_price = x.UnitPrice,
                        sold_at_ms = x.SoldAtMs,
                    }),
                    worn = result.Worn.Select(x => new
                    {
                        slot = x.Slot,
                        item_id = x.ItemId,
                        name = x.Name,
                        durability = x.Durability,
                        durability_max = x.DurabilityMax,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("npc_shop_describe_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        app.MapPost("/shop/npc/buy", async context =>
        {
            if (!Authorize(context, tokens, "npc_shop_buy", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            var body = await ReadBodyAsync<NpcBuyBody>(context);
            if (body?.CharacterId is null or 0 || string.IsNullOrWhiteSpace(body.ShopId)
                || string.IsNullOrWhiteSpace(body.ItemId))
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

                var txnId = "npc_shop_buy:" + Guid.NewGuid().ToString("N");
                var result = await character.BuyNpcShopItemAsync(
                    new BuyNpcShopItemRequest
                    {
                        CharacterId = body.CharacterId.Value,
                        ShopId = body.ShopId!.Trim(),
                        ItemId = body.ItemId!.Trim(),
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("npc_shop_buy account=" + account
                    + " id=" + body.CharacterId.Value
                    + " shop=" + body.ShopId!.Trim()
                    + " item=" + result.ItemId
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " price=" + result.Price
                    + " balance=" + result.Balance
                    + " slot=" + result.BagSlot
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    shop_id = result.ShopId,
                    item_id = result.ItemId,
                    name = result.Name,
                    currency = result.Currency,
                    price = result.Price,
                    balance = result.Balance,
                    stock_left = result.StockLeft,
                    bought_today = result.BoughtToday,
                    limit_per_character = result.LimitPerCharacter,
                    bag_slot = result.BagSlot,
                    count = result.Count,
                    bound = result.Bound,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("npc_shop_buy_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        app.MapPost("/shop/npc/sell", async context =>
        {
            if (!Authorize(context, tokens, "npc_shop_sell", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            var body = await ReadBodyAsync<NpcSellBody>(context);
            if (body?.CharacterId is null or 0 || string.IsNullOrWhiteSpace(body.ShopId)
                || body.BagSlot is null or < 1 || body.Count is null or < 1)
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

                var txnId = "npc_shop_sell:" + Guid.NewGuid().ToString("N");
                var result = await character.SellNpcItemAsync(
                    new SellNpcItemRequest
                    {
                        CharacterId = body.CharacterId.Value,
                        ShopId = body.ShopId!.Trim(),
                        BagSlot = body.BagSlot.Value,
                        Count = body.Count.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("npc_shop_sell account=" + account
                    + " id=" + body.CharacterId.Value
                    + " shop=" + body.ShopId!.Trim()
                    + " slot=" + body.BagSlot.Value
                    + " item=" + result.ItemId
                    + " count=" + result.Count
                    + " gained=" + result.Gained
                    + " balance=" + result.Balance
                    + " buyback=" + result.BuybackId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    shop_id = result.ShopId,
                    buyback_id = result.BuybackId,
                    item_id = result.ItemId,
                    name = result.Name,
                    count = result.Count,
                    unit_price = result.UnitPrice,
                    gained = result.Gained,
                    currency = result.Currency,
                    balance = result.Balance,
                    bag_slot = result.BagSlot,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("npc_shop_sell_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        app.MapPost("/shop/npc/buyback", async context =>
        {
            if (!Authorize(context, tokens, "npc_shop_buyback", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            var body = await ReadBodyAsync<NpcBuybackBody>(context);
            if (body?.CharacterId is null or 0 || string.IsNullOrWhiteSpace(body.ShopId)
                || body.BuybackId is null or < 1)
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

                var txnId = "npc_shop_buyback:" + Guid.NewGuid().ToString("N");
                var result = await character.BuybackNpcItemAsync(
                    new BuybackNpcItemRequest
                    {
                        CharacterId = body.CharacterId.Value,
                        ShopId = body.ShopId!.Trim(),
                        BuybackId = body.BuybackId.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("npc_shop_buyback account=" + account
                    + " id=" + body.CharacterId.Value
                    + " shop=" + body.ShopId!.Trim()
                    + " buyback=" + result.BuybackId
                    + " item=" + result.ItemId
                    + " paid=" + result.Paid
                    + " balance=" + result.Balance
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    buyback_id = result.BuybackId,
                    item_id = result.ItemId,
                    name = result.Name,
                    count = result.Count,
                    unit_price = result.UnitPrice,
                    paid = result.Paid,
                    currency = result.Currency,
                    balance = result.Balance,
                    bag_slot = result.BagSlot,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("npc_shop_buyback_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        app.MapPost("/shop/npc/repair", async context =>
        {
            if (!Authorize(context, tokens, "npc_shop_repair", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            var body = await ReadBodyAsync<NpcRepairBody>(context);
            if (body?.CharacterId is null or 0)
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

                var txnId = "npc_shop_repair:" + Guid.NewGuid().ToString("N");
                var result = await character.RepairNpcEquipmentAsync(
                    new RepairNpcEquipmentRequest
                    {
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("npc_shop_repair account=" + account
                    + " id=" + body.CharacterId.Value
                    + " charged=" + result.Charged
                    + " balance=" + result.Balance
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    charged = result.Charged,
                    currency = result.Currency,
                    balance = result.Balance,
                    duplicate = result.Duplicate,
                    repaired = result.Repaired.Select(x => new
                    {
                        slot = x.Slot,
                        item_id = x.ItemId,
                        before = x.Before,
                        after = x.After,
                        max = x.Max,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("npc_shop_repair_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });
    }

    static async Task<T?> ReadBodyAsync<T>(HttpContext context) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body, Json, context.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
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
