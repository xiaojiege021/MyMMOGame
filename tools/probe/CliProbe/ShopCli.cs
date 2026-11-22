using Npgsql;

using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>商城 / 通行证 / 月叠 / 角色栏 / 充值只读与连库核对。</summary>
internal static class ShopCli
{
    public static int? TryReadOnly()
    {
        // 商城清单只读核对：与启动共用同一套校验（十八种白名单、币种必须是元宝、
        // 标价必须是正整数），不连库也不启动进程。坏清单在这一步就能看出来。
        if (shopList)
        {
            try
            {
                var characterConfig = CharacterConfig.Load(configPath);
                var shopCurrencies = CurrencyTable.Load("config/currency.json");
                // 发货校验要物品表与强化表：保护券的发货物必须就是 enhance.json 那一个编号，
                // 与启动走同一套 Load，坏清单在这里就露出来。
                var shopItems = ItemTable.Load("config/items.json");
                var shopEnhance = EnhanceTable.Load(characterConfig.EnhanceTable, shopItems, shopCurrencies);
                // 红符的发货物必须就是 talisman.json 那一个编号，与启动走同一套 Load。
                var shopTalismans = TalismanTable.Load(characterConfig.TalismanTable, shopItems);
                // P1-5c6：复活卷轴的发货物必须就是 revive_scroll.json 那一个编号，
                // 同样与启动走同一套 Load（比例从 classes.json 读，不另写一份）。
                var shopScrolls = ReviveScrollTable.Load(
                    characterConfig.ReviveScrollTable, "config/classes.json",
                    shopItems, shopTalismans);
                // P1-5c7：限时经验道具的发货物必须就是 exp_booster.json 那一个编号，
                // 同样与启动走同一套 Load。
                var shopBoosters = ExpBoosterTable.Load(
                    characterConfig.ExpBoosterTable, shopItems, shopTalismans, shopScrolls);
                // P1-5c8：解绑道具的发货物必须就是 unbind_item.json 那一个编号。
                var shopUnbinds = UnbindTable.Load(
                    characterConfig.UnbindTable, shopItems, shopTalismans, shopScrolls, shopBoosters);
                var table = ShopTable.Load(
                    shopConfigPath ?? characterConfig.ShopTable, shopCurrencies, shopItems, shopEnhance,
                    shopTalismans, shopScrolls, shopBoosters, shopUnbinds);
                Console.WriteLine("shop_list_state=ok version=" + table.Version
                    + " currency=" + table.Currency
                    + " items=" + table.Items.Count
                    + " kinds=" + table.Categories.Count
                    + " delivered_kinds=" + table.DeliveredCount
                    + " deliverable_items=" + table.Items.Count(i => i.Deliverable));
                foreach (var item in table.Items)
                {
                    // K1：每条都带上它自己的发货方式与限购 / 库存，清单改一条重启就能在这里核对到。
                    Console.WriteLine("shop_item=" + item.Id
                        + " kind=" + item.Kind
                        + " price=" + item.PriceYuanbao
                        + " mode=" + ShopTable.ModeText(item.Mode)
                        + " delivery=" + (item.Mode switch
                        {
                            DeliveryMode.Item => item.DeliveryItemId + "x" + item.DeliveryCount,
                            DeliveryMode.None => "pending",
                            _ => "state",
                        })
                        + " limit=" + item.LimitPerCharacter + "/" + item.LimitPerDay
                        + " stock=" + item.StockRule + (item.StockCount > 0 ? "x" + item.StockCount : "")
                        + " on_sale=" + (item.OnSale ? "true" : "false")
                        + " name=" + item.Name);
                }
            }
            catch (ConfigException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }
            catch (IOException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }

            return 0;
        }

        // P3-4a2 通行证只读核对：与启动共用同一套校验（免费轨只许称号与外观碎片、付费轨只许
        // 当季外观 / 洞府摆件 / 保护券、保护券必须就是 enhance.json 那一件且到账即绑、
        // 标价只从 shop.json 那条 season_pass 商品读），不连库也不启动进程。
        if (seasonPassList)
        {
            try
            {
                var passConfig = CharacterConfig.Load(configPath);
                var (passTable, _) = LoadSeasonPass(passConfig, seasonPassPath);
                Console.WriteLine("season_pass_list_state=ok version=" + passTable.Version
                    + " pending=" + (passTable.Pending ? "true" : "false")
                    + " seasons=" + passTable.Seasons.Count
                    + " product=" + passTable.ProductId
                    + " price=" + passTable.PriceYuanbao
                    + " protect=" + passTable.ProtectItemId
                    + " protect_per_stage_max=" + passTable.ProtectPerStageMax);
                foreach (var season in passTable.Seasons)
                {
                    Console.WriteLine("season_pass_season=" + season.SeasonId
                        + " free=" + season.Free.Count
                        + " paid=" + season.Paid.Count);
                    foreach (var stage in season.Free)
                    {
                        Console.WriteLine("season_pass_stage=" + season.SeasonId + " track=free"
                            + " stage=" + stage.Stage
                            + " kind=" + stage.Rewards[0].Kind
                            + " item=" + stage.Rewards[0].ItemId
                            + " count=" + stage.Rewards[0].Count
                            + " bind=always");
                    }

                    foreach (var stage in season.Paid)
                    {
                        Console.WriteLine("season_pass_stage=" + season.SeasonId + " track=paid"
                            + " stage=" + stage.Stage
                            + " kind=" + stage.Rewards[0].Kind
                            + " item=" + stage.Rewards[0].ItemId
                            + " count=" + stage.Rewards[0].Count
                            + " bind=always");
                    }
                }
            }
            catch (ConfigException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }
            catch (IOException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }

            return 0;
        }

        // 拍卖税率只读核对：与启动共用同一套 5%–8% 写定区间校验，不连库也不启动进程。
        if (monthlyBundleList)
        {
            try
            {
                var bundleConfig = CharacterConfig.Load(configPath);
                var bundleTable = LoadMonthlyBundle(bundleConfig, monthlyBundlePath);
                Console.WriteLine("monthly_bundle_list_state=ok version=" + bundleTable.Version
                    + " pending=" + (bundleTable.Pending ? "true" : "false")
                    + " product=" + bundleTable.ProductId
                    + " price=" + bundleTable.PriceYuanbao
                    + " days=" + bundleTable.Days
                    + " kinds=" + bundleTable.Entries.Count);
                foreach (var entry in bundleTable.Entries)
                {
                    Console.WriteLine("monthly_bundle_item=" + entry.Kind
                        + " item=" + entry.ItemId
                        + " per_day=" + entry.PerDay
                        + " count=" + entry.Count
                        + " bind=always");
                }
            }
            catch (ConfigException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }
            catch (IOException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }

            return 0;
        }

        // P3-4b：第二角色栏只读核对。与启动共用同一 CharacterSlotTable.Load，
        // 基础位不是 1、上限不是「基础位 + 1」、商品不是 second_slot，都在这一步起不来。
        if (characterSlotList)
        {
            try
            {
                var slotConfig = CharacterConfig.Load(configPath);
                var slotTable = LoadCharacterSlot(slotConfig, characterSlotPath);
                Console.WriteLine("character_slot_list_state=ok version=" + slotTable.Version
                    + " pending=" + (slotTable.Pending ? "true" : "false")
                    + " product=" + slotTable.ProductId
                    + " price=" + slotTable.PriceYuanbao
                    + " base_slots=" + slotTable.BaseSlots
                    + " max_slots=" + slotTable.MaxSlots
                    + " table=character_slot");
            }
            catch (ConfigException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }
            catch (IOException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }

            return 0;
        }

        // P1-5d 内部充值商品表只读核对：与启动共用同一套校验（schema / version /
        // 编号唯一 / 元宝与金额为正），不连库也不启动进程；临时表用 --recharge-config 指过去。
        if (rechargeList)
        {
            try
            {
                var rechargeConfig = CharacterConfig.Load(configPath);
                var table = RechargeTable.Load(rechargeConfigPath ?? rechargeConfig.RechargeTable);
                Console.WriteLine("recharge_list_state=ok version=" + table.Version
                    + " products=" + table.Products.Count
                    + " product=" + string.Join(",", table.Products
                        .Select(p => p.Id + ":" + p.Yuanbao + ":" + p.RmbCents)));
            }
            catch (ConfigException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }
            catch (IOException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }

            return 0;
        }

        return null;
    }

    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        var dataSource = ctx.DataSource;
        if (shopStateOf != 0)
        {
            var shopMoney = new CurrencyRepository(dataSource);
            var shopBalances = await shopMoney.AllAsync(shopStateOf, CancellationToken.None);
            var shopCurrencies = CurrencyTable.Load("config/currency.json");
            var shopItemsForState = ItemTable.Load("config/items.json");
            var shopCharConfig = CharacterConfig.Load(configPath);
            var shopEnhanceForState = EnhanceTable.Load(
                shopCharConfig.EnhanceTable, shopItemsForState, shopCurrencies);
            var shopTalismansForState = TalismanTable.Load(
                shopCharConfig.TalismanTable, shopItemsForState);
            // 与启动、--shop-list 走同一套 Load：卷轴发货物必须就是那一个编号。
            var shopScrollsForState = ReviveScrollTable.Load(
                shopCharConfig.ReviveScrollTable, "config/classes.json",
                shopItemsForState, shopTalismansForState);
            // 与启动、--shop-list 走同一套 Load：经验道具发货物必须就是那一个编号。
            var shopBoostersForState = ExpBoosterTable.Load(
                shopCharConfig.ExpBoosterTable, shopItemsForState,
                shopTalismansForState, shopScrollsForState);
            // 与启动、--shop-list 走同一套 Load：解绑道具发货物必须就是那一个编号。
            var shopUnbindsForState = UnbindTable.Load(
                shopCharConfig.UnbindTable, shopItemsForState,
                shopTalismansForState, shopScrollsForState, shopBoostersForState);
            var shopTableForState = ShopTable.Load(
                shopCharConfig.ShopTable, shopCurrencies, shopItemsForState, shopEnhanceForState,
                shopTalismansForState, shopScrollsForState, shopBoostersForState,
                shopUnbindsForState);

            // 券的张数与绑定态都从背包里读出来，不另算。绑定的券不能上架（P1-5a 的 bound_item）。
            var shopInventory = new InventoryRepository(dataSource);
            var shopSlots = await shopInventory.ListAsync(shopStateOf, CancellationToken.None);
            var ticketItem = shopTableForState.Items
                .FirstOrDefault(i => string.Equals(i.Kind, "protect_ticket", StringComparison.Ordinal));
            var ticketSlots = shopSlots
                .Where(s => ticketItem is not null
                    && string.Equals(s.ItemId, ticketItem.DeliveryItemId, StringComparison.Ordinal))
                .ToList();
            var ticketCount = ticketSlots.Sum(s => s.Count);
            var ticketAllBound = ticketSlots.Count > 0 && ticketSlots.All(s => s.Bound);
            var ticketSlotList = ticketSlots.Count == 0
                ? "-"
                : string.Join(",", ticketSlots.Select(s => s.Slot + ":" + s.Count));

            Console.WriteLine("shop_state=ok id=" + shopStateOf
                + " currency=" + ShopTable.WrittenCurrency
                + " balance=" + shopBalances.GetValueOrDefault(ShopTable.WrittenCurrency, 0L)
                + " balances=" + string.Join(",", shopCurrencies.All.Select(
                    c => c.Id + ":" + shopBalances.GetValueOrDefault(c.Id, 0L)))
                + " goods=" + (ticketItem?.DeliveryItemId ?? "-")
                + " tickets=" + ticketCount
                + " ticket_bound=" + (ticketAllBound ? "true" : "false")
                + " ticket_slots=" + ticketSlotList
                + " used_slots=" + shopSlots.Count);
        }

