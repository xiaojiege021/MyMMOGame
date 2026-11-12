using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 商城清单的唯一正本（`config/shop.json`）。方案 12.1 定死了能上架的十八种，
/// 表外的种类一律不上架；12.1 的「绝不上架」那一串在这里再挡一遍，
/// 就算有人把白名单改错也起不来。标价方案没给数字，只存在这个文件里，
/// 代码里没有第二套价格。
/// </summary>
public sealed class ShopTable
{
    public const string WrittenSchema = "shop-table/1";

    // 方案第 12.1 写定：商城只收元宝。币种编号在 currency.json 里是 yuanbao。
    public const string WrittenCurrency = "yuanbao";

    // 12.1 表里的十八种，顺序照原表。多一种少一种都算玩法被改了。
    public static readonly string[] AllowedKinds =
    {
        "outfit", "weapon_skin", "realm_skin", "flight_trail", "mount_skin",
        "home_decor", "guild_display", "bag_expand", "teleport_talisman",
        "red_talisman", "blue_talisman", "revive_scroll", "unbind_item",
        "exp_booster", "monthly_bundle", "second_slot", "season_pass",
        "protect_ticket",
    };

    // 12.1 的「绝不上架」那一串。白名单已经覆盖，这里单列是为了报错能说清是哪一条。
    public static readonly string[] ForbiddenKinds =
    {
        "gold", "enhance_stone", "transfer_stone", "crystal", "spirit_stone",
        "pill", "ore", "herb", "equip_blueprint", "equip", "attr_item",
        "revive_coin", "fatigue", "dungeon_count", "weekly_share",
        "realm_speed", "guild_tech", "territory_bonus",
    };

    // 已经接上发货的种类。P1-5c2 接保护券，P1-5c3 接红符；其余十六种的发货在
    // 后面的卡，没接的种类必须把 delivery_item_id 留空，免得清单上看着能买、
    // 实际发不出东西。
    public static readonly string[] DeliveredKinds = { "protect_ticket", "red_talisman" };

    /// <summary>ProtectItemId 是 enhance.json 里那唯一一种保护券的编号，本表要对得上。</summary>
    public sealed record Item(
        string Id,
        string Kind,
        string Name,
        long PriceYuanbao,
        string DeliveryItemId,
        int DeliveryCount,
        string Note)
    {
        /// <summary>这一种接上发货了吗。没接的走 delivery_pending，一分不扣。</summary>
        public bool Deliverable => DeliveryItemId.Length > 0;
    }

    public int Version { get; }
    public string Currency { get; }
    public string Note { get; }
    public IReadOnlyList<Item> Items { get; }

    ShopTable(int version, string currency, string note, IReadOnlyList<Item> items)
    {
        Version = version;
        Currency = currency;
        Note = note;
        Items = items;
    }

