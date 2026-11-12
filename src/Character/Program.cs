using System.Runtime.InteropServices;
using Character;
using Npgsql;

// 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
var configPath = "config/character.json";
var sqlPath = "db/character/character.sql";
string? reportName = null;
string? deleteName = null;
var stats = false;
var migrate = false;
long rawLevelWrite = 0;
var rawLevelValue = 0;
long levelOf = 0;
string? bindingOf = null;
var bindingSlot = 0;
long rawCurrencyId = 0;
string? rawCurrencyKind = null;
var rawCurrencyAmount = 0L;
string? txnCount = null;
long reservesOf = 0;
long entryCountOf = 0;
long fatigueStateOf = 0;
long setFatigueDayId = 0;
string? setFatigueDay = null;
long rawFatigueId = 0;
var rawFatigueValue = 0;
long shareStateOf = 0;
long setWeekDayId = 0;
string? setWeekDay = null;
long rawShareId = 0;
var rawShareValue = 0;
long sharesTotalOf = 0;
string? sharesTotalDungeon = null;
long expStateOf = 0;
long rawExpId = 0;
var rawExpValue = 0L;
var enhanceState = false;
string? enhanceConfigPath = null;
long enhanceLevelOf = 0;
var enhanceSlotOf = 0;
string? enhanceTxnCount = null;
long couponCountOf = 0;
long rawEnhanceId = 0;
var rawEnhanceSlot = 0;
var rawEnhanceLevel = 0;
var transferState = false;
string? transferConfigPath = null;
long transferStateOf = 0;
var transferFromSlot = 0;
var transferToSlot = 0;
string? transferTxnCount = null;
var crystalState = false;
string? crystalConfigPath = null;
long crystalStateOf = 0;
var crystalSlotOf = 0;
string? crystalItemOf = null;
string? crystalTxnCount = null;
var auctionState = false;
string? auctionConfigPath = null;
var shopList = false;
string? shopConfigPath = null;
long shopStateOf = 0;
long shopLedgerOf = 0;
// 红符：只读核对用。talismanConfigPath 指向临时表试坏配置，talismanStateOf 连库读张数。
var talismanList = false;
string? talismanConfigPath = null;
long talismanStateOf = 0;
long auctionBuyerOf = 0;
long auctionSellerOf = 0;
long auctionListingOf = 0;
string? auctionTxnOf = null;
long ingotBuyerOf = 0;
long ingotSellerOf = 0;
long ingotListingOf = 0;
string? ingotTxnOf = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--sql" when i + 1 < args.Length:
            sqlPath = args[++i];
            break;
        case "--migrate":
            migrate = true;
            break;
        case "--character-report" when i + 1 < args.Length:
            reportName = args[++i];
            break;
        case "--delete-character" when i + 1 < args.Length:
            deleteName = args[++i];
            break;
        case "--character-stats":
            stats = true;
            break;
        // 只给核对用：绕过接口直写库，验库层 CHECK。不能当运维手段用。
        case "--raw-level-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawLevelWrite);
            int.TryParse(args[++i], out rawLevelValue);
            break;
        case "--level-of" when i + 1 < args.Length:
            long.TryParse(args[++i], out levelOf);
            break;
        // 只给核对用：看这一格的 bound 与 bound_at 有没有一起落对。
        case "--binding-of" when i + 2 < args.Length:
            bindingOf = args[++i];
            int.TryParse(args[++i], out bindingSlot);
            break;
        // 只给核对用：绕过接口直写余额，验库层 CHECK 会不会挡住负数。
        case "--raw-currency-write" when i + 3 < args.Length:
            long.TryParse(args[++i], out rawCurrencyId);
            rawCurrencyKind = args[++i];
            long.TryParse(args[++i], out rawCurrencyAmount);
            break;
        // 只给核对用：这个事务号在表里有几行。
        case "--txn-count" when i + 1 < args.Length:
            txnCount = args[++i];
            break;
        // 只给核对用：读一个角色的疲劳与周份额。
        case "--reserves-of" when i + 1 < args.Length:
            long.TryParse(args[++i], out reservesOf);
            break;
        // 只给核对用：这个角色进过几次本。
        case "--entry-count" when i + 1 < args.Length:
            long.TryParse(args[++i], out entryCountOf);
            break;
        // 只给核对用：读疲劳、周份额与日界（含跨天归零后的状态）。
        case "--fatigue-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out fatigueStateOf);
            break;
        // 只给核对用：把日界改到别的一天，验第二天从 0 起算。
        case "--set-fatigue-day" when i + 2 < args.Length:
            long.TryParse(args[++i], out setFatigueDayId);
            setFatigueDay = args[++i];
            break;
        // 只给核对用：绕过接口直写疲劳，验库层日上限 CHECK。
        case "--raw-fatigue-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawFatigueId);
            int.TryParse(args[++i], out rawFatigueValue);
            break;
        // 只给核对用：读已用份额、剩余份额、周界与疲劳。
        case "--share-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out shareStateOf);
            break;
        // 只给核对用：把周界改到别的周首，验跨周归零。
        case "--set-week-day" when i + 2 < args.Length:
            long.TryParse(args[++i], out setWeekDayId);
            setWeekDay = args[++i];
            break;
        // 只给核对用：绕过接口直写已用份额，造并发现场或验库层非负 CHECK。
        case "--raw-share-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawShareId);
            int.TryParse(args[++i], out rawShareValue);
            break;
        // 只给核对用：这个角色在某个副本累计应得多少单人份。
        case "--shares-total" when i + 2 < args.Length:
            long.TryParse(args[++i], out sharesTotalOf);
            sharesTotalDungeon = args[++i];
            break;
        // 只给核对用：读等级、当前级经验、结算笔数与累计入账经验。
        case "--exp-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out expStateOf);
            break;
        // 只给核对用：绕过接口直写经验，验库层的非负 CHECK。
        case "--raw-exp-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawExpId);
            long.TryParse(args[++i], out rawExpValue);
            break;
        // 只给核对用：把强化表读出来，不连库、不启动进程。坏配置也走这里。
        case "--enhance-state":
            enhanceState = true;
            break;
        case "--enhance-config" when i + 1 < args.Length:
            enhanceConfigPath = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回强化等级、金币与三档石头余量。
        case "--enhance-level" when i + 2 < args.Length:
            long.TryParse(args[++i], out enhanceLevelOf);
            int.TryParse(args[++i], out enhanceSlotOf);
            break;
        // 只给核对用：这个强化事务号在表里有几行。
        case "--enhance-txn-count" when i + 1 < args.Length:
            enhanceTxnCount = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回保护券张数。
        case "--coupon-count" when i + 1 < args.Length:
            long.TryParse(args[++i], out couponCountOf);
            break;
        // 只给核对用：绕过业务层直写强化等级，验库层 CHECK 挡住越界。
        case "--raw-enhance-write" when i + 3 < args.Length:
            long.TryParse(args[++i], out rawEnhanceId);
            int.TryParse(args[++i], out rawEnhanceSlot);
            int.TryParse(args[++i], out rawEnhanceLevel);
            break;
        // 只给核对用：把转移表读出来，不连库、不启动进程。坏配置也走这里。
        case "--transfer-state":
            transferState = true;
            break;
        case "--transfer-config" when i + 1 < args.Length:
            transferConfigPath = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回两格等级、绑定、金币与转移石余量。
        case "--transfer-level" when i + 3 < args.Length:
            long.TryParse(args[++i], out transferStateOf);
            int.TryParse(args[++i], out transferFromSlot);
            int.TryParse(args[++i], out transferToSlot);
            break;
        // 只给核对用：这个转移事务号在表里有几行。
        case "--transfer-txn-count" when i + 1 < args.Length:
            transferTxnCount = args[++i];
            break;
        // 只给核对用：把结晶表读出来，不连库、不启动进程。坏配置也走这里。
        case "--crystal-state":
            crystalState = true;
            break;
        case "--crystal-config" when i + 1 < args.Length:
            crystalConfigPath = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回镶嵌状态与几个不该被改动的字段。
        // 第三个参数（结晶编号）可选，给了才顺带读背包余量。
        case "--crystal-sockets" when i + 2 < args.Length:
            long.TryParse(args[++i], out crystalStateOf);
            int.TryParse(args[++i], out crystalSlotOf);
            crystalItemOf = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i] : "";
            break;
        // 只给核对用：这个结晶事务号在表里有几行。
        case "--crystal-txn-count" when i + 1 < args.Length:
            crystalTxnCount = args[++i];
            break;
        // 只给核对用：把拍卖税率读出来，不连库、不启动进程。坏配置也走这里。
        case "--auction-state":
            auctionState = true;
            break;
        case "--auction-config" when i + 1 < args.Length:
            auctionConfigPath = args[++i];
            break;
        // 只给核对用：把商城清单读出来，不连库也不启动进程。坏清单也走这里。
        case "--shop-list":
            shopList = true;
            break;
        case "--shop-config" when i + 1 < args.Length:
            shopConfigPath = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回这个角色的元宝余额，
        // 用来证「清单外被拒之后元宝一分没动」。
        case "--shop-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out shopStateOf);
            break;
        // 只给核对用：数一数这个角色名下有几笔商城成交（事务表里 shop_buy: 前缀）。
        case "--shop-ledger" when i + 1 < args.Length:
            long.TryParse(args[++i], out shopLedgerOf);
            break;
        // 只给核对用：把红符表读出来，不连库也不启动进程。坏表也走这里——
        // 「回复超过同级红药」「发货物不是红符」都在这一步就起不来。
        case "--talisman-list":
            talismanList = true;
            break;
        case "--talisman-config" when i + 1 < args.Length:
            talismanConfigPath = args[++i];
            break;
        // 只给核对用：从库里独立读回这个角色背包里有几张红符、绑没绑，
        // 用来证「用掉一张之后只少一张」。
        case "--talisman-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out talismanStateOf);
            break;
        // 只给核对用：不看回包，从库里独立读回买方/卖方金币、全库金币分布、
        // 挂单状态与成交流水，用来证「这笔税不进任何背包」。
        case "--auction-ledger" when i + 3 < args.Length:
            long.TryParse(args[++i], out auctionBuyerOf);
            long.TryParse(args[++i], out auctionSellerOf);
            long.TryParse(args[++i], out auctionListingOf);
            auctionTxnOf = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i] : "";
            break;
        // 只给核对用：元宝寄售前后双方元宝、金币与全库总量，用来证元宝等量转移、
        // 金币只少一笔税。
        case "--ingot-ledger" when i + 3 < args.Length:
            long.TryParse(args[++i], out ingotBuyerOf);
            long.TryParse(args[++i], out ingotSellerOf);
            long.TryParse(args[++i], out ingotListingOf);
            ingotTxnOf = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i] : "";
            break;
        default:
            Console.WriteLine("unknown_arg=" + args[i]);
            return 64;
    }
}

