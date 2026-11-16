using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>N11 家园币商城正本。只许外观与便捷，战力与材料类加载期拒绝。</summary>
public sealed class HomeCoinShopTable
{
    public const string WrittenSchema = "home-coin-shop/1";
    public const string WrittenCurrency = "home_coin";

    public static readonly string[] AllowedKinds =
    {
        "outfit", "weapon_skin", "realm_skin", "flight_trail", "mount_skin",
        "home_decor", "guild_display", "bag_expand", "teleport_talisman",
    };

    public static readonly string[] ForbiddenKinds =
    {
        "gold", "enhance_stone", "transfer_stone", "crystal", "spirit_stone",
        "pill", "ore", "herb", "equip_blueprint", "equip", "attr_item",
        "revive_coin", "fatigue", "dungeon_count", "weekly_share",
        "realm_speed", "guild_tech", "territory_bonus",
        "red_talisman", "blue_talisman", "revive_scroll", "unbind_item",
        "exp_booster", "monthly_bundle", "second_slot", "season_pass", "protect_ticket",
    };

    public static readonly string[] DeliveredKinds = { "teleport_talisman" };

    public sealed record Item(
        string Id, string Kind, string Name, long PriceHomeCoin,
        string DeliveryItemId, int DeliveryCount, string Note)
    {
        public bool Deliverable => DeliveryItemId.Length > 0 && DeliveryCount > 0;
    }

    public int Version { get; }
    public string Currency { get; }
    public IReadOnlyList<Item> Items { get; }

    HomeCoinShopTable(int version, string currency, IReadOnlyList<Item> items)
    {
        Version = version;
        Currency = currency;
        Items = items;
    }

    public Item? Find(string itemId)
    {
        foreach (var item in Items)
        {
            if (string.Equals(item.Id, itemId, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    public static HomeCoinShopTable Load(
        string path, CurrencyTable currencies, ItemTable items)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到家园币商城表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("home_coin_shop.json 是空的");
        }
        catch (JsonException ex)
        {
            throw new ConfigException("home_coin_shop.json 不是合法 JSON：" + ex.Message);
        }

        if ((wire.Schema ?? "").Trim() != WrittenSchema)
        {
            throw new ConfigException("home_coin_shop.json schema 必须是 " + WrittenSchema);
        }

        var currency = (wire.Currency ?? WrittenCurrency).Trim();
        if (!string.Equals(currency, WrittenCurrency, StringComparison.Ordinal)
            || currencies.Find(currency) is null)
        {
            throw new ConfigException("家园币商城币种必须是 currency.json 里的 home_coin");
        }

        var list = new List<Item>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in wire.Items ?? [])
        {
            var id = (row.Id ?? "").Trim();
            var kind = (row.Kind ?? "").Trim();
            if (id.Length == 0 || !seen.Add(id))
            {
                throw new ConfigException("家园币商品 id 重复或为空");
            }

            if (Array.IndexOf(ForbiddenKinds, kind) >= 0)
            {
                throw new ConfigException("家园币商城不许上架种类：" + kind);
            }

            if (Array.IndexOf(AllowedKinds, kind) < 0)
            {
                throw new ConfigException("家园币商城种类不在白名单：" + kind);
            }

            var price = row.PriceHomeCoin;
            if (price <= 0)
            {
                throw new ConfigException("price_home_coin 必须为正：" + id);
            }

            var deliveryId = (row.DeliveryItemId ?? "").Trim();
            var deliveryCount = row.DeliveryCount;
            if (deliveryId.Length > 0)
            {
                if (Array.IndexOf(DeliveredKinds, kind) < 0)
                {
                    throw new ConfigException("该种类尚未接发货：" + kind);
                }

                if (deliveryCount <= 0 || items.Find(deliveryId) is null)
                {
                    throw new ConfigException("发货物不在 items.json：" + deliveryId);
                }
            }
            else if (deliveryCount != 0)
            {
                throw new ConfigException("没接发货时 delivery_count 必须为 0：" + id);
            }

            list.Add(new Item(id, kind, (row.Name ?? id).Trim(), price,
                deliveryId, deliveryCount, (row.Note ?? "").Trim()));
        }

        if (list.Count == 0)
        {
            throw new ConfigException("home_coin_shop.json 至少一条商品");
        }

        return new HomeCoinShopTable(wire.Version, currency, list);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("currency")]
        public string? Currency { get; init; }

        [JsonPropertyName("items")]
        public List<ItemWire>? Items { get; init; }
    }

    sealed class ItemWire
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("price_home_coin")]
        public long PriceHomeCoin { get; init; }

        [JsonPropertyName("delivery_item_id")]
        public string? DeliveryItemId { get; init; }

        [JsonPropertyName("delivery_count")]
        public int DeliveryCount { get; init; }

        [JsonPropertyName("note")]
        public string? Note { get; init; }
    }
}