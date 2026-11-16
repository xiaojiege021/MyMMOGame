using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// N11 家园币商城正本（`config/home_coin_shop.json`，K1 起 v2，与 shop.json 同一套字段）。
/// 只许外观与便捷（方案 12.1 洞府与家园口径）：白名单 `categories`、黑名单 `forbidden`
/// 都在配置里，代码只做结构、范围与引用校验。发货方式同样按 `delivery.mode` 分发，
/// `none` 即还没接发货（回 delivery_pending，一分不扣）。
/// </summary>
public sealed class HomeCoinShopTable
{
    public const string WrittenSchema = "home-coin-shop/1";
    public const string WrittenCurrency = "home_coin";

    public sealed record Item(
        string Id, string Kind, string Name, long PriceHomeCoin, string Currency,
        DeliveryMode Mode, string DeliveryItemId, int DeliveryCount,
        long LimitPerCharacter, long LimitPerDay, string StockRule, long StockCount,
        bool OnSale, string Note)
    {
        public bool Deliverable => Mode != DeliveryMode.None;
    }

    public int Version { get; }
    public string Currency { get; }
    public IReadOnlyList<string> Categories { get; }
    public IReadOnlyList<string> Forbidden { get; }
    public IReadOnlyList<Item> Items { get; }

    HomeCoinShopTable(
        int version, string currency,
        IReadOnlyList<string> categories, IReadOnlyList<string> forbidden,
        IReadOnlyList<Item> items)
    {
        Version = version;
        Currency = currency;
        Categories = categories;
        Forbidden = forbidden;
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

        var categories = ShopTable.ReadNameList(wire.Categories, "categories", "home_coin_shop.json");
        var forbidden = ShopTable.ReadNameList(wire.Forbidden, "forbidden", "home_coin_shop.json");
        for (var i = 0; i < categories.Count; i++)
        {
            if (forbidden.Contains(categories[i]))
            {
                throw new ConfigException("home_coin_shop.json 的种类同时出现在两张表里："
                    + categories[i]);
            }
        }

        var list = new List<Item>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in wire.Items ?? [])
        {
            var id = (row.Id ?? "").Trim();
            if (id.Length == 0 || !seen.Add(id))
            {
                throw new ConfigException("家园币商品 id 重复或为空");
            }

            // category 是现在的字段，kind 是迁移期的旧名。
            var kind = (row.Category ?? "").Trim();
            var legacyKind = (row.Kind ?? "").Trim();
            if (kind.Length == 0)
            {
                kind = legacyKind;
            }
            else if (legacyKind.Length > 0 && !string.Equals(kind, legacyKind, StringComparison.Ordinal))
            {
                throw new ConfigException("家园币商品 " + id + " category 与 kind 不一致");
            }

            if (kind.Length == 0)
            {
                throw new ConfigException("家园币商品 " + id + " 没有 category");
            }

            if (forbidden.Contains(kind))
            {
                throw new ConfigException("家园币商城不许上架种类：" + kind);
            }

            if (!categories.Contains(kind))
            {
                throw new ConfigException("家园币商城种类不在 categories 里：" + kind);
            }

            var price = row.Price > 0 ? row.Price : row.PriceHomeCoin;
            if (price <= 0)
            {
                throw new ConfigException("price 必须为正：" + id);
            }

            var mode = ParseMode(row, id);
            var deliveryId = (row.Delivery?.ItemId ?? row.DeliveryItemId ?? "").Trim();
            var deliveryCount = row.Delivery?.Count ?? row.DeliveryCount;
            var passId = (row.Delivery?.PassId ?? "").Trim();
            var bundleId = (row.Delivery?.BundleId ?? "").Trim();

            if (row.Delivery is null && deliveryId.Length > 0)
            {
                mode = DeliveryMode.Item;
            }

            if (row.Delivery is not null
                && ((row.DeliveryItemId ?? "").Trim().Length > 0 || row.DeliveryCount != 0))
            {
                throw new ConfigException("家园币商品 " + id
                    + " 同时写了 delivery 与旧的 delivery_item_id / delivery_count");
            }

            // 家园币商城只发消耗与凭证（外观类等 B7+B8 接上再开）：
            // 状态发货那几种玩法不在这里，写了就起不来。
            if (mode != DeliveryMode.None && mode != DeliveryMode.Item)
            {
                throw new ConfigException("家园币商城不支持 " + mode
                    + " 这种发货方式（只有 none / item）：" + id);
            }

            if (mode == DeliveryMode.Item)
            {
                if (deliveryCount < 1)
                {
                    throw new ConfigException("家园币商品 " + id + " 每份数量必须是正整数");
                }

                if (items.Find(deliveryId) is null)
                {
                    throw new ConfigException("发货物不在 items.json：" + deliveryId);
                }

                if (passId.Length > 0 || bundleId.Length > 0)
                {
                    throw new ConfigException("家园币商品 " + id + " 是 item 发货，不该写别的参数");
                }
            }
            else if (deliveryId.Length != 0 || deliveryCount != 0
                || passId.Length != 0 || bundleId.Length != 0)
            {
                throw new ConfigException("没接发货时 delivery 里不许写发货物：" + id);
            }

            var perCharacter = row.Limit?.PerCharacter ?? 0;
            var perDay = row.Limit?.PerDay ?? 0;
            if (perCharacter < 0 || perDay < 0)
            {
                throw new ConfigException("家园币商品 " + id + " 限购必须是非负整数");
            }

            var rule = (row.Stock?.Rule ?? "unlimited").Trim();
            if (Array.IndexOf(ShopTable.StockRules, rule) < 0)
            {
                throw new ConfigException("家园币商品 " + id + " 的 stock.rule 不认识：" + rule);
            }

            var stockCount = row.Stock?.Count ?? 0;
            if (stockCount < 0)
            {
                throw new ConfigException("家园币商品 " + id + " 库存必须是非负整数");
            }

            if (!string.Equals(rule, "unlimited", StringComparison.Ordinal) && stockCount < 1)
            {
                throw new ConfigException("家园币商品 " + id + " 选了 " + rule + " 库存却没写 count");
            }

            if (string.Equals(rule, "unlimited", StringComparison.Ordinal) && stockCount != 0)
            {
                throw new ConfigException("家园币商品 " + id + " 不限库存时 count 必须是 0");
            }

            list.Add(new Item(id, kind, (row.Name ?? id).Trim(), price, currency, mode,
                deliveryId, deliveryCount, perCharacter, perDay, rule, stockCount,
                row.OnSale ?? true, (row.Note ?? "").Trim()));
        }

