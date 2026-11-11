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

    public sealed record Item(string Id, string Kind, string Name, long PriceYuanbao, string Note);

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

    public static ShopTable Load(string path, CurrencyTable currencies)
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

            result.Add(new Item(id, kind, name, item.PriceYuanbao, (item.Note ?? "").Trim()));
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
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
