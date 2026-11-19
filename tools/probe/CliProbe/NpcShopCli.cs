using Npgsql;

using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// K2+Q13：NPC 商店的只读与连库核对。只读段把货架 / 回收价 / 回购窗口 / 修理单价打出来
/// （与启动共用同一 NpcShopTable.Load）；连库段直接调 NpcShopRepository 的四笔事务，
/// 用于 ubu 真库上的「买 → 限购 → 卖 → 回购 → 修理」核对。业务入口仍在网关的 /shop/npc/*。
/// </summary>
internal static class NpcShopCli
{
    /// <summary>
    /// 一行分发（CliParser ≤600 行闸门）：这一族 flag 都归本文件解析，加一个 flag 只改这里。
    /// </summary>
    public static bool Handles(string flag) =>
        flag is "--npc-shop-list" or "--npc-shop-table-path" or "--npc-shop-state"
            or "--npc-shop-shop" or "--npc-buy" or "--npc-sell" or "--npc-buyback"
            or "--npc-repair" or "--npc-txn" or "--raw-durability";

    public static void Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--npc-shop-list":
                npcShopList = true;
                break;
            case "--npc-shop-table-path" when i + 1 < args.Length:
                npcShopTablePath = args[++i];
                break;
            case "--npc-shop-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out npcShopStateOf);
                break;
            case "--npc-shop-shop" when i + 1 < args.Length:
                npcShopShopId = args[++i];
                break;
            case "--npc-buy" when i + 3 < args.Length:
                long.TryParse(args[++i], out npcBuyOf);
                npcBuyShop = args[++i];
                npcBuyItem = args[++i];
                break;
            case "--npc-sell" when i + 4 < args.Length:
                long.TryParse(args[++i], out npcSellOf);
                npcSellShop = args[++i];
                int.TryParse(args[++i], out npcSellSlot);
                int.TryParse(args[++i], out npcSellCount);
                break;
            case "--npc-buyback" when i + 3 < args.Length:
                long.TryParse(args[++i], out npcBuybackOf);
                npcBuybackShop = args[++i];
                long.TryParse(args[++i], out npcBuybackId);
                break;
            case "--npc-repair" when i + 1 < args.Length:
                long.TryParse(args[++i], out npcRepairOf);
                break;
            case "--npc-txn" when i + 1 < args.Length:
                npcTxn = args[++i];
                break;
            // 核对造数：把某个角色某件穿戴装备的耐久压到指定值（修理要看到缺失点数）。
            case "--raw-durability" when i + 3 < args.Length:
                long.TryParse(args[++i], out rawDurabilityOf);
                rawDurabilitySlot = args[++i];
                int.TryParse(args[++i], out rawDurabilityValue);
                break;
        }
    }

    public static int? TryReadOnly()
    {
        if (!npcShopList)
        {
            return null;
        }

        try
        {
            var config = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var currencies = CurrencyTable.Load("config/currency.json");
            var table = NpcShopTable.Load(
                npcShopTablePath ?? "config/npc_shop.json", currencies, items);

            Console.WriteLine("npc_shop_table=ok version=" + table.Version
                + " shops=" + table.Shops.Count
                + " shelf_kinds=" + table.ShelfKinds.Count
                + " forbidden=" + table.ForbiddenItems.Count
                + " sell=" + table.Sell.Items.Count
                + " buyback_keep=" + table.Buyback.Keep
                + " buyback_pct=" + table.Buyback.PricePct
                + " buyback_days=" + table.Buyback.ExpireDays
                + " repair_currency=" + table.Repair.Currency
                + " repair_per_point=" + table.Repair.PricePerPoint);

            foreach (var shop in table.Shops)
            {
                Console.WriteLine("npc_shop shop=" + shop.Id
                    + " npc=" + shop.NpcId
                    + " currency=" + shop.Currency
                    + " refresh=" + shop.Refresh
                    + " items=" + shop.Items.Count);
                foreach (var goods in shop.Items)
                {
                    Console.WriteLine("npc_shop_item shop=" + shop.Id
                        + " item=" + goods.ItemId
                        + " name=" + (items.Find(goods.ItemId)?.Name ?? goods.ItemId)
                        + " price=" + goods.Price
                        + " stock=" + goods.StockPerDay
                        + " limit=" + goods.LimitPerCharacter);
                }
            }

            foreach (var line in table.Sell.Items)
            {
                Console.WriteLine("npc_shop_sell item=" + line.ItemId
                    + " price=" + line.Price
                    + " buyback_price=" + table.BuybackPriceOf(line.Price));
            }

            foreach (var pair in table.Repair.DurabilityMax)
            {
                Console.WriteLine("npc_shop_durability grade=" + pair.Key + " max=" + pair.Value);
            }

            _ = config;
            return 0;
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }
    }

    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        if (!AnyNpcFlag())
        {
            return null;
        }

        var config = CharacterConfig.Load(configPath);
        var items = ItemTable.Load("config/items.json");
        var currencies = CurrencyTable.Load("config/currency.json");
        var calendar = CliLoaders.LoadCalendar(config, null);
        var table = NpcShopTable.Load(
            npcShopTablePath ?? "config/npc_shop.json", currencies, items);

        if (rawDurabilityOf != 0)
        {
            return await SetDurabilityAsync(ctx, rawDurabilityOf, rawDurabilitySlot ?? "",
                rawDurabilityValue);
        }

        var grants = new GrantRepository(ctx.DataSource, items, currencies, config);
        var shops = new NpcShopRepository(ctx.DataSource, table, items, config, grants, calendar);
        var txn = string.IsNullOrWhiteSpace(npcTxn)
            ? "npc_shop_probe:" + Guid.NewGuid().ToString("N")
            : npcTxn;

        try
        {
            if (npcShopStateOf != 0)
            {
                var view = await shops.DescribeAsync(
                    npcShopStateOf, npcShopShopId ?? table.Shops[0].Id, CancellationToken.None);
                Console.WriteLine("npc_shop_state id=" + npcShopStateOf
                    + " shop=" + view.ShopId
                    + " currency=" + view.Currency
                    + " balance=" + view.Balance
                    + " sell_balance=" + view.SellBalance
                    + " buyback=" + view.Buyback.Count
                    + " worn=" + view.Worn.Count
                    + " repair_per_point=" + view.RepairPricePerPoint);
                foreach (var goods in view.Goods)
                {
                    Console.WriteLine("npc_shop_state_item item=" + goods.ItemId
                        + " price=" + goods.Price
                        + " stock_left=" + goods.StockLeft
                        + " bought_today=" + goods.BoughtToday
                        + " limit=" + goods.LimitPerCharacter
                        + " enough=" + (goods.Enough ? "true" : "false")
                        + " sold_out=" + (goods.SoldOut ? "true" : "false")
                        + " limit_reached=" + (goods.LimitReached ? "true" : "false"));
                }

                foreach (var row in view.Buyback)
                {
                    Console.WriteLine("npc_shop_state_buyback id=" + row.Id
                        + " item=" + row.ItemId
                        + " count=" + row.Count
                        + " unit_price=" + row.UnitPrice);
                }

                foreach (var row in view.Worn)
                {
                    Console.WriteLine("npc_shop_state_worn slot=" + row.Slot
                        + " item=" + row.ItemId
                        + " durability=" + row.Durability
                        + " max=" + row.Max);
                }

                return 0;
            }

            if (npcBuyOf != 0)
            {
                var facts = await shops.BuyAsync(npcBuyOf, npcBuyShop ?? "", npcBuyItem ?? "",
                    txn, CancellationToken.None);
                Console.WriteLine("npc_buy id=" + npcBuyOf
                    + " shop=" + facts.ShopId
                    + " item=" + facts.ItemId
                    + " price=" + facts.Price
                    + " balance=" + facts.Balance
                    + " slot=" + facts.BagSlot
                    + " bought_today=" + facts.BoughtToday
                    + " stock_left=" + facts.StockLeft
                    + " duplicate=" + (facts.Duplicate ? "true" : "false"));
                return 0;
            }

            if (npcSellOf != 0)
            {
                var facts = await shops.SellAsync(npcSellOf, npcSellShop ?? "",
                    npcSellSlot, npcSellCount, txn, CancellationToken.None);
                Console.WriteLine("npc_sell id=" + npcSellOf
                    + " shop=" + facts.ShopId
                    + " item=" + facts.ItemId
                    + " count=" + facts.Count
                    + " gained=" + facts.Gained
                    + " balance=" + facts.Balance
                    + " buyback_id=" + facts.BuybackId
                    + " duplicate=" + (facts.Duplicate ? "true" : "false"));
                return 0;
            }

            if (npcBuybackOf != 0)
            {
                var facts = await shops.BuybackAsync(npcBuybackOf, npcBuybackShop ?? "",
                    npcBuybackId, txn, CancellationToken.None);
                Console.WriteLine("npc_buyback id=" + npcBuybackOf
                    + " buyback_id=" + facts.BuybackId
                    + " item=" + facts.ItemId
                    + " count=" + facts.Count
                    + " paid=" + facts.Paid
                    + " balance=" + facts.Balance
                    + " slot=" + facts.BagSlot
                    + " duplicate=" + (facts.Duplicate ? "true" : "false"));
                return 0;
            }

            if (npcRepairOf != 0)
            {
                var facts = await shops.RepairAsync(npcRepairOf, txn, CancellationToken.None);
                Console.WriteLine("npc_repair id=" + npcRepairOf
                    + " charged=" + facts.Charged
                    + " balance=" + facts.Balance
                    + " rows=" + facts.Repaired.Count
                    + " duplicate=" + (facts.Duplicate ? "true" : "false"));
                foreach (var row in facts.Repaired)
                {
                    Console.WriteLine("npc_repair_row slot=" + row.Slot
                        + " item=" + row.ItemId
                        + " before=" + row.Before
                        + " after=" + row.After
                        + " max=" + row.Max);
                }

                return 0;
            }
        }
        catch (NpcShopException ex)
        {
            // 业务拒绝一律 ok=false 的同一个出口：这里打 reject=code，退出码 0（不是进程错）。
            Console.WriteLine("npc_shop_reject reason=" + ex.Code);
            return 0;
        }
        catch (GrantException ex)
        {
            Console.WriteLine("npc_shop_reject reason=" + ex.Code);
            return 0;
        }

        return null;
    }

    /// <summary>核对造数：把某件穿戴装备的耐久压到指定值，修理才有缺失点数可修。</summary>
    static async Task<int> SetDurabilityAsync(
        CliContext ctx, long characterId, string slot, int value)
    {
        await using var command = ctx.DataSource.CreateCommand(
            "update \"角色\".inventory i set durability = $3 "
            + "from \"角色\".character_equip e "
            + "where e.inventory_id = i.id and e.character_id = $1 and e.slot = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(slot);
        command.Parameters.AddWithValue(value);
        var rows = await command.ExecuteNonQueryAsync();
        Console.WriteLine("raw_durability id=" + characterId + " slot=" + slot
            + " value=" + value + " rows=" + rows);
        return rows == 0 ? 7 : 0;
    }

    static bool AnyNpcFlag() =>
        npcShopStateOf != 0 || npcBuyOf != 0 || npcSellOf != 0 || npcBuybackOf != 0
        || npcRepairOf != 0 || rawDurabilityOf != 0;
}
