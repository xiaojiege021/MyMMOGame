using Grpc.Net.Client;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

/// <summary>
/// 启动装配用的一整套仓储与上游客户端（D2d：从 CharacterProcess 里原样搬出来）。
/// 写入点口径不变：钱只走 GrantRepository，经验只走 ExpSettlementRepository。
/// </summary>
public sealed class CharacterServices
{
    public required CharacterRepository Characters { get; init; }
    public required InventoryRepository Inventory { get; init; }
    public required BagRepository Bags { get; init; }
    public required EquipRepository Equips { get; init; }
    public required CurrencyRepository Currency { get; init; }
    public required CityTaxRepository CityTax { get; init; }
    public required GrantRepository Grants { get; init; }
    public required CraftRepository Crafts { get; init; }
    public required NpcShopRepository NpcShops { get; init; }
    public required CityTaskRepository CityTasks { get; init; }
    public required DeserterRepository Deserters { get; init; }
    public required DungeonEntryRepository DungeonEntries { get; init; }
    public required HardEntryRepository HardEntries { get; init; }
    public required WeeklyShareRepository WeeklyShares { get; init; }
    public required HotSpringRepository HotSpring { get; init; }
    public required ExpSettlementRepository ExpSettlements { get; init; }
    public required EnhanceRepository Enhances { get; init; }
    public required TransferRepository Transfers { get; init; }
    public required CrystalRepository Crystals { get; init; }
    public required AuctionRepository Auctions { get; init; }
    public required IngotRepository Ingots { get; init; }
    public required TalismanRepository Talismans { get; init; }
    public required PillRepository Pills { get; init; }
    public required ReviveScrollRepository Scrolls { get; init; }
    public required ExpBoosterRepository Boosters { get; init; }
    public required UnbindRepository Unbinds { get; init; }
    public required RechargeRepository Recharges { get; init; }
    public required RealmAdvanceRepository RealmAdvances { get; init; }
    public required RealmPhaseRepository RealmPhases { get; init; }
    public required StoneExchangeRepository StoneExchanges { get; init; }
    public required StoneAuctionRepository StoneAuctions { get; init; }
    public required SiegeTerritoryRepository SiegeTerritories { get; init; }
    public required SeasonPassProgressRepository SeasonPassPoints { get; init; }
    public required SeasonPassRepository SeasonPasses { get; init; }
    public required MonthlyBundleRepository MonthlyBundles { get; init; }
    public required CharacterSlotRepository CharacterSlots { get; init; }
    public required BagExpandRepository BagExpands { get; init; }
    public required WardrobeRepository Wardrobe { get; init; }
    public required MainlineRepository Mainlines { get; init; }
    public required HeroCodexRepository HeroCodex { get; init; }
    public required GrpcChannel SocialChannel { get; init; }
    public required SocialService.SocialServiceClient Social { get; init; }
    public required GrpcChannel GlobalChannel { get; init; }
    public required ISeasonSource SeasonSource { get; init; }
}