// 强化表只读核对：与启动共用同一套校验（三档材料要在物品表里、金币要在币种表里），
// 不连库也不启动进程，坏配置能在这一步直接看出来。
if (enhanceState)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var items = ItemTable.Load("config/items.json");
        var currencies = CurrencyTable.Load("config/currency.json");
        var table = EnhanceTable.Load(enhanceConfigPath ?? characterConfig.EnhanceTable, items, currencies);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine("enhance_table_state=ok version=" + table.Version
            + " max_level=" + table.MaxLevel
            + " gold_kind=" + table.GoldKind
            + " tiers=" + string.Join(",", table.Tiers.Select(
                t => t.Id + ":" + t.FromLevel + "-" + t.ToLevel + ":" + t.StoneId))
            + " rates=" + string.Join(",", table.Tiers.Select(
                t => t.SuccessRate.ToString("0.##", culture)))
            + " gold=" + string.Join(",", table.Tiers.Select(t => t.GoldCost))
            + " stones=" + string.Join(",", table.Tiers.Select(t => t.StoneCost))
            + " coupon=" + table.ProtectItemId
            + " coupon_bind=always");
        Console.WriteLine("enhance_gate=ok target=7 tier=" + table.TierFor(7).Id
            + " stone=" + table.TierFor(7).StoneId
            + " target=12 tier=" + table.TierFor(12).Id);
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