        if (shopLedgerOf != 0)
        {
            // 商城买过几笔：看事务表里这个角色名下、前缀是 shop_buy: 的行。
            var ledger = new GrantRepository(
                dataSource,
                ItemTable.Load("config/items.json"),
                CurrencyTable.Load("config/currency.json"),
                CharacterConfig.Load(configPath));
            var rows = await ledger.ShopTxnCountAsync(shopLedgerOf, CancellationToken.None);
            Console.WriteLine("shop_ledger=ok id=" + shopLedgerOf
                + " txn_prefix=shop_buy:"
                + " orders=" + rows);
        }

        if (rechargeStateOf != null)
        {
            // 从库里独立读回这张订单的状态、金额、发放时刻与余额。
            var rechargeRepo = new RechargeRepository(dataSource);
            var order = await rechargeRepo.StateAsync(rechargeStateOf, CancellationToken.None);
            Console.WriteLine(order.Found
                ? "recharge_state=ok order=" + order.OrderId
                  + " id=" + order.CharacterId
                  + " product=" + order.ProductId
                  + " yuanbao=" + order.Yuanbao
                  + " status=" + order.Status
                  + " paid=" + (order.Paid ? "true" : "false")
                  + " paid_at_ms=" + order.PaidAtMs
                  + " now_ms=" + order.NowMs
                  + " balance=" + order.Balance
                : "recharge_state=missing order=" + rechargeStateOf);
        }