public static class ServiceAssembly
{
    public static CharacterServices Build(
        NpgsqlDataSource dataSource, CharacterConfig config, CharacterTables tables)
    {
        var characters = new CharacterRepository(dataSource);
        var inventory = new InventoryRepository(dataSource);
        // Q10：背包操作（移动 / 丢弃 / 排序）的唯一写入点，与 inventory 共用同一个连接池。
        var bags = new BagRepository(dataSource);
        // B9：穿戴的唯一写入点。一条记录 = 这个部位穿的是哪一行背包；
        // 装备固定攻击也在这里按已穿装备求和（数值只在 config/tables.Items.json）。
        var equips = new EquipRepository(dataSource);
        var currency = new CurrencyRepository(dataSource);
        var cityTax = new CityTaxRepository(dataSource, tables.CityTable, tables.AuctionTable.GoldKind);
        var grants = new GrantRepository(dataSource, tables.Items, tables.Currencies, config, cityTax);
        // K3：制作的两笔事务（start 扣材料 / 费用、finish 掷点入包）都在这一个仓储里；
        // 产出入包复用 grants 的同一条 SQL，钱只用 grants 的货币写法。
        // B3：制作掷点的区间 / 权重档 / 附加属性允许表也从这里进；仓储不自己读配置文件。
        var crafts = new CraftRepository(
            dataSource, tables.RecipeTable, tables.Items, tables.CraftRollTable,
            config, grants, inventory);
        // K2+Q13：NPC 商店的四笔事务（买 / 卖 / 回购 / 修理）。钱与入包都复用 grants 的
        // 那两条 SQL，本类不另开第二条货币 UPDATE，也不在客户端算价。
        var npcShops = new NpcShopRepository(
            dataSource, tables.NpcShopTable, tables.Items, config, grants, tables.Calendar);
        // Q5/J2：城池任务。钱复用 grants 的同一条货币写法，抽成复用 cityTax 的账本写法，
        // 周期键只读 tables.Calendar；本卡不另开第二条货币 UPDATE，也不另算一套抽成。
        var cityTasks = new CityTaskRepository(
            dataSource, tables.CityTaskTable, tables.Calendar, tables.Realms, grants, cityTax);
        // Q9/H4：逃跑惩罚的唯一写入口（写到期时刻，不扣任何东西）。
        var deserters = new DeserterRepository(dataSource);
        var dungeonEntries = new DungeonEntryRepository(dataSource, tables.Calendar);
        // P2-2a：高难本（龙巢）每人每周的次数。只写 dungeon_entry 一行，
        // 不碰疲劳与周份额；次数以库里已写下的进本记录为准。
        var hardEntries = new HardEntryRepository(dataSource, tables.Calendar);
        var weeklyShares = new WeeklyShareRepository(dataSource, tables.Calendar);
        var hotSpring = new HotSpringRepository(dataSource, tables.Calendar);
        var expSettlements = new ExpSettlementRepository(dataSource);
        var enhances = new EnhanceRepository(dataSource, tables.EnhanceTable, tables.Items);
        var transfers = new TransferRepository(dataSource, tables.TransferTable, tables.Items);
        var crystals = new CrystalRepository(dataSource, tables.CrystalTable, tables.Items, config);
        var auctions = new AuctionRepository(dataSource, tables.AuctionTable, tables.Items, config);
        var ingots = new IngotRepository(dataSource, tables.AuctionTable);
        // 红符的扣减：只动背包这一张，生命在战斗进程里，不在这里。
        var talismans = new TalismanRepository(dataSource);
        // B1：吃丹也只有一个写入点——扣一格背包，生命与蓝量不在这里。
        var pills = new PillRepository(dataSource);
        // 卷轴的冷却到期、背包张数、当前级经验都从角色库读；写那一笔复用
        // ExpSettlementRepository（等级经验只从那里写，不另开一条路）。
        var scrolls = new ReviveScrollRepository(dataSource);
        // 限时经验道具的到期时刻、背包余量都从角色库读；写那一笔也只在这个
        // Repository。结算那一笔只读「这一刻有没有生效」，不写这张表。
        var boosters = new ExpBoosterRepository(dataSource);
        // 解绑只改 inventory 的 bound / bound_at，不新建表：这一笔事务也在这个
        // Repository 里，拒绝路径在扣减之前判完。
        var unbinds = new UnbindRepository(dataSource, tables.Items, tables.UnbindTable);
        // 充值订单的一笔只在这里结算：锁订单行 → 认归属 → 已 paid 就不做 →
        // 抢事务号 → 加元宝 → 订单改 paid → 读回。
        var recharges = new RechargeRepository(dataSource);
        // P3-1a2：突破与历练结算的唯一写入点。历练、炼心与灵石都在库里读，
        // 上一段开没开复用同一个 RealmTable。
        var realmAdvances = new RealmAdvanceRepository(dataSource, tables.Realms);
        var realmPhases = new RealmPhaseRepository(dataSource, tables.Realms);
        // P3-2a：灵石相邻档兑换与任务发放的灵石成交，两个写入点各一笔事务。
        var stoneExchanges = new StoneExchangeRepository(dataSource, tables.Currencies, tables.StoneExchange, config);
        var stoneAuctions = new StoneAuctionRepository(dataSource, tables.AuctionTable, tables.StoneExchange, config);
        // P3-3a4：城战易主的唯一写入点，一笔事务里改归属与抽成比例、抢事务号、读回提交。
        var siegeTerritories = new SiegeTerritoryRepository(
            dataSource, tables.CityTable, tables.SiegeTerritoryPolicy);
        // P3-4a2：通行证的唯一写入点。买与领各一笔事务，奖励物品与数量只从配置读。
        // Q6/J4：点数与来源的唯一写入点。领取那一笔在 SeasonPassRepository 里读本表算门槛。
        var seasonPassPoints = new SeasonPassProgressRepository(
            dataSource, tables.SeasonPassProgressTable, tables.Calendar);
        var seasonPasses = new SeasonPassRepository(
            dataSource, tables.SeasonPassTable, tables.Items, config, tables.SeasonPassProgressTable);
        // P3-4b：月叠的唯一写入点（扣元宝 + 两种符一次到账，跨堆叠上限就占多格），
        // 与第二角色栏的名额事务（账号一行、只存总名额）。
        var monthlyBundles = new MonthlyBundleRepository(dataSource, tables.MonthlyBundleTable, tables.Items, config);
        var characterSlotsRepo = new CharacterSlotRepository(dataSource, tables.CharacterSlotTable);
        // B7+B8：背包扩容的唯一写入点。商品编号从商城清单现取（mode=bag_expand 那一条），
        // 步长与上限只从 character.json 读；代码里没有第二份数字。
        var bagProduct = tables.ShopTable.Items.FirstOrDefault(i => i.Mode == DeliveryMode.BagExpand);
        var bagExpands = new BagExpandRepository(dataSource, config, bagProduct?.Id ?? "");
        // P3-4b：外观收藏是账号级只读视图，不写库、不改背包归属。
        var wardrobeRepo = new WardrobeRepository(dataSource, tables.Items);
        // C1：主线进度的唯一写入点。交任务与只读口共用这一份加载过的十二场正本。
        var mainlines = new MainlineRepository(
            dataSource, tables.MainlineTable, tables.MainlineDropTable, grants, tables.Items, config);
        var heroCodex = new HeroCodexRepository(dataSource, tables.HeroCodexTable);

        // P2-1c：判同宗门要问社交服。明文 gRPC（h2c）直连；channel 复用，不每次新建。
        var socialChannel = GrpcChannel.ForAddress(config.SocialUpstream);
        var social = new SocialService.SocialServiceClient(socialChannel);

        // P3-4a2：当前是哪一季只问全局服务（P3-4a1 的正本）。上游没起时通行证的
        // 购买与领取拒绝（season_unavailable），强化 / 拍卖 / 背包等老链路一律不受影响。
        var globalChannel = GrpcChannel.ForAddress(config.GlobalUpstream);
        ISeasonSource seasonSource = new GrpcSeasonSource(
            new Mmo.Proto.Trade.TradeService.TradeServiceClient(globalChannel));

        return new CharacterServices
        {
            Characters = characters,
            Inventory = inventory,
            Bags = bags,
            Equips = equips,
            Currency = currency,
            CityTax = cityTax,
            Grants = grants,
            Crafts = crafts,
            NpcShops = npcShops,
            CityTasks = cityTasks,
            Deserters = deserters,
            DungeonEntries = dungeonEntries,
            HardEntries = hardEntries,
            WeeklyShares = weeklyShares,
            HotSpring = hotSpring,
            ExpSettlements = expSettlements,
            Enhances = enhances,
            Transfers = transfers,
            Crystals = crystals,
            Auctions = auctions,
            Ingots = ingots,
            Talismans = talismans,
            Pills = pills,
            Scrolls = scrolls,
            Boosters = boosters,
            Unbinds = unbinds,
            Recharges = recharges,
            RealmAdvances = realmAdvances,
            RealmPhases = realmPhases,
            StoneExchanges = stoneExchanges,
            StoneAuctions = stoneAuctions,
            SiegeTerritories = siegeTerritories,
            SeasonPassPoints = seasonPassPoints,
            SeasonPasses = seasonPasses,
            MonthlyBundles = monthlyBundles,
            CharacterSlots = characterSlotsRepo,
            BagExpands = bagExpands,
            Wardrobe = wardrobeRepo,
            Mainlines = mainlines,
            HeroCodex = heroCodex,
            SocialChannel = socialChannel,
            Social = social,
            GlobalChannel = globalChannel,
            SeasonSource = seasonSource,
        };
    }
}