    /// <summary>按商品编号找。清单外的编号返回 null，调用方据此拒绝。</summary>
    public Item? Find(string itemId)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (string.Equals(Items[i].Id, itemId, StringComparison.Ordinal))
            {
                return Items[i];
            }
        }

        return null;
    }

    public static bool IsAllowedKind(string kind) =>
        Array.IndexOf(AllowedKinds, kind) >= 0;

    /// <summary>这一种接上发货了吗。清单外的种类一律 false。</summary>
    public static bool IsDeliveredKind(string kind) =>
        Array.IndexOf(DeliveredKinds, kind) >= 0;

    public static ShopTable Load(
        string path,
        CurrencyTable currencies,
        ItemTable itemTable,
        EnhanceTable enhance,
        TalismanTable talismans)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配置文件 " + path);
        }

        var text = File.ReadAllText(path);
        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(text, JsonOptions)
                ?? throw new ConfigException("配置是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配置不是合法 JSON：" + ex.Message);
        }

        if (!string.Equals(wire.Schema, WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException("shop.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("shop.json 的 version 必须是正整数");
        }

        // 商城只收元宝：币种得在币种表里，而且必须就是元宝。
        if (!string.Equals(wire.Currency, WrittenCurrency, StringComparison.Ordinal))
        {
            throw new ConfigException("shop.json 的 currency 必须是 " + WrittenCurrency
                + "，方案 12.1 写定商城只收元宝");
        }

        if (!currencies.All.Any(c => string.Equals(c.Id, wire.Currency, StringComparison.Ordinal)))
        {
            throw new ConfigException("shop.json 的 currency 不在 currency.json 里：" + wire.Currency);
        }

        var items = wire.Items ?? new List<WireItem>();
        if (items.Count == 0)
        {
            throw new ConfigException("shop.json 的 items 不能是空的");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Item>(items.Count);
        foreach (var item in items)
        {
            var id = (item.Id ?? "").Trim();
            var kind = (item.Kind ?? "").Trim();
            var name = (item.Name ?? "").Trim();

            if (id.Length == 0)
            {
                throw new ConfigException("shop.json 有一条商品没有 id");
            }

            if (!seen.Add(id))
            {
                throw new ConfigException("shop.json 的商品编号重复：" + id);
            }

            if (kind.Length == 0)
            {
                throw new ConfigException("shop.json 的商品 " + id + " 没有 kind");
            }

            if (Array.IndexOf(ForbiddenKinds, kind) >= 0)
            {
                throw new ConfigException("shop.json 上架了方案 12.1 写定绝不上架的种类："
                    + kind + "（商品 " + id + "）");
            }

            if (!IsAllowedKind(kind))
            {
                throw new ConfigException("shop.json 的商品 " + id + " 种类不在 12.1 的十八种里："
                    + kind);
            }

            if (name.Length == 0)
            {
                throw new ConfigException("shop.json 的商品 " + id + " 没有 name");
            }

            if (item.PriceYuanbao < 1)
            {
                throw new ConfigException("shop.json 的商品 " + id + " 标价必须是正整数");
            }

            var deliveryId = (item.DeliveryItemId ?? "").Trim();
            var deliveryCount = item.DeliveryCount;
            var delivered = IsDeliveredKind(kind);

            if (!delivered)
            {
                // 没接发货的种类不许留半个发货物：留着就会看着能买、实际发不出东西。
                if (deliveryId.Length != 0 || deliveryCount != 0)
                {
                    throw new ConfigException("shop.json 的商品 " + id + " 种类 " + kind
                        + " 还没接发货，delivery_item_id 必须留空、delivery_count 必须是 0");
                }
            }
            else
            {
                if (deliveryId.Length == 0)
                {
                    throw new ConfigException("shop.json 的商品 " + id + " 种类 " + kind
                        + " 已接发货，缺 delivery_item_id");
                }

                if (deliveryCount < 1)
                {
                    throw new ConfigException("shop.json 的商品 " + id
                        + " 每份数量必须是正整数");
                }

                var goods = itemTable.Find(deliveryId);
                if (goods is null)
                {
                    throw new ConfigException("shop.json 的商品 " + id
                        + " 发货物品不在 items.json 里：" + deliveryId);
                }

                // 方案 9.2 / 12.1：商城与通行证发的是同一种券，到账就绑定、不能上架。
                // 所以保护券的发货物只能指向 enhance.json 那一个编号，不许另立第二种。
                if (string.Equals(kind, "protect_ticket", StringComparison.Ordinal))
                {
                    if (!string.Equals(deliveryId, enhance.ProtectItemId, StringComparison.Ordinal))
                    {
                        throw new ConfigException("shop.json 的保护券必须发 enhance.json 的 "
                            + "protect_item_id（" + enhance.ProtectItemId + "），不许另立："
                            + deliveryId);
                    }

                    if (deliveryCount != 1)
                    {
                        throw new ConfigException("shop.json 的保护券按张卖，每份数量必须是 1："
                            + deliveryCount);
                    }

                    if (!string.Equals(goods.Kind, "voucher", StringComparison.Ordinal))
                    {
                        throw new ConfigException("shop.json 的保护券发货物必须是 voucher："
                            + goods.Kind);
                    }

                    if (goods.Bind != BindRule.Always)
                    {
                        throw new ConfigException("shop.json 的保护券发货物必须到账即绑："
                            + deliveryId);
                    }
                }
                else if (string.Equals(kind, "red_talisman", StringComparison.Ordinal))
                {
                    // 方案 12.1 写定「红符不是红药」：商城发的是符，回春丹、气血丹
                    // 仍从采药、炼丹、副本和特殊任务来。所以商城的红符只能发
                    // talisman.json 里的那一个编号，不许各自写一种。
                    var red = talismans.RedTalisman;
                    if (red is null)
                    {
                        throw new ConfigException("shop.json 上架了红符，但 talisman.json 没有 red 段");
                    }

                    if (!string.Equals(deliveryId, red.ItemId, StringComparison.Ordinal))
                    {
                        throw new ConfigException("shop.json 的红符必须发 talisman.json 的 "
                            + "red.item_id（" + red.ItemId + "），不许另立："
                            + deliveryId);
                    }

                    if (deliveryCount != 1)
                    {
                        throw new ConfigException("shop.json 的红符按张卖，每份数量必须是 1："
                            + deliveryCount);
                    }

                    if (!string.Equals(goods.Kind, "consumable", StringComparison.Ordinal))
                    {
                        throw new ConfigException("shop.json 的红符发货物必须是 consumable："
                            + goods.Kind);
                    }

                    if (goods.Bind != BindRule.Always)
                    {
                        throw new ConfigException("shop.json 的红符发货物必须到账即绑："
                            + deliveryId);
                    }
                }
                else if (!string.Equals(goods.Kind, "voucher", StringComparison.Ordinal)
                    && !string.Equals(goods.Kind, "consumable", StringComparison.Ordinal))
                {
                    // 后面几种符、卷轴、道具都是消耗或凭证类；接发货时照这条走，
                    // 免得把装备、材料混进商城（12.1 写定那些绝不上架）。
                    throw new ConfigException("shop.json 的商品 " + id
                        + " 发货物种类不是消耗或凭证：" + goods.Kind);
                }
            }

            result.Add(new Item(
                id, kind, name, item.PriceYuanbao, deliveryId, deliveryCount, (item.Note ?? "").Trim()));
        }

        return new ShopTable(wire.Version, wire.Currency!, (wire.Note ?? "").Trim(), result);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("items")] public List<WireItem>? Items { get; set; }
    }

    sealed class WireItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("price_yuanbao")] public long PriceYuanbao { get; set; }
        [JsonPropertyName("delivery_item_id")] public string? DeliveryItemId { get; set; }
        [JsonPropertyName("delivery_count")] public int DeliveryCount { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
