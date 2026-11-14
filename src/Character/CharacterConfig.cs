using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 角色服的启动配置：gRPC 听哪儿、连接池多大。
/// 连接串不在这里，从环境或 /srv/mmo/gamedb.env 读。
/// </summary>
public sealed record CharacterConfig(
    string Listen, int DbPoolMax, int BagSlots, string DungeonTable, string ExpTable,
    string EnhanceTable, string TransferTable, string CrystalTable, string AuctionTable,
    string ShopTable, string TalismanTable, string ReviveScrollTable, string ExpBoosterTable,
    string UnbindTable, string RechargeTable, string SocialUpstream, string RealmTable,
    string StoneExchangeTable, string CityTable, string SiegeTerritoryTable)
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

        var pool = wire.DbPoolMax ?? 8;
        if (pool < 1 || pool > 20)
        {
            throw new ConfigException("db_pool_max 必须在 1 到 20 之间，现在是 " + pool);
        }

        // 背包格子数走配置：扩容道具留给后面的行，这一行只认这个默认值。
        var bagSlots = wire.BagSlots ?? 40;
        if (bagSlots < 1 || bagSlots > 200)
        {
            throw new ConfigException("bag_slots 必须在 1 到 200 之间，现在是 " + bagSlots);
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

        return new CharacterConfig(
            listen.TrimEnd('/'), pool, bagSlots, dungeonTable, expTable, enhanceTable,
            transferTable, crystalTable, auctionTable, shopTable, talismanTable,
            reviveScrollTable, expBoosterTable, unbindTable, rechargeTable,
            socialUpstream.TrimEnd('/'), realmTable, stoneExchangeTable, cityTable,
            siegeTerritoryTable);
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
    }
}

public sealed class ConfigException(string message) : Exception(message);
