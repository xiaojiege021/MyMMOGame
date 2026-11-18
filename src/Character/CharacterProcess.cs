using System.Net;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

public sealed class CharacterProcess(string configPath)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        CharacterConfig config;
        try
        {
            config = CharacterConfig.Load(configPath);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        // 三张表：出身、分支、职业编号。种族与分支都不带属性，只决定外观、对白与兵器。
        RaceTable races;
        BranchTable branches;
        ClassIds classes;
        ItemTable items;
        CurrencyTable currencies;
        DungeonTable dungeons;
        RealmTable realms;
        ExpTable expTable;
        EnhanceTable enhanceTable;
        TransferTable transferTable;
        CrystalTable crystalTable;
        AuctionTable auctionTable;
        ShopTable shopTable;
        HomeCoinShopTable homeCoinShopTable;
        TalismanTable talismanTable;
        ReviveScrollTable scrollTable;
        ExpBoosterTable boosterTable;
        UnbindTable unbindTable;
        RechargeTable rechargeTable;
        StoneExchangeTable stoneExchange;
        CityTable cityTable;
        SiegeTerritoryPolicy siegeTerritoryPolicy;
        SeasonPassTable seasonPassTable;
        MonthlyBundleTable monthlyBundleTable;
        CharacterSlotTable characterSlotTable;
        MainlineTable mainlineTable;
        HeroCodexTable heroCodexTable;
        CalendarTable calendar;
        RecipeTable recipeTable;
        CityTaskTable cityTaskTable;
        SeasonPassProgressTable seasonPassProgressTable;
        NameRulesTable nameRulesTable;
        DungeonDropTable dungeonDropTable;
        MainlineDropTable mainlineDropTable;
        try
        {
            // Q3/G1：日 / 周 / 赛季边界只从这一份日历表算，先于所有仓储加载。
            calendar = CalendarTable.Load(config.CalendarTable);
            races = RaceTable.Load("config/races.json");
            branches = BranchTable.Load("config/branches.json");
            classes = new ClassIds("config/classes.json");
            items = ItemTable.Load("config/items.json");
            // Q12/K3：名字规则。长度、唯一、改名与敏感词表路径都在这份文件里；
            // rename_item_id 必须在上面这份物品表里——加载期就挡住「配了一个不存在的卡」。
            nameRulesTable = NameRulesTable.Load(config.NameRulesTable, items);
            currencies = CurrencyTable.Load("config/currency.json");
            dungeons = DungeonTable.Load(config.DungeonTable);
            // P3-1a1：十一段按顺序开放，首发到结丹；元婴起每段一个开关。
            // 与副本表交叉校验：写 realm 的只能是首发之后的新副本，旧副本不许写。
            realms = RealmTable.Load(config.RealmTable, dungeons, currencies);
            expTable = ExpTable.Load(config.ExpTable);
            // 开发架构第 7 节：强化概率由角色服读。三档材料要在物品表里存在，
            // 扣的金币要在币种表里存在，缺一样就起不来。
            enhanceTable = EnhanceTable.Load(config.EnhanceTable, items, currencies);
            // 方案 9.3：转移石要在物品表里、费用表要照写定的上限与递增关系，
            // 缺一样就起不来。和强化表一样只读一次，改配置重启生效。
            transferTable = TransferTable.Load(config.TransferTable, items, currencies, enhanceTable);
            // 方案第 9 章：三类、部位限定与可拆卸复用由配置校验；装备部位漏配、
            // 类别被改、结晶不在物品表里，缺一样就起不来。改配置重启生效。
            crystalTable = CrystalTable.Load(config.CrystalTable, items);
            // 方案第 12 章写定的 5%–8% 抽税区间由 AuctionTable 硬校验，
            // 具体税率与取整方式在 config/auction.json，改配置重启生效。
            auctionTable = AuctionTable.Load(config.AuctionTable, currencies);
            // 方案 12.1：红符是绑定的生命回复消耗，回复不超过同级红药，
            // 和红药、牧师治疗共用冷却。回复值与上限都在 config/talisman.json，
            // 超了、物品不在表里、没到账即绑，缺一样就起不来。
            talismanTable = TalismanTable.Load(config.TalismanTable, items);
            // 方案 12.1：复活卷轴只拉起自己，冷却跨场保留。编号与冷却时长在
            // revive_scroll.json；拉起血量比例与经验比例读同一份 classes.json
            // （一转基础，生息与卷轴同源，二转减免只给生息），不在这里抄数。
            scrollTable = ReviveScrollTable.Load(
                config.ReviveScrollTable, "config/classes.json", items, talismanTable);
            // 方案 10 章 / 12.1：限时经验道具只乘结算这一笔，时长与倍率在
            // exp_booster.json；到期时刻落角色库，不在这里生成，也不在内存计时。
            // 与符、卷轴的编号不许撞（三种是三种东西）。
            boosterTable = ExpBoosterTable.Load(
                config.ExpBoosterTable, items, talismanTable, scrollTable);
            // 方案 12.1：解绑道具购买后绑定，只解开装备；绑定越久张数越少、下限走配置。
            // 档位与张数在 unbind_item.json；绑定状态仍是 inventory 的 bound / bound_at，
            // 不在这里另存一份。与符、卷轴、经验道具的编号不许撞（四种是四种东西）。
            unbindTable = UnbindTable.Load(
                config.UnbindTable, items, talismanTable, scrollTable, boosterTable);
            // P1-5d：内部充值商品表。P0 不接真实支付渠道，订单与回调都在本服务；
            // 元宝数只在这份配置里，一笔订单只发一次。
            rechargeTable = RechargeTable.Load(config.RechargeTable);
            // 方案 12.1 定死十八种可上架种类，商城只收元宝；清单外的编号在购买时拒绝。
            // P1-5c2 起逐种接发货：已接的种类必须指向 items.json 里的真物品，
            // 保护券还必须就是 enhance.json 那一个编号（两处发的是同一种券），
            // 红符必须就是 talisman.json 那一个编号（红符不是红药）。
            shopTable = ShopTable.Load(
                config.ShopTable, currencies, items, enhanceTable, talismanTable,
                scrollTable, boosterTable, unbindTable);
            homeCoinShopTable = HomeCoinShopTable.Load(
                "config/home_coin_shop.json", currencies, items);
            // P3-2a：灵石五档兑换表与城池表。与启动共用同一套 Load：
            // 档位都要在币种表里、最高一档的中文名要与币种表一致；
            // 城池的 vein_id 要对应 gather.json 里的 spirit_stone 节点。
            // 缺一样就起不来，不带着半截配置跑。
            stoneExchange = StoneExchangeTable.Load(config.StoneExchangeTable, currencies);
            cityTable = CityTable.Load(config.CityTable);
            // P3-3a4：领地表与城池表是同一份正本，城池编号交叉校验，抽成比例只从这里读。
            // 城战胜负写回时角色服不另算税率，也不接受请求里带来的税率。
            siegeTerritoryPolicy = SiegeTerritoryPolicy.Load(
                config.SiegeTerritoryTable, cityTable);
            // P3-4a2：通行证两轨。免费轨只许发称号与外观碎片，付费轨只许发当季外观、
            // 洞府摆件与保护券；保护券必须就是 enhance.json 那一件（商城卖的同一种），
            // 四种奖励都要在 items.json 里且到账即绑。season.json 只用来核对季真的存在。
            seasonPassTable = SeasonPassTable.Load(
                config.SeasonPassTable, File.ReadAllText(config.SeasonTable),
                shopTable, items, enhanceTable);
            // P3-4b：月叠只许发传送符与红符（三十天份），两种编号分别等于商城那条传送符
            // 与 talisman.json 那一件；蓝符 / 卷轴 / 经验道具 / 金币材料在加载期就长不出来。
            monthlyBundleTable = MonthlyBundleTable.Load(
                config.MonthlyBundleTable, shopTable, items, talismanTable);
            // P3-4b：第二角色栏只改「一个账号能建几个角色」。基础位 1、上限 2（方案只写了
            // 「第二」这一种），商品与标价在 shop.json；装备 / 等级 / 境界仍按角色各存一行。
            characterSlotTable = CharacterSlotTable.Load(config.CharacterSlotTable, shopTable);
            // C1：主线十二场的内容正本。只加内容和配置：加载期与 dungeons.json 的 main
            // （1 人 / 不扣疲劳 / 不占份额）、realms.json 的首发三段交叉校验。
            // 老卡的临时配置目录没有这份表时降级成「没有主线内容」，不挡启动。
            mainlineTable = File.Exists(config.MainlineTable)
                ? MainlineTable.Load(config.MainlineTable, dungeons, realms)
                : MainlineTable.Missing();
            heroCodexTable = HeroCodexTable.Load("config/hero_codex.json", mainlineTable);
            // K3：生产 / 配方的唯一正本。四个副职、每条配方的材料 / 产出 / 费用 /
            // 成功率 / 耗时 / 解锁等级都在这一份文件里；加一个配方不改代码。
            recipeTable = RecipeTable.Load(config.RecipeTable, items, currencies);
            // Q5/J2：主城修炼 / 境界任务的正本。与启动共用同一份 Load：城池编号必须在
            // config/city.json 里、奖励只能是灵石五档、境界门槛必须在 realms.json 的顺序表里；
            // 缺表降级成「没有任务」，老卡环境照常起。
            cityTaskTable = File.Exists(config.CityTaskTable)
                ? CityTaskTable.Load(config.CityTaskTable, cityTable, stoneExchange, realms)
                : CityTaskTable.Empty;
            // Q6/J4：通行证点数与来源的正本。副本来源必须真的在 dungeons.json 里，
            // daily / weekly 必须有次数上限；缺表降级成「没有来源」，老卡环境照常起。
            seasonPassProgressTable = File.Exists(config.SeasonPassProgressTable)
                ? SeasonPassProgressTable.Load(config.SeasonPassProgressTable, dungeons)
                : SeasonPassProgressTable.Missing();
            var dungeonDropPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(configPath))!, "dungeon_drops.json"));
            dungeonDropTable = DungeonDropTable.LoadIfPresent(
                dungeonDropPath,
                items.All.Select(item => item.Id),
                currencies.All.Select(currency => currency.Id));
            var mainlineDropPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(configPath))!, "mainline_drops.json"));
            mainlineDropTable = MainlineDropTable.LoadIfPresent(mainlineDropPath, items, currencies);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        Console.WriteLine("config=" + configPath);
        Console.WriteLine("listen=" + config.Listen);
        // Q3/G1：启动时把这一份日历的日 / 周 / 赛季边界打出来，改时区或重置小时后
        // 一眼能确认跑的是哪一套时钟；判定仍在每一笔事务里重算，不缓存。
        var bootNow = DateTimeOffset.UtcNow;
        var bootSeason = calendar.SeasonOf(bootNow);
        Console.WriteLine("calendar=ok version=" + calendar.Version
            + " timezone=" + calendar.TimeZoneId
            + " daily_reset_hour=" + calendar.DailyResetHour
            + " weekly_reset=" + calendar.WeeklyResetDay.ToString().ToLowerInvariant()
            + "@" + calendar.WeeklyResetHour
            + " season_start=" + calendar.SeasonStartDate.ToString("yyyy-MM-dd")
            + " season_length_days=" + calendar.SeasonLengthDays
            + " day=" + calendar.DayKey(bootNow).ToString("yyyy-MM-dd")
            + " week_start=" + calendar.WeekStartDate(bootNow).ToString("yyyy-MM-dd")
            + " season_index=" + bootSeason.Index);
        Console.WriteLine("social_upstream=" + config.SocialUpstream);
        Console.WriteLine("global_upstream=" + config.GlobalUpstream);
        Console.WriteLine("races=" + races.All.Count + " race_table_version=" + races.Version);
        Console.WriteLine("branches=" + branches.All.Count + " branch_table_version=" + branches.Version);
        Console.WriteLine("items=" + items.All.Count + " item_table_version=" + items.Version
            + " bag_slots=" + config.BagSlots);
        Console.WriteLine("currencies=" + currencies.All.Count + " currency_table_version=" + currencies.Version);
        Console.WriteLine("dungeons=" + dungeons.All.Count + " dungeon_table_version=" + dungeons.Version
            + " dungeons_version=" + dungeons.Version
            + " fatigue_daily_cap=" + dungeons.FatigueDailyCap
            + " hot_spring_recovery=" + dungeons.HotSpringRecovery
            + " weekly_share_cap=" + dungeons.WeeklyShareCap
            + " monster_hp_scale_per_player=" + dungeons.MonsterHpScalePerPlayer
            + " monster_damage_scale_per_player=" + dungeons.MonsterDamageScalePerPlayer);
        // 逐条列出人数区间与「每周次数 / 每人份数」，高难本的规则改一条重启后能直接核对到。
        foreach (var dungeon in dungeons.All)
        {
            Console.WriteLine("dungeon=" + dungeon.Id
                + " tier=" + dungeon.Tier
                + " min=" + dungeon.MinPlayers
                + " max=" + dungeon.MaxPlayers
                + " entries=" + dungeon.EntriesPerWeek
                + " reward=" + dungeon.RewardPerPlayer
                + " drill=" + dungeon.Drill);
        }
        // P3-1a1：十一段顺序与开关。改 realms.json 重启后这里能直接核对到。
        Console.WriteLine("realms=" + realms.Realms.Count
            + " realm_table_version=" + realms.Version
            + " launch_until=" + realms.LaunchUntil
            + " open_realms=" + string.Join(",", realms.Realms.Where(realms.IsOpen))
            + " closed_realms=" + string.Join(",", realms.Realms.Where(r => !realms.IsOpen(r))));
        foreach (var dungeon in dungeons.All.Where(d => d.Realm is not null))
        {
            Console.WriteLine("dungeon_realm=" + dungeon.Id + " realm=" + dungeon.Realm);
        }

        // P3-1a2：每一段的突破要求。改 realms.json 重启后这里能直接核对到。
        foreach (var requirement in realms.Requirements)
        {
            Console.WriteLine("realm_advance=" + requirement.From + "->" + requirement.Next
                + " drill=" + requirement.Drill
                + " stone=" + requirement.Stone
                + " stone_cost=" + requirement.StoneCost
                + " success_pct=" + requirement.SuccessPct
                + " main_stage=" + requirement.MainStage
                + " side_quests=" + string.Join("+", requirement.SideQuests));
        }
        Console.WriteLine("exp_table_version=" + expTable.Version
            + " level_cap=" + expTable.LevelCap
            + " base_exp=" + expTable.BaseExp
            + " party_bonus_pct=" + expTable.PartyBonusPct
            + " party_min_size=" + expTable.PartyMinSize
            + " exp_per_level=" + expTable.ExpPerLevel);
        Console.WriteLine("enhance_table_version=" + enhanceTable.Version
            + " max_level=" + enhanceTable.MaxLevel
            + " gold_kind=" + enhanceTable.GoldKind
            + " tiers=" + string.Join(",",
                enhanceTable.Tiers.Select(t => t.Id + ":" + t.FromLevel + "-" + t.ToLevel
                    + ":" + t.StoneId + ":rate=" + t.SuccessRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + ":gold=" + t.GoldCost + ":stone=" + t.StoneCost))
            + " rates=" + string.Join(",", enhanceTable.Tiers.Select(
                t => t.SuccessRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))
            + " coupon=" + enhanceTable.ProtectItemId
            + " coupon_bind=always");
        // 与 --transfer-state 只读入口同形，改金币费用重启后能直接核对到。
        Console.WriteLine("transfer_table_version=" + transferTable.Version
            + " transfer_stone=" + transferTable.StoneItemId
            + " transfer_stone_cost=" + transferTable.StoneCost
            + " transfer_gold_kind=" + transferTable.GoldKind
            + " transfer_gold=" + string.Join(",", transferTable.GoldCosts));
        // 与 --crystal-state 只读入口同形，改部位映射或镶嵌位数量重启后能直接核对到。
        Console.WriteLine("crystal_table_version=" + crystalTable.Version
            + " crystal_sockets=" + crystalTable.SocketCount
            + " crystal_categories=" + string.Join(",", CrystalTable.WrittenCategories)
            + " crystal_parts=" + string.Join(",", crystalTable.EquipmentParts
                .OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value))
            + " crystal_allowed=" + string.Join(",", CrystalTable.WrittenCategories
                .Select(c => c + ":" + string.Join("+", crystalTable.CategoryParts[c])))
            + " crystal_items=" + string.Join(",", crystalTable.Crystals
                .OrderBy(c => c.Id).Select(c => c.Id + ":" + c.Category + ":" + c.FixedValue)));
        // 与 --auction-state 只读入口同形，改税率重启后能直接核对到。
        Console.WriteLine("auction_table_version=" + auctionTable.Version
            + " tax_rate_pct=" + auctionTable.TaxRatePct
            + " tax_min=" + AuctionTable.WrittenMinRatePct
            + " tax_max=" + AuctionTable.WrittenMaxRatePct
            + " gold_kind=" + auctionTable.GoldKind
            + " rounding=" + auctionTable.Rounding
            + " ingot_kind=" + auctionTable.IngotKind);
        // 与 --shop-list 只读入口同形：清单改一条，重启后这里能直接核对到。
        Console.WriteLine("shop_table_version=" + shopTable.Version
            + " currency=" + shopTable.Currency
            + " items=" + shopTable.Items.Count
            + " kinds=" + shopTable.Categories.Count
            + " delivered_kinds=" + shopTable.DeliveredCount);
        // 与 --talisman-state 只读入口同形：回复值改一条，重启后这里能直接核对到。
        Console.WriteLine("talisman_table_version=" + talismanTable.Version
            + " red_item=" + (talismanTable.RedTalisman?.ItemId ?? "")
            + " hp_restore=" + (talismanTable.RedTalisman?.HpRestore ?? 0)
            + " potion_hp_restore=" + (talismanTable.RedTalisman?.PotionHpRestore ?? 0)
            + " cooldown_group=" + (talismanTable.RedTalisman?.CooldownGroup ?? "")
            + " cooldown_ms=" + (talismanTable.RedTalisman?.CooldownMs ?? 0)
            + " blue_item=" + (talismanTable.BlueTalisman?.ItemId ?? "")
            + " mana_restore=" + (talismanTable.BlueTalisman?.ManaRestore ?? 0)
            + " potion_mana_restore=" + (talismanTable.BlueTalisman?.PotionManaRestore ?? 0)
            + " blue_group=" + (talismanTable.BlueTalisman?.CooldownGroup ?? "")
            + " blue_cooldown_ms=" + (talismanTable.BlueTalisman?.CooldownMs ?? 0));
        // 与 --revive-scroll-list 只读入口同形：冷却时长、两个比例改一条，
        // 重启后这里能直接核对到；冷却到期时刻不在这里，在角色库那一行。
        Console.WriteLine("revive_scroll_table_version=" + scrollTable.Version
            + " item=" + scrollTable.ReviveScroll.ItemId
            + " cooldown_ms=" + scrollTable.ReviveScroll.CooldownMs
            + " hp_restore_pct=" + scrollTable.HpRestorePct
            + " exp_cost_pct=" + scrollTable.ExpCostPct);
        // 与 --exp-booster-list 只读入口同形：时长、倍率改一条，重启后这里能直接核对到。
        // 到期时刻不在这里，在角色库那一行（--exp-booster-state 读）。
        Console.WriteLine("recharge_table_version=" + rechargeTable.Version
            + " products=" + rechargeTable.Products.Count);
        Console.WriteLine("unbind_table_version=" + unbindTable.Version
            + " item=" + unbindTable.Unbind.ItemId
            + " tiers=" + unbindTable.Unbind.Tiers.Count
            + " floor=" + unbindTable.FloorCost);
        Console.WriteLine("exp_booster_table_version=" + boosterTable.Version
            + " item=" + boosterTable.ExpBooster.ItemId
            + " duration_ms=" + boosterTable.ExpBooster.DurationMs
            + " gain_pct=" + boosterTable.ExpBooster.GainPct);
        // P3-2a：灵石五档与比例只在配置里，启动日志能核对到全部事实值。
        Console.WriteLine("stone_exchange=tiers=" + stoneExchange.Tiers.Count
            + " ratio=" + stoneExchange.Ratio
            + " gain=" + stoneExchange.Gain
            + " highest=" + stoneExchange.HighestKind + "/" + stoneExchange.HighestName
            + " ingot=" + stoneExchange.IngotKind
            + " version=" + stoneExchange.Version);
        // Q6/J4：通行证进度的事实值。每阶点数与来源全在 config/season_pass_progress.json，
        // 与 --pass-progress-config 的只读入口同一个键名、同一组字段。
        Console.WriteLine("season_pass_progress=version=" + seasonPassProgressTable.Version
            + " pending=" + (seasonPassProgressTable.Pending ? "true" : "false")
            + " points_per_stage=" + seasonPassProgressTable.PointsPerStage
            + " sources=" + seasonPassProgressTable.Sources.Count);
        foreach (var source in seasonPassProgressTable.Sources)
        {
            Console.WriteLine("season_pass_source=" + source.Id
                + " kind=" + source.Kind
                + " dungeon=" + (source.DungeonId.Length == 0 ? "-" : source.DungeonId)
                + " points=" + source.Points
                + " period=" + source.Period
                + " max=" + source.MaxPerPeriod);
        }

        // Q12/K3：名字规则的事实值。长度、唯一、改名开关、改名卡编号与敏感词条数
        // 都能在这里一次看全；与 --name-rules 的只读入口同一组字段。
        Console.WriteLine("name_rules=version=" + nameRulesTable.Version
            + " global_unique=" + (nameRulesTable.GlobalUnique ? "true" : "false")
            + " allow_rename=" + (nameRulesTable.AllowRename ? "true" : "false")
            + " min_length=" + nameRulesTable.MinLength
            + " max_length=" + nameRulesTable.MaxLength
            + " rename_item_id=" + nameRulesTable.RenameItemId
            + " banned_words=" + nameRulesTable.BannedCount
            + " banned_words_file=" + nameRulesTable.BannedWordsFile);

        // P3-4a2：通行证两轨的事实值。改配置重启后这里能直接核对到：
        // 标价来自 shop.json 那条 season_pass 商品，保护券就是 enhance.json 那一件。
        Console.WriteLine("season_pass=version=" + seasonPassTable.Version
            + " pending=" + (seasonPassTable.Pending ? "true" : "false")
            + " seasons=" + seasonPassTable.Seasons.Count
            + " product=" + seasonPassTable.ProductId
            + " price=" + seasonPassTable.PriceYuanbao
            + " protect=" + seasonPassTable.ProtectItemId
            + " protect_per_stage_max=" + seasonPassTable.ProtectPerStageMax);
        foreach (var pass in seasonPassTable.Seasons)
        {
            Console.WriteLine("season_pass_season=" + pass.SeasonId
                + " free=" + pass.Free.Count
                + " paid=" + pass.Paid.Count
                + " free_items=" + string.Join("+", pass.Free.Select(x => x.Rewards[0].ItemId))
                + " paid_items=" + string.Join("+", pass.Paid.Select(x => x.Rewards[0].ItemId)));
        }

        // P3-4b：月叠的事实值。改配置重启后这里能直接核对到：只两种符、每天几张、
        // 共几天、总张数，价格来自 shop.json 那条 monthly_bundle 商品。
        Console.WriteLine("monthly_bundle=version=" + monthlyBundleTable.Version
            + " pending=" + (monthlyBundleTable.Pending ? "true" : "false")
            + " product=" + monthlyBundleTable.ProductId
            + " price=" + monthlyBundleTable.PriceYuanbao
            + " days=" + monthlyBundleTable.Days
            + " kinds=" + monthlyBundleTable.Entries.Count);
        foreach (var entry in monthlyBundleTable.Entries)
        {
            Console.WriteLine("monthly_bundle_item=" + entry.Kind
                + " item=" + entry.ItemId
                + " per_day=" + entry.PerDay
                + " count=" + entry.Count
                + " bind=always");
        }

        // P3-4b：第二角色栏的事实值。基础位与上限只在这里，名额的事实值在库里那一行。
        Console.WriteLine("character_slot=version=" + characterSlotTable.Version
            + " pending=" + (characterSlotTable.Pending ? "true" : "false")
            + " product=" + characterSlotTable.ProductId
            + " price=" + characterSlotTable.PriceYuanbao
            + " base_slots=" + characterSlotTable.BaseSlots
            + " max_slots=" + characterSlotTable.MaxSlots
            + " table=character_slot");

        // C1：十二场内容只在这里列一遍事实值，定词正文仍只在 config/dialogue。
        Console.WriteLine("mainline_config=" + (mainlineTable.IsMissing ? "missing" : "ok")
            + " stages=" + mainlineTable.Stages.Count
            + " pending=" + (mainlineTable.Pending ? "true" : "false")
            + " battle_dungeon=" + mainlineTable.BattleDungeon);
        Console.WriteLine("recipe_table_version=" + recipeTable.Version
            + " recipes=" + recipeTable.Recipes.Count
            + " professions=" + recipeTable.Professions.Count
            + " pending=" + (recipeTable.Pending ? "true" : "false"));
        foreach (var profession in recipeTable.Professions)
        {
            Console.WriteLine("recipe_profession=" + profession.Id
                + " name=" + profession.Name
                + " recipes=" + recipeTable.ForProfession(profession.Id).Count);
        }
        // Q5/J2：城池任务表的事实值。数值全在 config/city_tasks.json，这里只列出来，
        // 与 --city-task-config 的只读入口同一个键名、同一组字段。
        Console.WriteLine("city_task_config=" + (cityTaskTable.Tasks.Count == 0 ? "missing" : "ok")
            + " version=" + cityTaskTable.Version
            + " pending=" + (cityTaskTable.Pending ? "true" : "false")
            + " tasks=" + cityTaskTable.Tasks.Count
            + " stone_tiers=" + string.Join("+", cityTaskTable.Tasks
                .SelectMany(t => t.Rewards).Select(r => r.Currency).Distinct()));
        foreach (var task in cityTaskTable.Tasks)
        {
            Console.WriteLine("city_task_row=" + task.Id
                + " city=" + task.CityId
                + " period=" + task.Period
                + " max=" + task.MaxPerPeriod
                + " level=" + task.RequireLevel
                + " realm=" + (task.RequireRealm.Length == 0 ? "-" : task.RequireRealm)
                + " rewards=" + string.Join("+", task.Rewards.Select(r => r.Currency + ":" + r.Amount)));
        }

        Console.WriteLine("hero_codex=version=" + heroCodexTable.Version
            + " pending=" + (heroCodexTable.Pending ? "true" : "false")
            + " kind=" + heroCodexTable.PassiveKind
            + " entries=" + heroCodexTable.Entries.Count
            + " max_total_passive_pct_bps=" + heroCodexTable.MaxTotalPassivePctBps);
        foreach (var stage in mainlineTable.Stages)
        {
            Console.WriteLine("mainline_stage=" + stage.Stage
                + " title=" + stage.Title
                + " level_min=" + stage.LevelMin
                + " requires_breakthrough=" + stage.RequiresBreakthrough
                + " alignment_choice=" + (stage.AlignmentChoice ? "true" : "false")
                + " battle=" + stage.Battle);
        }

        Console.WriteLine("dungeon_drops=" + (dungeonDropTable.IsMissing ? "missing" : "ok")
            + " version=" + dungeonDropTable.Version
            + " dungeons=" + dungeonDropTable.DungeonCount);
        Console.WriteLine("mainline_drops=" + (mainlineDropTable.IsMissing ? "missing" : "ok")
            + " version=" + mainlineDropTable.Version
            + " stages=" + mainlineDropTable.StageCount
            + " pending=" + (mainlineDropTable.Pending ? "true" : "false"));

        // 城池只登记静态定义（编号 / 名字 / 矿脉）；归属与税率是库里的运行时事实（技术架构 6.1）。
        // 城池税收账本与拍卖销毁税是分开的两张表。
        Console.WriteLine("task_grant_city=" + cityTable.TaskGrantCityId);
        foreach (var city in cityTable.Cities)
        {
            Console.WriteLine("city=" + city.CityId
                + " vein=" + city.VeinId
                + " owner_sect=db"
                + " tax_pct=db"
                + " city_tax_table=社交.city_tax_ledger"
                + " auction_tax_table=交易.auction_settlement.tax_gold");
        }

        var connectionString = DbEnv.Resolve();
        Console.WriteLine("db_env=" + DbEnv.Source());

        if (connectionString == null)
        {
            // 角色数据没有库就没法建号，这里直接不起来，免得看起来在跑其实什么都做不了。
            Console.WriteLine("db_missing=1 需要 MMO_GAMEDB 或 " + DbEnv.DefaultEnvFile);
            return 3;
        }

        await using var dataSource = NpgsqlDataSource.Create(DbEnv.WithPoolLimit(connectionString, config.DbPoolMax));

        // P3-3a4：config/city.json 是城池的正本，库里这一行是运行时事实。
        // P3-2a 只建了表、没写城池行，城战写回的 UPDATE 会找不到行，所以启动时
        // 把配置里登记的城池补进库；已存在的行（含城战写回过的 owner / tax_pct）
        // 一律不覆盖，免得每次重启把归属改回空。只跑分表迁移的老卡环境可能还没有
        // 这张表，那就跳过，不让启动被拖垮。
        // K7：只插静态三列，owner_sect / tax_pct 走库默认（null / 0），配置不再带这两个值。
        try
        {
            foreach (var city in cityTable.Cities)
            {
                await using var seed = dataSource.CreateCommand(
                    "insert into \"社交\".city (city_id, name, vein_id) "
                    + "values ($1, $2, $3) on conflict (city_id) do nothing");
                seed.Parameters.AddWithValue(city.CityId);
                seed.Parameters.AddWithValue(city.Name);
                seed.Parameters.AddWithValue(city.VeinId);
                await seed.ExecuteNonQueryAsync(stopping);
            }
            Console.WriteLine("city_seed=ok cities=" + cityTable.Cities.Count);
        }
        catch (NpgsqlException ex)
        {
            Console.WriteLine("city_seed=skipped " + ex.GetType().Name);
        }

        var characters = new CharacterRepository(dataSource);
        var inventory = new InventoryRepository(dataSource);
        var currency = new CurrencyRepository(dataSource);
        var cityTax = new CityTaxRepository(dataSource, cityTable, auctionTable.GoldKind);
        var grants = new GrantRepository(dataSource, items, currencies, config, cityTax);
        // K3：制作的两笔事务（start 扣材料 / 费用、finish 掷点入包）都在这一个仓储里；
        // 产出入包复用 grants 的同一条 SQL，钱只用 grants 的货币写法。
        var crafts = new CraftRepository(
            dataSource, recipeTable, items, config, grants, inventory);
        // Q5/J2：城池任务。钱复用 grants 的同一条货币写法，抽成复用 cityTax 的账本写法，
        // 周期键只读 calendar；本卡不另开第二条货币 UPDATE，也不另算一套抽成。
        var cityTasks = new CityTaskRepository(
            dataSource, cityTaskTable, calendar, realms, grants, cityTax);
        // Q9/H4：逃跑惩罚的唯一写入口（写到期时刻，不扣任何东西）。
        var deserters = new DeserterRepository(dataSource);
        var dungeonEntries = new DungeonEntryRepository(dataSource, calendar);
        // P2-2a：高难本（龙巢）每人每周的次数。只写 dungeon_entry 一行，
        // 不碰疲劳与周份额；次数以库里已写下的进本记录为准。
        var hardEntries = new HardEntryRepository(dataSource, calendar);
        var weeklyShares = new WeeklyShareRepository(dataSource, calendar);
        var hotSpring = new HotSpringRepository(dataSource, calendar);
        var expSettlements = new ExpSettlementRepository(dataSource);
        var enhances = new EnhanceRepository(dataSource, enhanceTable, items);
        var transfers = new TransferRepository(dataSource, transferTable, items);
        var crystals = new CrystalRepository(dataSource, crystalTable, items, config);
        var auctions = new AuctionRepository(dataSource, auctionTable, items, config);
        var ingots = new IngotRepository(dataSource, auctionTable);
        // 红符的扣减：只动背包这一张，生命在战斗进程里，不在这里。
        var talismans = new TalismanRepository(dataSource);
        // 卷轴的冷却到期、背包张数、当前级经验都从角色库读；写那一笔复用
        // ExpSettlementRepository（等级经验只从那里写，不另开一条路）。
        var scrolls = new ReviveScrollRepository(dataSource);
        // 限时经验道具的到期时刻、背包余量都从角色库读；写那一笔也只在这个
        // Repository。结算那一笔只读「这一刻有没有生效」，不写这张表。
        var boosters = new ExpBoosterRepository(dataSource);
        // 解绑只改 inventory 的 bound / bound_at，不新建表：这一笔事务也在这个
        // Repository 里，拒绝路径在扣减之前判完。
        var unbinds = new UnbindRepository(dataSource, items, unbindTable);
        // 充值订单的一笔只在这里结算：锁订单行 → 认归属 → 已 paid 就不做 →
        // 抢事务号 → 加元宝 → 订单改 paid → 读回。
        var recharges = new RechargeRepository(dataSource);
        // P3-1a2：突破与历练结算的唯一写入点。历练、炼心与灵石都在库里读，
        // 上一段开没开复用同一个 RealmTable。
        var realmAdvances = new RealmAdvanceRepository(dataSource, realms);
        var realmPhases = new RealmPhaseRepository(dataSource, realms);
        // P3-2a：灵石相邻档兑换与任务发放的灵石成交，两个写入点各一笔事务。
        var stoneExchanges = new StoneExchangeRepository(dataSource, currencies, stoneExchange, config);
        var stoneAuctions = new StoneAuctionRepository(dataSource, auctionTable, stoneExchange, config);
        // P3-3a4：城战易主的唯一写入点，一笔事务里改归属与抽成比例、抢事务号、读回提交。
        var siegeTerritories = new SiegeTerritoryRepository(
            dataSource, cityTable, siegeTerritoryPolicy);
        // P3-4a2：通行证的唯一写入点。买与领各一笔事务，奖励物品与数量只从配置读。
        // Q6/J4：点数与来源的唯一写入点。领取那一笔在 SeasonPassRepository 里读本表算门槛。
        var seasonPassPoints = new SeasonPassProgressRepository(
            dataSource, seasonPassProgressTable, calendar);
        var seasonPasses = new SeasonPassRepository(
            dataSource, seasonPassTable, items, config, seasonPassProgressTable);
        // P3-4b：月叠的唯一写入点（扣元宝 + 两种符一次到账，跨堆叠上限就占多格），
        // 与第二角色栏的名额事务（账号一行、只存总名额）。
        var monthlyBundles = new MonthlyBundleRepository(dataSource, monthlyBundleTable, items, config);
        var characterSlotsRepo = new CharacterSlotRepository(dataSource, characterSlotTable);
        // P3-4b：外观收藏是账号级只读视图，不写库、不改背包归属。
        var wardrobeRepo = new WardrobeRepository(dataSource, items);
        // C1：主线进度的唯一写入点。交任务与只读口共用这一份加载过的十二场正本。
        var mainlines = new MainlineRepository(
            dataSource, mainlineTable, mainlineDropTable, grants, items, config);
        var heroCodex = new HeroCodexRepository(dataSource, heroCodexTable);

        // P2-1c：判同宗门要问社交服。明文 gRPC（h2c）直连；channel 复用，不每次新建。
        var socialChannel = GrpcChannel.ForAddress(config.SocialUpstream);
        var social = new SocialService.SocialServiceClient(socialChannel);

        // P3-4a2：当前是哪一季只问全局服务（P3-4a1 的正本）。上游没起时通行证的
        // 购买与领取拒绝（season_unavailable），强化 / 拍卖 / 背包等老链路一律不受影响。
        var globalChannel = GrpcChannel.ForAddress(config.GlobalUpstream);
        ISeasonSource seasonSource = new GrpcSeasonSource(
            new Mmo.Proto.Trade.TradeService.TradeServiceClient(globalChannel));

        var builder = WebApplication.CreateSlimBuilder();
        // 服与服是明文 gRPC（h2c）：端点必须显式只走 HTTP/2，
        // 否则 Kestrel 在明文上只谈 HTTP/1.1，gRPC 握手直接失败。
        if (!Uri.TryCreate(config.Listen, UriKind.Absolute, out var listenUri)
            || !IPAddress.TryParse(listenUri.Host, out var listenAddress))
        {
            Console.WriteLine("config_error=listen 必须是带 IP 的地址：" + config.Listen);
            return 2;
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(listenAddress, listenUri.Port, options => options.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(characters);
        builder.Services.AddSingleton(inventory);
        builder.Services.AddSingleton(currency);
        builder.Services.AddSingleton(grants);
        builder.Services.AddSingleton(talismans);
        builder.Services.AddSingleton(scrolls);
        builder.Services.AddSingleton(races);
        builder.Services.AddSingleton(branches);
        builder.Services.AddSingleton(classes);
        builder.Services.AddSingleton(items);
        builder.Services.AddSingleton(currencies);
        builder.Services.AddSingleton(dungeons);
        builder.Services.AddSingleton(realms);
        builder.Services.AddSingleton(dungeonEntries);
        builder.Services.AddSingleton(hardEntries);
        builder.Services.AddSingleton(weeklyShares);
        builder.Services.AddSingleton(hotSpring);
        builder.Services.AddSingleton(expSettlements);
        builder.Services.AddSingleton(expTable);
        builder.Services.AddSingleton(enhanceTable);
        builder.Services.AddSingleton(enhances);
        builder.Services.AddSingleton(transferTable);
        builder.Services.AddSingleton(transfers);
        builder.Services.AddSingleton(crystalTable);
        builder.Services.AddSingleton(crystals);
        builder.Services.AddSingleton(auctionTable);
        builder.Services.AddSingleton(shopTable);
        builder.Services.AddSingleton(homeCoinShopTable);
        builder.Services.AddSingleton(talismanTable);
        builder.Services.AddSingleton(scrollTable);
        builder.Services.AddSingleton(boosters);
        builder.Services.AddSingleton(boosterTable);
        builder.Services.AddSingleton(unbinds);
        builder.Services.AddSingleton(unbindTable);
        builder.Services.AddSingleton(recharges);
        builder.Services.AddSingleton(realmAdvances);
        builder.Services.AddSingleton(realmPhases);
        builder.Services.AddSingleton(rechargeTable);
        builder.Services.AddSingleton(auctions);
        builder.Services.AddSingleton(ingots);
        builder.Services.AddSingleton(stoneExchange);
        builder.Services.AddSingleton(cityTable);
        builder.Services.AddSingleton(stoneExchanges);
        builder.Services.AddSingleton(stoneAuctions);
        builder.Services.AddSingleton(siegeTerritories);
        builder.Services.AddSingleton(siegeTerritoryPolicy);
        builder.Services.AddSingleton(social);
        builder.Services.AddSingleton(seasonPassTable);
        builder.Services.AddSingleton(seasonPassProgressTable);
        builder.Services.AddSingleton(nameRulesTable);
        builder.Services.AddSingleton(seasonPassPoints);
        builder.Services.AddSingleton(seasonPasses);
        builder.Services.AddSingleton(monthlyBundleTable);
        builder.Services.AddSingleton(monthlyBundles);
        builder.Services.AddSingleton(characterSlotTable);
        builder.Services.AddSingleton(characterSlotsRepo);
        builder.Services.AddSingleton(wardrobeRepo);
        builder.Services.AddSingleton(mainlineTable);
        builder.Services.AddSingleton(mainlines);
        builder.Services.AddSingleton(heroCodexTable);
        builder.Services.AddSingleton(heroCodex);
        builder.Services.AddSingleton(recipeTable);
        builder.Services.AddSingleton(cityTaskTable);
        builder.Services.AddSingleton(cityTasks);
        builder.Services.AddSingleton(deserters);
        builder.Services.AddSingleton(crafts);
        builder.Services.AddSingleton(dungeonDropTable);
        builder.Services.AddSingleton(mainlineDropTable);
        builder.Services.AddSingleton(seasonSource);
        builder.Services.AddSingleton(config);

        var app = builder.Build();
        app.MapGrpcService<CharacterGrpcService>();

        // E3 认这一行：进程起来了，gRPC 也在听。
        Console.WriteLine("ready name=Character pid=" + Environment.ProcessId);

        try
        {
            await app.RunAsync(stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Character pid=" + Environment.ProcessId);
        return 0;
    }
}
