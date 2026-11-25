using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 角色服的启动配置：gRPC 听哪儿、连接池多大。
/// 连接串不在这里，从环境或 /srv/mmo/gamedb.env 读。
/// </summary>
public sealed record CharacterConfig(
    string Listen, int DbPoolMax, int BagSlots, int BagExpandStep, int BagSlotsMax,
    string DungeonTable, string ExpTable,
    string EnhanceTable, string TransferTable, string CrystalTable, string AuctionTable,
    string ShopTable, string TalismanTable, string ReviveScrollTable, string ExpBoosterTable,
    string UnbindTable, string RechargeTable, string SocialUpstream, string RealmTable,
    string StoneExchangeTable, string CityTable, string SiegeTerritoryTable,
    string SeasonPassTable, string SeasonTable, string MonthlyBundleTable,
    string CharacterSlotTable, string GlobalUpstream, string MainlineTable,
    string RecipeTable, string CalendarTable, string CityTaskTable,
    string SeasonPassProgressTable, string NameRulesTable, string PillTable,
    string CraftRollTable, string MountTable, string SpiritBeastTable, string StallTable,
    string AwakenTable)
{
    public const string DefaultDungeonTable = "config/dungeons.json";
    public const string DefaultExpTable = "config/exp.json";
    public const string DefaultEnhanceTable = "config/enhance.json";
    public const string DefaultTransferTable = "config/transfer.json";
    public const string DefaultCrystalTable = "config/crystal.json";
    public const string DefaultAuctionTable = "config/auction.json";
    public const string DefaultShopTable = "config/shop.json";
    public const string DefaultTalismanTable = "config/talisman.json";
    public const string DefaultReviveScrollTable = "config/revive_scroll.json";
    public const string DefaultExpBoosterTable = "config/exp_booster.json";
    public const string DefaultUnbindTable = "config/unbind_item.json";
    public const string DefaultRechargeTable = "config/recharge.json";
    // P3-1a1：十一段境界顺序与版本开关；核对脚本可以指到临时表试坏配置。
    public const string DefaultRealmTable = "config/realms.json";
    // P3-2a：灵石五档兑换表与城池表，缺省读仓库那两份；核对脚本可以指到临时表试坏配置。
    public const string DefaultStoneExchangeTable = "config/stone_exchange.json";
    public const string DefaultCityTable = "config/city.json";
    // P3-3a4：城战易主后的抽成比例与城池编号，缺省读仓库那一份；核对脚本可以指到临时表。
    public const string DefaultSiegeTerritoryTable = "config/siege_territory.json";
    // P3-4a2：通行证两轨的正本。它只记「哪一季发什么」，不记价格（价格在 shop.json）。
    public const string DefaultSeasonPassTable = "config/season_pass.json";
    // P3-4a2：season.json 只用来交叉校验「通行证里的季真的存在」，当前是哪一季仍归全局服务。
    public const string DefaultSeasonTable = "config/season.json";
    // P3-4b：月叠的正本。哪些符、每天几张、共几天都在这份文件里，价格仍在 shop.json。
    public const string DefaultMonthlyBundleTable = "config/monthly_bundle.json";
    // P3-4b：账号角色位的正本（基础位、上限、商品编号）。名额的事实值在库里的
    // "角色".character_slot，这里只是规则与标价来源。
    public const string DefaultCharacterSlotTable = "config/character_slot.json";
    // P3-4a2：当前季是全局服务的事实值；角色服经 DescribeSeason 只读它，不自己认定。
    public const string DefaultGlobalUpstream = "http://127.0.0.1:64000";
    // C1：主线十二场的内容正本。也不显式登记进 config/character.json：老卡的临时
    // 配置目录没有这份表时按「默认文件名 + 缺表降级」处理，不拖垮启动。
    public const string DefaultMainlineTable = "config/mainline.json";
    // B1：丹药的正本（六种丹的回复值、池、冷却组与档位）。它要和 talisman.json
    // 互相钉「符不超过同级丹」，所以两份表都得能指到临时文件。
    public const string DefaultPillTable = "config/pill.json";
    public const string DefaultCraftRollTable = "config/craft_roll.json";
    // B4：玩法坐骑与灵兽。与日历 / 城池任务同口径不显式登记进 config/character.json：
    // 老配置不写这两项也照常起（走仓库根这两份）；核对脚本可指到临时表试坏配置。
    public const string DefaultMountTable = "config/mount.json";
    public const string DefaultSpiritBeastTable = "config/spirit_beast.json";
    // B5：摆摊的税率、格数、等级门槛与「是否跨服」只在 config/stall.json；
    // 与拍卖同一种写法，核对脚本可以指到临时表试坏配置。
    public const string DefaultStallTable = "config/stall.json";
    // B6：觉醒的门槛与「哪条分支解锁哪条技能」。和老卡同口径不显式登记进
    // config/character.json：不写就走仓库根这一份，核对脚本可指到临时表试坏配置。
    public const string DefaultAwakenTable = "config/awaken.json";
    // K3：生产 / 配方的正本。同样不显式登记进 character.json：老卡的临时配置目录里
    // 没有这份表时走默认文件名，仓库根的那一份照常可读。
    public const string DefaultRecipeTable = "config/recipes.json";
    // Q3/G1：日 / 周 / 赛季边界的唯一正本。存量临时配置不写这一项时走仓库根这一份，
    // 核对脚本可以指到临时日历表试时区与重置小时。
    public const string DefaultCalendarTable = "config/calendar.json";
    // Q5/J2：主城修炼 / 境界任务的正本。与日历一样不显式登记进 character.json，
    // 老配置不写这一项也照常起（走仓库根这一份）；核对脚本可指到临时表试坏配置。
    public const string DefaultCityTaskTable = "config/city_tasks.json";
    // Q6/J4：通行证点数与来源的正本。与日历 / 城池任务同口径不显式登记进
    // config/character.json：老配置不写这一项也照常起（走仓库根这一份）。
    public const string DefaultSeasonPassProgressTable = "config/season_pass_progress.json";
    // Q12/K3：角色名规则（长度、唯一、改名、敏感词表路径）。与日历 / 城池任务同口径
    // 不显式登记进 config/character.json：老配置不写这一项也照常起（走仓库根这一份）；
    // 核对脚本可指到临时表试坏配置。
    public const string DefaultNameRulesTable = "config/name_rules.json";
    // P2-1c：角色服判同宗门要问社交服（65000）；本地默认指到它，可被配置覆盖。
    public const string DefaultSocialUpstream = "http://127.0.0.1:65000";

    public static CharacterConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配置文件 " + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("配置是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配置不是合法 JSON：" + ex.Message);
        }

        var listen = (wire.Listen ?? "").Trim();
        if (listen.Length == 0 || !Uri.TryCreate(listen, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ConfigException("listen 必须是 http:// 或 https:// 的地址：" + listen);
        }

        // 连接池与背包格数都必须写在配置里，不在代码里给默认值：缺了就起不来，
        // 免得两处各记一个数字（C7 数值台账：值只在 config/，代码没有第二份）。
        var pool = wire.DbPoolMax
            ?? throw new ConfigException("character.json 缺 db_pool_max");
        if (pool < 1 || pool > 20)
        {
            throw new ConfigException("db_pool_max 必须在 1 到 20 之间，现在是 " + pool);
        }

        var bagSlots = wire.BagSlots
            ?? throw new ConfigException("character.json 缺 bag_slots");
        if (bagSlots < 1 || bagSlots > 200)
        {
            throw new ConfigException("bag_slots 必须在 1 到 200 之间，现在是 " + bagSlots);
        }

        // B7+B8：背包扩容的步长与上限。两键不写就是「扩容没开」——老卡的临时配置目录
        // （只改 bag_slots 试上限的那几张）不必补这两行，容量仍等于 bag_slots。
        // 步长与上限都是配置数值，代码里没有第二份。
        var bagExpandStep = wire.BagExpandStep ?? 0;
        if (bagExpandStep < 0 || bagExpandStep > 200)
        {
            throw new ConfigException("bag_expand_step 必须在 0 到 200 之间，现在是 " + bagExpandStep);
        }

        var bagSlotsMax = wire.BagSlotsMax ?? bagSlots;
        if (bagSlotsMax < bagSlots || bagSlotsMax > 200)
        {
            throw new ConfigException("bag_slots_max 必须不小于 bag_slots 且不超过 200，现在是 "
                + bagSlotsMax + "（bag_slots=" + bagSlots + "）");
        }

        if (bagExpandStep > 0 && bagSlotsMax <= bagSlots)
        {
            throw new ConfigException("bag_expand_step 大于 0 时 bag_slots_max 必须大于 bag_slots");
        }

        // 副本表缺省读仓库那一份；核对脚本可以指到临时副本，改上限做隔离测试。
        var dungeonTable = (wire.DungeonTable ?? DefaultDungeonTable).Trim();
        if (dungeonTable.Length == 0)
        {
            throw new ConfigException("dungeon_table 不能为空");
        }

        // 经验表也走同一份配置正本；核对脚本可以指到临时表改数值做隔离测试。
        var expTable = (wire.ExpTable ?? DefaultExpTable).Trim();
        if (expTable.Length == 0)
        {
            throw new ConfigException("exp_table 不能为空");
        }

        // 强化概率与三档消耗也走同一份配置正本；核对脚本可以指到临时表改数值做隔离测试。
        var enhanceTable = (wire.EnhanceTable ?? DefaultEnhanceTable).Trim();
        if (enhanceTable.Length == 0)
        {
            throw new ConfigException("enhance_table 不能为空");
        }

        // 转移费用也走同一份配置正本；核对脚本可以指到临时表改数值做隔离测试。
        var transferTable = (wire.TransferTable ?? DefaultTransferTable).Trim();
        if (transferTable.Length == 0)
        {
            throw new ConfigException("transfer_table 不能为空");
        }

        // 结晶的三类、部位映射与镶嵌位也走同一份配置正本；核对脚本可以指到临时表
        // 改部位或改镶嵌位数量做隔离测试。
        var crystalTable = (wire.CrystalTable ?? DefaultCrystalTable).Trim();
        if (crystalTable.Length == 0)
        {
            throw new ConfigException("crystal_table 不能为空");
        }

        // 拍卖税率也走同一份配置正本；核对脚本可以指到临时表改税率做隔离测试。
        var auctionTable = (wire.AuctionTable ?? DefaultAuctionTable).Trim();
        if (auctionTable.Length == 0)
        {
            throw new ConfigException("auction_table 不能为空");
        }

        // B5：摆摊表与拍卖表分开，税与格数各自读各自的；同一套「指到临时表」的核对口。
        var stallTable = (wire.StallTable ?? DefaultStallTable).Trim();
        if (stallTable.Length == 0)
        {
            throw new ConfigException("stall_table 不能为空");
        }

        // B6：觉醒表。门槛必须等于 exp.json 的 level_cap，覆盖全部分支；
        // 与技能表的交叉校验在战斗服启动期（角色服不读技能表，第 7 节）。
        var awakenTable = (wire.AwakenTable ?? DefaultAwakenTable).Trim();
        if (awakenTable.Length == 0)
        {
            throw new ConfigException("awaken_table 不能为空");
        }

        // 商城清单也走同一份配置正本；核对脚本可以指到临时表试坏清单。
        var shopTable = (wire.ShopTable ?? DefaultShopTable).Trim();
        if (shopTable.Length == 0)
        {
            throw new ConfigException("shop_table 不能为空");
        }

        // 符类消耗品同一套写法：红符的回复值与共用冷却都在这里，核对可以指到临时表。
        var talismanTable = (wire.TalismanTable ?? DefaultTalismanTable).Trim();
        if (talismanTable.Length == 0)
        {
            throw new ConfigException("talisman_table 不能为空");
        }

        // 复活卷轴也走同一份配置正本；核对脚本可以指到临时表试坏配置。
        // 拉起血量比例与经验比例不在这份文件里，由 Load 去读 config/classes.json。
        var reviveScrollTable = (wire.ReviveScrollTable ?? DefaultReviveScrollTable).Trim();
        if (reviveScrollTable.Length == 0)
        {
            throw new ConfigException("revive_scroll_table 不能为空");
        }

        // 限时经验道具也走同一份配置正本；核对脚本可以指到临时表试坏配置。
        // 时长与倍率都在这份文件里，角色库只存到期时刻，不存倍率。
        var expBoosterTable = (wire.ExpBoosterTable ?? DefaultExpBoosterTable).Trim();
        if (expBoosterTable.Length == 0)
        {
            throw new ConfigException("exp_booster_table 不能为空");
        }

        // 解绑道具也走同一份配置正本；核对脚本可以指到临时表试坏配置。
        // 档位与张数都在这份文件里，角色库只存 bound / bound_at，不存张数。
        var unbindTable = (wire.UnbindTable ?? DefaultUnbindTable).Trim();
        if (unbindTable.Length == 0)
        {
            throw new ConfigException("unbind_table 不能为空");
        }

        // 内部充值商品也走同一份配置正本；核对脚本可以指到临时表试坏配置。
        // 元宝数只在这份文件里，客户端带不进来。
        var rechargeTable = (wire.RechargeTable ?? DefaultRechargeTable).Trim();
        if (rechargeTable.Length == 0)
        {
            throw new ConfigException("recharge_table 不能为空");
        }

        // P2-1c：判同宗门要问社交服；缺省指到本地 65000，可被配置覆盖。
        // 必须是 http/https 地址，否则起不来。
        var socialUpstream = (wire.SocialUpstream ?? DefaultSocialUpstream).Trim();
        if (socialUpstream.Length == 0
            || !Uri.TryCreate(socialUpstream, UriKind.Absolute, out var socialUri)
            || (socialUri.Scheme != "http" && socialUri.Scheme != "https"))
        {
            throw new ConfigException("social_upstream 必须是 http:// 或 https:// 的地址：" + socialUpstream);
        }

        // P3-1a1：境界顺序与版本开关也走同一份配置正本；核对脚本可以指到临时表
        // 改开关做隔离测试（例如把元婴打开）。
        var realmTable = (wire.RealmTable ?? DefaultRealmTable).Trim();
        if (realmTable.Length == 0)
        {
            throw new ConfigException("realm_table 不能为空");
        }

        // P3-2a：灵石兑换表与城池表也走同一份配置正本；核对脚本可以指到临时表
        // 试坏配置（改比例、改最高档中文名、把 vein_id 指到不存在的矿脉节点）。
        var stoneExchangeTable = (wire.StoneExchangeTable ?? DefaultStoneExchangeTable).Trim();
        if (stoneExchangeTable.Length == 0)
        {
            throw new ConfigException("stone_exchange_table 不能为空");
        }

        var cityTable = (wire.CityTable ?? DefaultCityTable).Trim();
        if (cityTable.Length == 0)
        {
            throw new ConfigException("city_table 不能为空");
        }

        // P3-3a4：领地表与城池表是同一份正本，抽成比例只从这里读，请求里带不进来。
        var siegeTerritoryTable =
            (wire.SiegeTerritoryTable ?? DefaultSiegeTerritoryTable).Trim();
        if (siegeTerritoryTable.Length == 0)
        {
            throw new ConfigException("siege_territory_table 不能为空");
        }

        // P3-4a2：通行证表与它的两处交叉校验来源（season.json 的季、shop.json 的标价）。
        var seasonPassTable = (wire.SeasonPassTable ?? DefaultSeasonPassTable).Trim();
        if (seasonPassTable.Length == 0)
        {
            throw new ConfigException("season_pass_table 不能为空");
        }

        var seasonTable = (wire.SeasonTable ?? DefaultSeasonTable).Trim();
        if (seasonTable.Length == 0)
        {
            throw new ConfigException("season_table 不能为空");
        }

        // P3-4b：月叠与第二角色栏各一份正本；都不显式登记进 config/character.json，
        // 老卡的临时配置目录里没有这两份也照常起（走默认文件名）。
        var monthlyBundleTable =
            (wire.MonthlyBundleTable ?? DefaultMonthlyBundleTable).Trim();
        if (monthlyBundleTable.Length == 0)
        {
            throw new ConfigException("monthly_bundle_table 不能为空");
        }

        var characterSlotTable = (wire.CharacterSlotTable ?? DefaultCharacterSlotTable).Trim();
        if (characterSlotTable.Length == 0)
        {
            throw new ConfigException("character_slot_table 不能为空");
        }

        // 当前季在全局服务；缺省指到本机 64000，老卡配置不写这项也照常起。
        var globalUpstream = (wire.GlobalUpstream ?? DefaultGlobalUpstream).Trim().TrimEnd('/');
        if (globalUpstream.Length == 0
            || !Uri.TryCreate(globalUpstream, UriKind.Absolute, out var globalUri)
            || (globalUri.Scheme != "http" && globalUri.Scheme != "https"))
        {
            throw new ConfigException("global_upstream 必须是 http:// 或 https:// 的地址：" + globalUpstream);
        }

        // C1：主线十二场只加内容和配置，不改既有表。缺表由调用方降级，不在这里抛。
        var mainlineTable = (wire.MainlineTable ?? DefaultMainlineTable).Trim();
        if (mainlineTable.Length == 0)
        {
            throw new ConfigException("mainline_table 不能为空");
        }

        // K3：生产 / 配方表。老配置不写这一项也照常起（走默认文件名）。
        var recipeTable = (wire.RecipeTable ?? DefaultRecipeTable).Trim();
        if (recipeTable.Length == 0)
        {
            throw new ConfigException("recipe_table 不能为空");
        }

        // Q3/G1：日、周、赛季边界只从这一份日历表算。路径按工作目录解析，
        // 与 races.json / items.json 同口径；核对脚本可指到临时表。
        var calendarTable = (wire.CalendarTable ?? DefaultCalendarTable).Trim();
        if (calendarTable.Length == 0)
        {
            throw new ConfigException("calendar_table 不能为空");
        }

        // Q5/J2：城池任务表只加配置，不改既有表。缺表由调用方降级，不在这里抛。
        var cityTaskTable = (wire.CityTaskTable ?? DefaultCityTaskTable).Trim();
        if (cityTaskTable.Length == 0)
        {
            throw new ConfigException("city_task_table 不能为空");
        }

        // Q6/J4：通行证进度表。和城池任务表一样缺表由调用方降级，不在这里抛。
        var seasonPassProgressTable =
            (wire.SeasonPassProgressTable ?? DefaultSeasonPassProgressTable).Trim();
        if (seasonPassProgressTable.Length == 0)
        {
            throw new ConfigException("season_pass_progress_table 不能为空");
        }

        // Q12/K3：名字规则表。缺表由调用方降级，不在这里抛。
        var nameRulesTable = (wire.NameRulesTable ?? DefaultNameRulesTable).Trim();
        if (nameRulesTable.Length == 0)
        {
            throw new ConfigException("name_rules_table 不能为空");
        }

        // B1：丹药表。和符表一样走配置正本；缺表由调用方降级不在这里抛。
        var pillTable = (wire.PillTable ?? DefaultPillTable).Trim();
        if (pillTable.Length == 0)
        {
            throw new ConfigException("pill_table 不能为空");
        }

        // B3：制作掷点表。与配方表同口径：老配置不写就走默认文件名。
        // B4：坐骑与灵兽各一份正本；数值（速度 / 生活协助）只在这两份文件里，
        // 代码与世界服都不写第二份。核对脚本可指到临时表试坏配置。
        var mountTable = (wire.MountTable ?? DefaultMountTable).Trim();
        if (mountTable.Length == 0)
        {
            throw new ConfigException("mount_table 不能为空");
        }

        var spiritBeastTable = (wire.SpiritBeastTable ?? DefaultSpiritBeastTable).Trim();
        if (spiritBeastTable.Length == 0)
        {
            throw new ConfigException("spirit_beast_table 不能为空");
        }

        var craftRollTable = (wire.CraftRollTable ?? DefaultCraftRollTable).Trim();
        if (craftRollTable.Length == 0)
        {
            throw new ConfigException("craft_roll_table 不能为空");
        }

        return new CharacterConfig(
            listen.TrimEnd('/'), pool, bagSlots, bagExpandStep, bagSlotsMax,
            dungeonTable, expTable, enhanceTable,
            transferTable, crystalTable, auctionTable, shopTable, talismanTable,
            reviveScrollTable, expBoosterTable, unbindTable, rechargeTable,
            socialUpstream.TrimEnd('/'), realmTable, stoneExchangeTable, cityTable,
            siegeTerritoryTable, seasonPassTable, seasonTable, monthlyBundleTable,
            characterSlotTable, globalUpstream, mainlineTable, recipeTable, calendarTable,
            cityTaskTable, seasonPassProgressTable, nameRulesTable, pillTable, craftRollTable,
            mountTable, spiritBeastTable, stallTable, awakenTable);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        [JsonPropertyName("listen")]
        public string? Listen { get; init; }

        [JsonPropertyName("db_pool_max")]
        public int? DbPoolMax { get; init; }

        [JsonPropertyName("bag_slots")]
        public int? BagSlots { get; init; }

        // B7+B8：不写就是扩容没开（老卡的临时配置目录不必补）。
        [JsonPropertyName("bag_expand_step")]
        public int? BagExpandStep { get; init; }

        [JsonPropertyName("bag_slots_max")]
        public int? BagSlotsMax { get; init; }

        [JsonPropertyName("dungeon_table")]
        public string? DungeonTable { get; init; }

        [JsonPropertyName("exp_table")]
        public string? ExpTable { get; init; }

        [JsonPropertyName("enhance_table")]
        public string? EnhanceTable { get; init; }

        [JsonPropertyName("transfer_table")]
        public string? TransferTable { get; init; }

        [JsonPropertyName("crystal_table")]
        public string? CrystalTable { get; init; }

        [JsonPropertyName("auction_table")]
        public string? AuctionTable { get; init; }

        [JsonPropertyName("stall_table")]
        public string? StallTable { get; init; }

        [JsonPropertyName("awaken_table")]
        public string? AwakenTable { get; init; }

        [JsonPropertyName("shop_table")]
        public string? ShopTable { get; init; }

        [JsonPropertyName("talisman_table")]
        public string? TalismanTable { get; init; }

        [JsonPropertyName("revive_scroll_table")]
        public string? ReviveScrollTable { get; init; }

        [JsonPropertyName("exp_booster_table")]
        public string? ExpBoosterTable { get; init; }

        [JsonPropertyName("unbind_table")]
        public string? UnbindTable { get; init; }

        [JsonPropertyName("recharge_table")]
        public string? RechargeTable { get; init; }

        [JsonPropertyName("social_upstream")]
        public string? SocialUpstream { get; init; }

        [JsonPropertyName("realm_table")]
        public string? RealmTable { get; init; }

        [JsonPropertyName("stone_exchange_table")]
        public string? StoneExchangeTable { get; init; }

        [JsonPropertyName("city_table")]
        public string? CityTable { get; init; }

        [JsonPropertyName("siege_territory_table")]
        public string? SiegeTerritoryTable { get; init; }

        [JsonPropertyName("season_pass_table")]
        public string? SeasonPassTable { get; init; }

        [JsonPropertyName("season_table")]
        public string? SeasonTable { get; init; }

        [JsonPropertyName("monthly_bundle_table")]
        public string? MonthlyBundleTable { get; init; }

        [JsonPropertyName("character_slot_table")]
        public string? CharacterSlotTable { get; init; }

        [JsonPropertyName("global_upstream")]
        public string? GlobalUpstream { get; init; }

        [JsonPropertyName("mainline_table")]
        public string? MainlineTable { get; init; }

        [JsonPropertyName("recipe_table")]
        public string? RecipeTable { get; init; }

        [JsonPropertyName("calendar_table")]
        public string? CalendarTable { get; init; }

        [JsonPropertyName("city_task_table")]
        public string? CityTaskTable { get; init; }

        [JsonPropertyName("season_pass_progress_table")]
        public string? SeasonPassProgressTable { get; init; }

        [JsonPropertyName("name_rules_table")]
        public string? NameRulesTable { get; init; }

        [JsonPropertyName("craft_roll_table")]
        public string? CraftRollTable { get; init; }

        [JsonPropertyName("mount_table")]
        public string? MountTable { get; init; }

        [JsonPropertyName("spirit_beast_table")]
        public string? SpiritBeastTable { get; init; }

        [JsonPropertyName("pill_table")]
        public string? PillTable { get; init; }
    }
}