        // P3-4a2：通行证。三条入口共用同一份配置加载与同一个仓储：
        // state 只读；买与领各一笔事务——和网关那条路走的是同一段仓储代码。
        if (seasonPassStateOf != 0 || buySeasonPassOf != 0 || claimSeasonPassOf != 0)
        {
            var passConfig = CharacterConfig.Load(configPath);
            var (passTable, passItems) = LoadSeasonPass(passConfig, seasonPassPath);
            var passSeason = (seasonPassSeason ?? "").Trim();
            // Q6/J4：领取门槛要读本季点数，进度表与真实启动共用同一条加载链。
            var passProgress = LoadSeasonPassProgress(passConfig, null);
            var seasonPassRepo = new SeasonPassRepository(
                dataSource, passTable, passItems, passConfig, passProgress);

            if (seasonPassStateOf != 0)
            {
                var state = await seasonPassRepo.StateAsync(
                    seasonPassStateOf, passSeason, CancellationToken.None);
                Console.WriteLine("season_pass_state=ok id=" + seasonPassStateOf
                    + " season=" + state.SeasonId
                    + " paid=" + (state.Paid ? "true" : "false")
                    + " claims=" + state.Claims.Count
                    + " claimed=" + (state.Claims.Count == 0
                        ? "-"
                        : string.Join(",", state.Claims.Select(
                            c => c.Track + ":" + c.Stage + ":" + c.ItemId + "x" + c.Count))));

                // 包里那几件通行证奖励：编号、数量、绑定态都从库里读，不另算。
                var passDef = passTable.Find(state.SeasonId);
                var rewardIds = new HashSet<string>(StringComparer.Ordinal);
                if (passDef is not null)
                {
                    foreach (var stage in passDef.Free)
                    {
                        rewardIds.Add(stage.Rewards[0].ItemId);
                    }

                    foreach (var stage in passDef.Paid)
                    {
                        rewardIds.Add(stage.Rewards[0].ItemId);
                    }
                }

                var passInventory = new InventoryRepository(dataSource);
                var passSlots = await passInventory.ListAsync(
                    seasonPassStateOf, CancellationToken.None);
                var held = passSlots.Where(x => rewardIds.Contains(x.ItemId)).ToList();
                Console.WriteLine("season_pass_bag=" + (held.Count == 0
                    ? "-"
                    : string.Join(",", held.Select(
                        x => x.ItemId + "x" + x.Count + (x.Bound ? "@bound" : "@free")
                            + ":slot" + x.Slot))));
            }

            if (buySeasonPassOf != 0)
            {
                var outcome = await seasonPassRepo.BuyAsync(
                    buySeasonPassOf, passSeason, buySeasonPassTxn ?? "", CancellationToken.None);
                Console.WriteLine("season_pass_buy=done id=" + buySeasonPassOf
                    + " season=" + outcome.SeasonId
                    + " ok=" + (outcome.Ok ? "true" : "false")
                    + " reason=" + outcome.Reason
                    + " price=" + outcome.Price
                    + " balance=" + outcome.Balance
                    + " paid=" + (outcome.Paid ? "true" : "false")
                    + " duplicate=" + (outcome.Duplicate ? "true" : "false"));
            }

            if (claimSeasonPassOf != 0)
            {
                var outcome = await seasonPassRepo.ClaimAsync(
                    claimSeasonPassOf, passSeason, claimSeasonPassTrack ?? "",
                    claimSeasonPassStage, claimSeasonPassTxn ?? "", CancellationToken.None);
                Console.WriteLine("season_pass_claim=done id=" + claimSeasonPassOf
                    + " season=" + outcome.SeasonId
                    + " track=" + outcome.Track
                    + " stage=" + outcome.Stage
                    + " ok=" + (outcome.Ok ? "true" : "false")
                    + " reason=" + outcome.Reason
                    + " item=" + outcome.ItemId
                    + " count=" + outcome.Count
                    + " slot=" + outcome.Slot
                    + " duplicate=" + (outcome.Duplicate ? "true" : "false")
                    + " claimed=" + (outcome.Claimed ? "true" : "false"));
            }
        }