// 转移表只读核对：与启动共用同一套校验（转移石要在物品表里、费用表要照
// 强化上限与递增关系），不连库也不启动进程，坏配置能在这一步直接看出来。
if (transferState)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var items = ItemTable.Load("config/items.json");
        var currencies = CurrencyTable.Load("config/currency.json");
        var enhanceTable = EnhanceTable.Load(
            enhanceConfigPath ?? characterConfig.EnhanceTable, items, currencies);
        var table = TransferTable.Load(
            transferConfigPath ?? characterConfig.TransferTable, items, currencies, enhanceTable);
        Console.WriteLine("transfer_table_state=ok version=" + table.Version
            + " max_level=" + table.MaxLevel
            + " transfer_stone=" + table.StoneItemId
            + " transfer_stone_cost=" + table.StoneCost
            + " transfer_gold_kind=" + table.GoldKind
            + " transfer_gold=" + string.Join(",", table.GoldCosts));
        Console.WriteLine("transfer_gate=ok level=1 gold=" + table.GoldCostFor(1)
            + " level=" + table.MaxLevel + " gold=" + table.GoldCostFor(table.MaxLevel));
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

// 结晶表只读核对：与启动共用同一套校验（三类写定、装备部位不能漏配、
// 结晶要在物品表里），不连库也不启动进程，坏配置能在这一步直接看出来。
if (crystalState)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var items = ItemTable.Load("config/items.json");
        var table = CrystalTable.Load(crystalConfigPath ?? characterConfig.CrystalTable, items);
        Console.WriteLine("crystal_table_state=ok version=" + table.Version
            + " crystal_sockets=" + table.SocketCount
            + " crystal_categories=" + string.Join(",", CrystalTable.WrittenCategories)
            + " crystal_parts=" + string.Join(",", table.EquipmentParts
                .OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value))
            + " crystal_allowed=" + string.Join(",", CrystalTable.WrittenCategories
                .Select(c => c + ":" + string.Join("+", table.CategoryParts[c])))
            + " crystal_items=" + string.Join(",", table.Crystals
                .OrderBy(c => c.Id).Select(c => c.Id + ":" + c.Category + ":" + c.FixedValue)));
        Console.WriteLine("crystal_gate=ok " + string.Join(" ", CrystalTable.WrittenCategories
            .Select(c => c + "=" + string.Join("+", table.CategoryParts[c]))));
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
        var table = ShopTable.Load(
            shopConfigPath ?? characterConfig.ShopTable, shopCurrencies, shopItems, shopEnhance,
            shopTalismans);
        Console.WriteLine("shop_list_state=ok version=" + table.Version
            + " currency=" + table.Currency
            + " items=" + table.Items.Count
            + " kinds=" + ShopTable.AllowedKinds.Length
            + " delivered_kinds=" + ShopTable.DeliveredKinds.Length
            + " deliverable_items=" + table.Items.Count(i => i.Deliverable));
        foreach (var item in table.Items)
        {
            Console.WriteLine("shop_item=" + item.Id
                + " kind=" + item.Kind
                + " price=" + item.PriceYuanbao
                + " delivery=" + (item.Deliverable ? item.DeliveryItemId + "x" + item.DeliveryCount : "pending")
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

// 拍卖税率只读核对：与启动共用同一套 5%–8% 写定区间校验，不连库也不启动进程。
if (auctionState)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var currencies = CurrencyTable.Load("config/currency.json");
        var table = AuctionTable.Load(auctionConfigPath ?? characterConfig.AuctionTable, currencies);
        Console.WriteLine("auction_table_state=ok version=" + table.Version
            + " tax_rate_pct=" + table.TaxRatePct
            + " tax_min=" + AuctionTable.WrittenMinRatePct
            + " tax_max=" + AuctionTable.WrittenMaxRatePct
            + " gold_kind=" + table.GoldKind
            + " rounding=" + table.Rounding
            + " ingot_kind=" + table.IngotKind);
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

// 红符表只读核对：与启动共用同一套校验（物品在 items.json 里、是消耗品、到账即绑、
// 回复不超过同级红药、冷却组必须是 heal），不连库也不启动进程。坏表在这一步就能看出来。
if (talismanList)
{
    try
    {
        var talismanConfig = CharacterConfig.Load(configPath);
        var talismanItems = ItemTable.Load("config/items.json");
        var table = TalismanTable.Load(
            talismanConfigPath ?? talismanConfig.TalismanTable, talismanItems);
        var red = table.RedTalisman;
        var blue = table.BlueTalisman;
        Console.WriteLine("talisman_list_state=ok version=" + table.Version
            + " red_item=" + (red?.ItemId ?? "-")
            + " hp_restore=" + (red?.HpRestore ?? 0)
            + " potion_hp_restore=" + (red?.PotionHpRestore ?? 0)
            + " cooldown_group=" + (red?.CooldownGroup ?? "-")
            + " cooldown_ms=" + (red?.CooldownMs ?? 0)
            + " blue_item=" + (blue?.ItemId ?? "-")
            + " mana_restore=" + (blue?.ManaRestore ?? 0)
            + " potion_mana_restore=" + (blue?.PotionManaRestore ?? 0)
            + " blue_group=" + (blue?.CooldownGroup ?? "-")
            + " blue_cooldown_ms=" + (blue?.CooldownMs ?? 0));
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

// 只读核对与清理走这里；正常启动不经过（gRPC 服务在下面）。
if (reportName != null || deleteName != null || stats || migrate || rawLevelWrite != 0 || levelOf != 0
    || bindingOf != null || rawCurrencyId != 0 || txnCount != null
    || reservesOf != 0 || entryCountOf != 0 || fatigueStateOf != 0 || setFatigueDay != null
    || rawFatigueId != 0 || shareStateOf != 0 || setWeekDay != null
    || rawShareId != 0 || sharesTotalDungeon != null || expStateOf != 0 || rawExpId != 0
    || enhanceLevelOf != 0 || enhanceTxnCount != null || couponCountOf != 0 || rawEnhanceId != 0
    || transferStateOf != 0 || transferTxnCount != null
    || crystalStateOf != 0 || crystalTxnCount != null || auctionBuyerOf != 0
    || ingotBuyerOf != 0 || shopStateOf != 0 || shopLedgerOf != 0 || talismanStateOf != 0)
{
    var connectionString = DbEnv.Resolve();
    if (connectionString == null)
    {
        Console.WriteLine("db_env=missing 需要 " + DbEnv.EnvName + " 或 " + DbEnv.DefaultEnvFile);
        return 3;
    }

    Console.WriteLine("db_env=" + DbEnv.Source());
    await using var dataSource = NpgsqlDataSource.Create(DbEnv.WithPoolLimit(connectionString, 2));
    var characters = new CharacterRepository(dataSource);

    try
    {
        if (migrate)
        {
            var sql = File.ReadAllText(sqlPath);
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("migrate=ok sql=" + sqlPath);
        }

        if (deleteName != null)
        {
            var removed = await characters.DeleteAsync(deleteName, CancellationToken.None);
            Console.WriteLine("delete=ok name=" + deleteName + " rows=" + removed);
        }

        if (stats)
        {
            Console.WriteLine("characters=" + await characters.CountAsync(CancellationToken.None));
        }

        if (rawLevelWrite != 0)
        {
            try
            {
                await characters.RawWriteLevelAsync(rawLevelWrite, rawLevelValue, CancellationToken.None);
                Console.WriteLine("raw_level=written id=" + rawLevelWrite + " level=" + rawLevelValue);
            }
            catch (PostgresException ex)
            {
                // 核对要看到的就是这个：库层的 CHECK 把越界值挡住了。
                Console.WriteLine("raw_level=rejected id=" + rawLevelWrite
                    + " level=" + rawLevelValue + " sqlstate=" + ex.SqlState);
            }
        }

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
            var shopTableForState = ShopTable.Load(
                shopCharConfig.ShopTable, shopCurrencies, shopItemsForState, shopEnhanceForState,
                shopTalismansForState);

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

        if (talismanStateOf != 0)
        {
            // 红符的张数与绑定态都从背包里读出来，不另算；回复值与上限读配置。
            // 张数是事实值——用掉一张之后这里少一张，就是这一卡的落点。
            var talismanCharConfig = CharacterConfig.Load(configPath);
            var talismanItemsForState = ItemTable.Load("config/items.json");
            var talismanTableForState = TalismanTable.Load(
                talismanCharConfig.TalismanTable, talismanItemsForState);
            var redForState = talismanTableForState.RedTalisman;
            var blueForState = talismanTableForState.BlueTalisman;
            var talismanInventory = new InventoryRepository(dataSource);
            var talismanSlots = await talismanInventory.ListAsync(
                talismanStateOf, CancellationToken.None);
            var redSlots = redForState is null
                ? new List<BagSlot>()
                : talismanSlots
                    .Where(s => string.Equals(s.ItemId, redForState.ItemId, StringComparison.Ordinal))
                    .ToList();
            var redCount = redSlots.Sum(s => s.Count);
            var redAllBound = redSlots.Count > 0 && redSlots.All(s => s.Bound);
            var blueSlots = blueForState is null
                ? new List<BagSlot>()
                : talismanSlots
                    .Where(s => string.Equals(s.ItemId, blueForState.ItemId, StringComparison.Ordinal))
                    .ToList();
            var blueCount = blueSlots.Sum(s => s.Count);
            var blueAllBound = blueSlots.Count > 0 && blueSlots.All(s => s.Bound);

            Console.WriteLine("talisman_state=ok id=" + talismanStateOf
                + " red_item=" + (redForState?.ItemId ?? "-")
                + " count=" + redCount
                + " bound=" + (redAllBound ? "true" : "false")
                + " slots=" + (redSlots.Count == 0
                    ? "-"
                    : string.Join(",", redSlots.Select(s => s.Slot + ":" + s.Count)))
                + " hp_restore=" + (redForState?.HpRestore ?? 0)
                + " potion_hp_restore=" + (redForState?.PotionHpRestore ?? 0)
                + " cooldown_group=" + (redForState?.CooldownGroup ?? "-")
                + " cooldown_ms=" + (redForState?.CooldownMs ?? 0)
                + " blue_item=" + (blueForState?.ItemId ?? "-")
                + " blue_count=" + blueCount
                + " blue_bound=" + (blueAllBound ? "true" : "false")
                + " blue_slots=" + (blueSlots.Count == 0
                    ? "-"
                    : string.Join(",", blueSlots.Select(s => s.Slot + ":" + s.Count)))
                + " mana_restore=" + (blueForState?.ManaRestore ?? 0)
                + " potion_mana_restore=" + (blueForState?.PotionManaRestore ?? 0)
                + " blue_group=" + (blueForState?.CooldownGroup ?? "-")
                + " blue_cooldown_ms=" + (blueForState?.CooldownMs ?? 0));
        }

        if (levelOf != 0)
        {
            var found = await characters.LevelOfAsync(levelOf, CancellationToken.None);
            Console.WriteLine(found is null
                ? "level=missing id=" + levelOf
                : "level=ok id=" + levelOf + " account=" + found.Value.Account + " level=" + found.Value.Level);
        }

        if (bindingOf != null)
        {
            var inventory = new InventoryRepository(dataSource);
            var found = await inventory.BindingOfAsync(bindingOf, bindingSlot, CancellationToken.None);
            Console.WriteLine(found is null
                ? "binding=missing name=" + bindingOf + " slot=" + bindingSlot
                : "binding=ok name=" + bindingOf + " slot=" + bindingSlot
                  + " bound=" + found.Value.Bound + " bound_at_present=" + found.Value.BoundAtPresent);
        }

        if (rawCurrencyId != 0 && rawCurrencyKind != null)
        {
            var money = new CurrencyRepository(dataSource);
            try
            {
                await money.RawWriteAsync(rawCurrencyId, rawCurrencyKind, rawCurrencyAmount, CancellationToken.None);
                Console.WriteLine("raw_currency=written id=" + rawCurrencyId
                    + " kind=" + rawCurrencyKind + " amount=" + rawCurrencyAmount);
            }
            catch (PostgresException ex)
            {
                // 核对要看到的就是这个：库层的 CHECK 把负数挡住了。
                Console.WriteLine("raw_currency=rejected id=" + rawCurrencyId
                    + " kind=" + rawCurrencyKind + " amount=" + rawCurrencyAmount
                    + " sqlstate=" + ex.SqlState);
            }
        }

        if (txnCount != null)
        {
            // 只数行，不经过发奖逻辑，免得核对自己去抢号。
            await using var command = dataSource.CreateCommand(
                "select count(*) from \"角色\".grant_txn where txn_id = $1");
            command.Parameters.AddWithValue(txnCount);
            var value = await command.ExecuteScalarAsync(CancellationToken.None);
            Console.WriteLine("txn_count=" + Convert.ToInt32(value) + " txn=" + txnCount);
        }

        if (reservesOf != 0)
        {
            var found = await characters.ReservesAsync(reservesOf, CancellationToken.None);
            Console.WriteLine(found is null
                ? "reserves=missing id=" + reservesOf
                : "reserves=ok id=" + reservesOf
                  + " fatigue=" + found.Value.Fatigue
                  + " weekly_share=" + found.Value.WeeklyShare);
        }

        if (entryCountOf != 0)
        {
            await using var command = dataSource.CreateCommand(
                "select count(*) from \"角色\".dungeon_entry where character_id = $1");
            command.Parameters.AddWithValue(entryCountOf);
            var value = await command.ExecuteScalarAsync(CancellationToken.None);
            Console.WriteLine("entry_count=" + Convert.ToInt32(value) + " id=" + entryCountOf);
        }

        if (fatigueStateOf != 0 || setFatigueDay != null || rawFatigueId != 0)
        {
            var dungeonEntries = new DungeonEntryRepository(dataSource);

            if (fatigueStateOf != 0)
            {
                var state = await dungeonEntries.FatigueStateAsync(fatigueStateOf, CancellationToken.None);
                Console.WriteLine(state is null
                    ? "fatigue_state=missing id=" + fatigueStateOf
                    : "fatigue_state=ok id=" + fatigueStateOf
                      + " fatigue=" + state.Value.Fatigue
                      + " weekly_share=" + state.Value.WeeklyShare
                      + " day=" + state.Value.Day.ToString("yyyy-MM-dd"));
            }

            if (setFatigueDay != null && setFatigueDayId != 0
                && DateOnly.TryParse(setFatigueDay, out var day))
            {
                await dungeonEntries.SetFatigueDayAsync(setFatigueDayId, day, CancellationToken.None);
                Console.WriteLine("fatigue_day=set id=" + setFatigueDayId + " day=" + day.ToString("yyyy-MM-dd"));
            }

            if (rawFatigueId != 0)
            {
                try
                {
                    await dungeonEntries.RawWriteFatigueAsync(
                        rawFatigueId, rawFatigueValue, CancellationToken.None);
                    Console.WriteLine("raw_fatigue=written id=" + rawFatigueId
                        + " fatigue=" + rawFatigueValue);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层的 CHECK 把超过日上限的值挡住了。
                    Console.WriteLine("raw_fatigue=rejected id=" + rawFatigueId
                        + " fatigue=" + rawFatigueValue + " sqlstate=" + ex.SqlState);
                }
            }
        }

        if (shareStateOf != 0 || setWeekDay != null || rawShareId != 0 || sharesTotalDungeon != null)
        {
            var weeklyShares = new WeeklyShareRepository(dataSource);

            if (shareStateOf != 0)
            {
                // 上限从同一个副本表读，和跑着的角色服看到的是同一份。
                var weeklyCap = DungeonTable.Load(CharacterConfig.Load(configPath).DungeonTable).WeeklyShareCap;
                var state = await weeklyShares.ShareStateAsync(shareStateOf, weeklyCap, CancellationToken.None);
                Console.WriteLine(state is null
                    ? "share_state=missing id=" + shareStateOf
                    : "share_state=ok id=" + shareStateOf
                      + " used=" + state.Value.Used
                      + " remaining=" + state.Value.Remaining
                      + " fatigue=" + state.Value.Fatigue
                      + " week=" + state.Value.Week.ToString("yyyy-MM-dd"));
            }

            if (setWeekDay != null && setWeekDayId != 0
                && DateOnly.TryParse(setWeekDay, out var week))
            {
                await weeklyShares.SetWeeklyShareDayAsync(setWeekDayId, week, CancellationToken.None);
                Console.WriteLine("week_day=set id=" + setWeekDayId + " day=" + week.ToString("yyyy-MM-dd"));
            }

            if (rawShareId != 0)
            {
                try
                {
                    await weeklyShares.RawWriteShareAsync(rawShareId, rawShareValue, CancellationToken.None);
                    Console.WriteLine("raw_share=written id=" + rawShareId + " used=" + rawShareValue);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层 CHECK 把负份额挡住了。
                    Console.WriteLine("raw_share=rejected id=" + rawShareId
                        + " used=" + rawShareValue + " sqlstate=" + ex.SqlState);
                }
            }

            if (sharesTotalDungeon != null && sharesTotalOf != 0)
            {
                var total = await weeklyShares.SharesTotalAsync(
                    sharesTotalOf, sharesTotalDungeon, CancellationToken.None);
                Console.WriteLine("shares_total=" + total + " id=" + sharesTotalOf
                    + " dungeon=" + sharesTotalDungeon);
            }
        }

        if (expStateOf != 0 || rawExpId != 0)
        {
            var expSettlements = new ExpSettlementRepository(dataSource);

            if (expStateOf != 0)
            {
                var state = await expSettlements.ExpStateAsync(expStateOf, CancellationToken.None);
                Console.WriteLine(state is null
                    ? "exp_state=missing id=" + expStateOf
                    : "exp_state=ok id=" + expStateOf
                      + " level=" + state.Value.Level
                      + " exp=" + state.Value.Exp
                      + " rows=" + state.Value.Rows
                      + " total_gain=" + state.Value.TotalGain);
            }

            if (rawExpId != 0)
            {
                try
                {
                    await expSettlements.RawWriteExpAsync(rawExpId, rawExpValue, CancellationToken.None);
                    Console.WriteLine("raw_exp=written id=" + rawExpId + " exp=" + rawExpValue);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层 CHECK 把负经验挡住了。
                    Console.WriteLine("raw_exp=rejected id=" + rawExpId
                        + " exp=" + rawExpValue + " sqlstate=" + ex.SqlState);
                }
            }
        }

        if (enhanceLevelOf != 0 || enhanceTxnCount != null || couponCountOf != 0
            || rawEnhanceId != 0)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var enhanceTable = EnhanceTable.Load(
                enhanceConfigPath ?? characterConfig.EnhanceTable,
                items, CurrencyTable.Load("config/currency.json"));
            var enhances = new EnhanceRepository(dataSource, enhanceTable, items);

            if (rawEnhanceId != 0)
            {
                try
                {
                    await enhances.RawWriteLevelAsync(
                        rawEnhanceId, rawEnhanceSlot, rawEnhanceLevel, CancellationToken.None);
                    Console.WriteLine("raw_enhance=written id=" + rawEnhanceId
                        + " slot=" + rawEnhanceSlot + " level=" + rawEnhanceLevel);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层 CHECK 把越界的强化等级挡住了。
                    Console.WriteLine("raw_enhance=rejected id=" + rawEnhanceId
                        + " level=" + rawEnhanceLevel + " sqlstate=" + ex.SqlState);
                }
            }

            if (enhanceLevelOf != 0)
            {
                var state = await enhances.StateAsync(enhanceLevelOf, enhanceSlotOf, CancellationToken.None);
                Console.WriteLine("enhance_state=ok id=" + enhanceLevelOf
                    + " slot=" + enhanceSlotOf
                    + " level=" + state.Level
                    + " gold=" + state.Gold
                    + " low=" + state.Low
                    + " mid=" + state.Mid
                    + " high=" + state.High);
            }

            if (enhanceTxnCount != null)
            {
                Console.WriteLine("enhance_txn_count="
                    + await enhances.TxnCountAsync(enhanceTxnCount, CancellationToken.None)
                    + " txn=" + enhanceTxnCount);
            }

            if (couponCountOf != 0)
            {
                Console.WriteLine("coupon_count="
                    + await enhances.CouponCountAsync(couponCountOf, CancellationToken.None)
                    + " id=" + couponCountOf
                    + " item=" + enhanceTable.ProtectItemId);
            }
        }

        if (transferStateOf != 0 || transferTxnCount != null)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var currencies = CurrencyTable.Load("config/currency.json");
            var enhanceTable = EnhanceTable.Load(
                enhanceConfigPath ?? characterConfig.EnhanceTable, items, currencies);
            var transferTable = TransferTable.Load(
                transferConfigPath ?? characterConfig.TransferTable,
                items, currencies, enhanceTable);
            var transfers = new TransferRepository(dataSource, transferTable, items);

            if (transferStateOf != 0)
            {
                var state = await transfers.StateAsync(
                    transferStateOf, transferFromSlot, transferToSlot, CancellationToken.None);
                Console.WriteLine("transfer_state=ok id=" + transferStateOf
                    + " from=" + transferFromSlot + " to=" + transferToSlot
                    + " from_level=" + state.FromLevel
                    + " to_level=" + state.ToLevel
                    // 显式输出小写，和网关回包以及核对脚本读到的布尔形状一致。
                    + " from_bound=" + (state.FromBound ? "true" : "false")
                    + " to_bound=" + (state.ToBound ? "true" : "false")
                    + " gold=" + state.Gold
                    + " stone=" + state.StoneLeft);
            }

            if (transferTxnCount != null)
            {
                Console.WriteLine("transfer_txn_count="
                    + await transfers.TxnCountAsync(transferTxnCount, CancellationToken.None)
                    + " txn=" + transferTxnCount);
            }
        }

        if (crystalStateOf != 0 || crystalTxnCount != null)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var crystalTable = CrystalTable.Load(
                crystalConfigPath ?? characterConfig.CrystalTable, items);
            var crystals = new CrystalRepository(dataSource, crystalTable, items, characterConfig);

            if (crystalStateOf != 0)
            {
                var state = await crystals.StateAsync(
                    crystalStateOf, crystalSlotOf, crystalItemOf ?? "", CancellationToken.None);
                Console.WriteLine("crystal_state=ok id=" + crystalStateOf
                    + " slot=" + crystalSlotOf
                    + " filled=" + state.Filled
                    + " installed=" + state.Installed
                    + " left=" + state.CrystalLeft
                    + " gold=" + state.Gold
                    + " enhance=" + state.EnhanceLevel
                    // 显式输出小写，和网关回包以及核对脚本读到的布尔形状一致。
                    + " bound=" + (state.Bound ? "true" : "false"));
            }

            if (crystalTxnCount != null)
            {
                Console.WriteLine("crystal_txn_count="
                    + await crystals.TxnCountAsync(crystalTxnCount, CancellationToken.None)
                    + " txn=" + crystalTxnCount);
            }
        }

        if (auctionBuyerOf != 0)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var auctionItems = ItemTable.Load("config/items.json");
            var auctionCurrencies = CurrencyTable.Load("config/currency.json");
            var auctionTable = AuctionTable.Load(
                auctionConfigPath ?? characterConfig.AuctionTable, auctionCurrencies);
            var auctions = new AuctionRepository(
                dataSource, auctionTable, auctionItems, characterConfig);
            var ledger = await auctions.StateAsync(
                auctionBuyerOf, auctionSellerOf, auctionListingOf,
                auctionTxnOf ?? "", CancellationToken.None);
            Console.WriteLine("auction_state=ok buyer=" + auctionBuyerOf
                + " buyer_gold=" + ledger.BuyerGold
                + " seller=" + auctionSellerOf
                + " seller_gold=" + ledger.SellerGold
                + " total_gold=" + ledger.TotalGold
                + " listing=" + auctionListingOf
                + " state=" + ledger.ListingState
                + " price=" + ledger.Price
                + " tax=" + ledger.Tax
                + " net=" + ledger.Net
                + " settlements=" + ledger.Settlements
                + " txn=" + ledger.Txns
                + " buyer_items=" + ledger.BuyerItems
                + " seller_items=" + ledger.SellerItems
                + " total_items=" + ledger.TotalItems
                + " accounts=" + ledger.Accounts);
        }

        if (ingotBuyerOf != 0)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var ingotCurrencies = CurrencyTable.Load("config/currency.json");
            var ingotTable = AuctionTable.Load(
                auctionConfigPath ?? characterConfig.AuctionTable, ingotCurrencies);
            var ingots = new IngotRepository(dataSource, ingotTable);
            var ledger = await ingots.StateAsync(
                ingotBuyerOf, ingotSellerOf, ingotListingOf,
                ingotTxnOf ?? "", CancellationToken.None);
            Console.WriteLine("ingot_state=ok buyer=" + ingotBuyerOf
                + " buyer_gold=" + ledger.BuyerGold
                + " seller=" + ingotSellerOf
                + " seller_gold=" + ledger.SellerGold
                + " total_gold=" + ledger.TotalGold
                + " buyer_ingot=" + ledger.BuyerIngot
                + " seller_ingot=" + ledger.SellerIngot
                + " total_ingot=" + ledger.TotalIngot
                + " listing=" + ingotListingOf
                + " state=" + ledger.ListingState
                + " price=" + ledger.Price
                + " tax=" + ledger.Tax
                + " net=" + ledger.Net
                + " settlements=" + ledger.Settlements
                + " txn=" + ledger.Txns
                + " accounts=" + ledger.Accounts);
        }

        if (reportName != null)
        {
            var report = await characters.ReportAsync(reportName, CancellationToken.None);
            if (report is null)
            {
                Console.WriteLine("report=missing name=" + reportName);
            }
            else
            {
                Console.WriteLine("report=ok name=" + reportName
                    + " id=" + report.Id
                    + " race=" + report.Race
                    + " class=" + report.ClassId
                    + " branch=" + report.Branch
                    + " level=" + report.Level
                    + " realm=" + report.Realm
                    + " main_stage_null=" + report.MainStageEmpty
                    + " alignment_null=" + report.AlignmentEmpty
                    + " appearance_len=" + report.AppearanceLength);
            }
        }
    }
    catch (Exception ex) when (ex is NpgsqlException or IOException)
    {
        Console.WriteLine("db_error=" + ex.GetType().Name + " " + ex.Message);
        return 5;
    }

    return 0;
}

using var stop = new CancellationTokenSource();
PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.Cancel();
});
PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    stop.Cancel();
});

return await new CharacterProcess(configPath).RunAsync(stop.Token);
