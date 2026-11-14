using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 第二角色栏的唯一正本（`config/character_slot.json`）。方案 12.1 第 810 行写定：
/// 「第二角色栏 同账号再养一名修士 装备、等级、境界不共享。外观收藏可以共享」。
///
/// 加载期的硬约束：
///   · 一个账号默认只有 base_slots 个角色位；方案只写了「第二」这一种，第三个及以后
///     没有卡承接，所以 max_slots 必须正好是 base_slots + 1，写大了就起不来。
///   · base_slots 必须是 1：方案写的是「同账号再养一名」，基础位不是 0 也不是 2。
///   · product_id 必须是 shop.json 里那条 second_slot 商品，且不许填假发货物——
///     第二角色栏是状态发货（给账号加一个位），不往背包放东西。
///   · 表里不许出现装备 / 等级 / 境界 / 外观 / 名额以外的东西：那些都不是配置能改的，
///     「不共享」是表结构的结果（背包、强化、境界都按 character_id 各存一行）。
/// 价格不在这份表里：商城按 shop.json 那条商品直接标价卖，本表只读那一个数。
/// </summary>
public sealed class CharacterSlotTable
{
    public const string WrittenSchema = "character-slot-table/1";

    static readonly string[] AllowedTopKeys =
        ["schema", "version", "note", "pending", "product_id", "base_slots", "max_slots"];
    static readonly string[] RequiredTopKeys =
        ["schema", "version", "product_id", "base_slots", "max_slots"];

    // 这些键一律不许出现：装备 / 等级 / 境界 / 外观 / 金币 / 价格都不能从这份配置里改。
    static readonly string[] ForbiddenKeys =
    [
        "equip", "equipment", "level", "realm", "appearance", "wardrobe", "collection",
        "shared", "share", "gold", "yuanbao", "price", "price_yuanbao", "reward",
    ];

    public int Version { get; }
    public bool Pending { get; }
    public string ProductId { get; }
    public int BaseSlots { get; }
    public int MaxSlots { get; }
    public long PriceYuanbao { get; }

    CharacterSlotTable(
        int version, bool pending, string productId, int baseSlots, int maxSlots, long priceYuanbao)
    {
        Version = version;
        Pending = pending;
        ProductId = productId;
        BaseSlots = baseSlots;
        MaxSlots = maxSlots;
        PriceYuanbao = priceYuanbao;
    }

    /// <summary>
    /// 与角色服真实启动共用同一条校验链：商品与标价从 shop.json 读，本表不写第二套价格。
    /// </summary>
    public static CharacterSlotTable Load(string path, ShopTable shop)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配置文件 " + path);
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("角色栏表不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            foreach (var key in RequiredTopKeys)
            {
                if (!root.TryGetProperty(key, out _))
                {
                    throw new ConfigException("character_slot.json 缺字段 " + key);
                }
            }

            RejectUnknownKeys(root);

            Wire wire;
            try
            {
                wire = JsonSerializer.Deserialize<Wire>(text, JsonOptions)
                    ?? throw new ConfigException("角色栏表是空的");
            }
            catch (JsonException ex)
            {
                throw new ConfigException("角色栏表字段类型不对：" + ex.Message);
            }

            if (!string.Equals((wire.Schema ?? "").Trim(), WrittenSchema, StringComparison.Ordinal))
            {
                throw new ConfigException("character_slot.json 的 schema 必须是 " + WrittenSchema);
            }

            if (wire.Version is null || wire.Version.Value <= 0)
            {
                throw new ConfigException("character_slot.json 的 version 必须大于 0");
            }

            var productId = (wire.ProductId ?? "").Trim();
            if (productId.Length == 0)
            {
                throw new ConfigException("character_slot.json 的 product_id 不能为空");
            }

            var product = shop.Find(productId)
                ?? throw new ConfigException("character_slot.json 的 product_id 不在 shop.json 里："
                    + productId);
            if (!string.Equals(product.Kind, "second_slot", StringComparison.Ordinal))
            {
                throw new ConfigException("character_slot.json 的 product_id 指到的不是第二角色栏："
                    + productId + " kind=" + product.Kind);
            }

            if (product.Deliverable)
            {
                // 第二角色栏是状态发货：买下即给账号加一个位，不往背包放东西。
                throw new ConfigException("character_slot.json 的角色栏商品不许配 delivery_item_id："
                    + productId);
            }

            var baseSlots = wire.BaseSlots ?? 0;
            if (baseSlots != 1)
            {
                // 方案写的是「同账号再养一名」：一个账号本来就有且只有一个位。
                throw new ConfigException("character_slot.json 的 base_slots 必须是 1，现在是 "
                    + baseSlots);
            }

            var maxSlots = wire.MaxSlots ?? 0;
            if (maxSlots != baseSlots + 1)
            {
                // 方案只写了「第二角色栏」：第三个及以后没有卡承接，写大了就是提前做后面的玩法。
                throw new ConfigException("character_slot.json 的 max_slots 必须是 base_slots + 1（"
                    + (baseSlots + 1) + "），现在是 " + maxSlots);
            }

            return new CharacterSlotTable(
                wire.Version!.Value, wire.Pending ?? false, productId, baseSlots, maxSlots,
                product.PriceYuanbao);
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed record Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; init; }
        [JsonPropertyName("version")] public int? Version { get; init; }
        [JsonPropertyName("pending")] public bool? Pending { get; init; }
        [JsonPropertyName("product_id")] public string? ProductId { get; init; }
        [JsonPropertyName("base_slots")] public int? BaseSlots { get; init; }
        [JsonPropertyName("max_slots")] public int? MaxSlots { get; init; }
    }

    static void RejectUnknownKeys(JsonElement row)
    {
        foreach (var property in row.EnumerateObject())
        {
            if (Array.IndexOf(AllowedTopKeys, property.Name) < 0)
            {
                throw new ConfigException("character_slot.json 的顶层出现不许出现的键："
                    + property.Name);
            }

            foreach (var forbidden in ForbiddenKeys)
            {
                if (string.Equals(property.Name, forbidden, StringComparison.Ordinal))
                {
                    throw new ConfigException("character_slot.json 的顶层出现不许出现的键："
                        + property.Name);
                }
            }
        }
    }
}