        // B7+B8：背包扩容现状。只读：基础格 / 步长 / 上限 / 买过几次 / 现在几格。
        // 不读 shop.json——这里的 product 只作展示，核对看的是容量算法与库里的次数。
        if (bagExpandStateOf != 0)
        {
            var bagConfig = CharacterConfig.Load(configPath);
            var bagRepo = new BagExpandRepository(dataSource, bagConfig, "");
            var state = await bagRepo.DescribeAsync(bagExpandStateOf, CancellationToken.None);
            Console.WriteLine("bag_expand_state=ok id=" + state.CharacterId
                + " found=" + (state.Found ? "true" : "false")
                + " base_slots=" + state.BaseSlots
                + " step=" + state.Step
                + " max_slots=" + state.MaxSlots
                + " count=" + state.Count
                + " slots=" + state.Slots);
        }

        // P3-4b：第二角色栏与外观收藏。名额的事实值在库里那一行（没有就按配置的基础位），
        // 外观收藏按账号把名下所有角色持有的外观类物品取并集——三个入口都只读，
        // 只有 --raw-grant-character-slot 会写，而它只给种子 / 核对用，不扣钱也不推进产品路径。
        if (characterSlotsOf != null || wardrobeOf != null || rawSlotAccount != null)
        {
            var slotConfig = CharacterConfig.Load(configPath);
            var slotTable = LoadCharacterSlot(slotConfig, characterSlotPath);
            var slotRepo = new CharacterSlotRepository(dataSource, slotTable);
            var wardrobeRepo = new WardrobeRepository(
                dataSource, ItemTable.Load("config/items.json"));

            if (rawSlotAccount != null)
            {
                var granted = await slotRepo.RawGrantAsync(
                    rawSlotAccount, rawSlotValue, "raw-slot-grant", CancellationToken.None);
                Console.WriteLine("character_slot_raw=done account=" + rawSlotAccount
                    + " slots=" + rawSlotValue
                    + " ok=" + (granted ? "true" : "false"));
            }

            if (characterSlotsOf != null)
            {
                var state = await slotRepo.DescribeAsync(characterSlotsOf, CancellationToken.None);
                Console.WriteLine("character_slots=ok account=" + state.Account
                    + " found=" + (state.Found ? "true" : "false")
                    + " base_slots=" + state.BaseSlots
                    + " max_slots=" + state.MaxSlots
                    + " slots=" + state.Slots
                    + " used=" + state.Used
                    + " product=" + state.ProductId
                    + " price=" + state.PriceYuanbao);
            }

            if (wardrobeOf != null)
            {
                var (found, entries) = await wardrobeRepo.DescribeAsync(
                    wardrobeOf, CancellationToken.None);
                Console.WriteLine("wardrobe=ok account=" + wardrobeOf
                    + " found=" + (found ? "true" : "false")
                    + " entries=" + entries.Count
                    + " items=" + (entries.Count == 0
                        ? "-"
                        : string.Join(",", entries.Select(e => e.ItemId)))
                    + " owners=" + (entries.Count == 0
                        ? "-"
                        : string.Join(",", entries.Select(
                            e => e.ItemId + ":" + string.Join("+", e.CharacterIds)))));
            }
        }

        return null;
    }

}