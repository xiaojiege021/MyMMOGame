using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
        ExpTable expTable;
        EnhanceTable enhanceTable;
        TransferTable transferTable;
        CrystalTable crystalTable;
        AuctionTable auctionTable;
        ShopTable shopTable;
        TalismanTable talismanTable;
        ReviveScrollTable scrollTable;
        ExpBoosterTable boosterTable;
        UnbindTable unbindTable;
        RechargeTable rechargeTable;
        try
        {
            races = RaceTable.Load("config/races.json");
            branches = BranchTable.Load("config/branches.json");
            classes = new ClassIds("config/classes.json");
            items = ItemTable.Load("config/items.json");
            currencies = CurrencyTable.Load("config/currency.json");
            dungeons = DungeonTable.Load(config.DungeonTable);
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
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        Console.WriteLine("config=" + configPath);
        Console.WriteLine("listen=" + config.Listen);
        Console.WriteLine("races=" + races.All.Count + " race_table_version=" + races.Version);
        Console.WriteLine("branches=" + branches.All.Count + " branch_table_version=" + branches.Version);
        Console.WriteLine("items=" + items.All.Count + " item_table_version=" + items.Version
            + " bag_slots=" + config.BagSlots);
        Console.WriteLine("currencies=" + currencies.All.Count + " currency_table_version=" + currencies.Version);
        Console.WriteLine("dungeons=" + dungeons.All.Count + " dungeon_table_version=" + dungeons.Version
            + " fatigue_daily_cap=" + dungeons.FatigueDailyCap
            + " hot_spring_recovery=" + dungeons.HotSpringRecovery
            + " weekly_share_cap=" + dungeons.WeeklyShareCap
            + " monster_hp_scale_per_player=" + dungeons.MonsterHpScalePerPlayer
            + " monster_damage_scale_per_player=" + dungeons.MonsterDamageScalePerPlayer);
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
            + " kinds=" + ShopTable.AllowedKinds.Length
            + " delivered_kinds=" + ShopTable.DeliveredKinds.Length);
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

        var connectionString = DbEnv.Resolve();
        Console.WriteLine("db_env=" + DbEnv.Source());

        if (connectionString == null)
        {
            // 角色数据没有库就没法建号，这里直接不起来，免得看起来在跑其实什么都做不了。
            Console.WriteLine("db_missing=1 需要 MMO_GAMEDB 或 " + DbEnv.DefaultEnvFile);
            return 3;
        }

        await using var dataSource = NpgsqlDataSource.Create(DbEnv.WithPoolLimit(connectionString, config.DbPoolMax));
        var characters = new CharacterRepository(dataSource);
        var inventory = new InventoryRepository(dataSource);
        var currency = new CurrencyRepository(dataSource);
        var grants = new GrantRepository(dataSource, items, currencies, config);
        var dungeonEntries = new DungeonEntryRepository(dataSource);
        var weeklyShares = new WeeklyShareRepository(dataSource);
        var hotSpring = new HotSpringRepository(dataSource);
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
        builder.Services.AddSingleton(dungeonEntries);
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
        builder.Services.AddSingleton(talismanTable);
        builder.Services.AddSingleton(scrollTable);
        builder.Services.AddSingleton(boosters);
        builder.Services.AddSingleton(boosterTable);
        builder.Services.AddSingleton(unbinds);
        builder.Services.AddSingleton(unbindTable);
        builder.Services.AddSingleton(recharges);
        builder.Services.AddSingleton(rechargeTable);
        builder.Services.AddSingleton(auctions);
        builder.Services.AddSingleton(ingots);
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