        if (list.Count == 0)
        {
            throw new ConfigException("home_coin_shop.json 至少一条商品");
        }

        return new HomeCoinShopTable(wire.Version, currency, categories, forbidden, list);
    }

    static DeliveryMode ParseMode(WireItem row, string id)
    {
        var mode = (row.Delivery?.Mode ?? "").Trim();
        if (mode.Length == 0)
        {
            return DeliveryMode.None;
        }

        return mode switch
        {
            "none" => DeliveryMode.None,
            "item" => DeliveryMode.Item,
            _ => throw new ConfigException("家园币商品 " + id + " 的 delivery.mode 不认识：" + mode),
        };
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; init; }
        [JsonPropertyName("version")] public int Version { get; init; }
        [JsonPropertyName("currency")] public string? Currency { get; init; }
        [JsonPropertyName("categories")] public List<string>? Categories { get; init; }
        [JsonPropertyName("forbidden")] public List<string>? Forbidden { get; init; }
        [JsonPropertyName("items")] public List<WireItem>? Items { get; init; }
    }

    sealed class WireItem
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("category")] public string? Category { get; init; }
        [JsonPropertyName("price")] public long Price { get; init; }
        [JsonPropertyName("delivery")] public ShopTable.WireDelivery? Delivery { get; init; }
        [JsonPropertyName("limit")] public ShopTable.WireLimit? Limit { get; init; }
        [JsonPropertyName("stock")] public ShopTable.WireStock? Stock { get; init; }
        [JsonPropertyName("on_sale")] public bool? OnSale { get; init; }
        [JsonPropertyName("note")] public string? Note { get; init; }

        // 迁移期兼容字段：K1 收口后随配置一起删。
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("price_home_coin")] public long PriceHomeCoin { get; init; }
        [JsonPropertyName("delivery_item_id")] public string? DeliveryItemId { get; init; }
        [JsonPropertyName("delivery_count")] public int DeliveryCount { get; init; }
    }
}
