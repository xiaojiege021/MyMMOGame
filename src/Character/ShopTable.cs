using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 买到手变成什么。取值是**代码能力**，不是内容：配置只能在这几个里选，
/// 想加新玩法得先有对应实现，写个没人认的字符串在加载期就起不来。
/// `None` 表示还没接发货：能看能点，回 delivery_pending，一分不扣。
/// </summary>
public enum DeliveryMode
{
    None,
    Item,
    SeasonPass,
    MonthlyBundle,
    SecondSlot,
    BagExpand,
}

/// <summary>
/// 商城清单的唯一正本（`config/shop.json`，K1 起 v11）。方案 12.1 定死了能上架的
/// 十八种与「绝不上架」那一串，这两张表都在配置里（`categories` / `forbidden`），
/// 代码只做结构与引用校验，不再写第二份种类清单。每件商品自己的 `delivery.mode`
/// 就是「接没接发货」的答案——原来的 DeliveredKinds 表没有第二份了。
/// 标价方案没给数字，只存在这个文件里，代码里没有第二套价格。
/// </summary>
public sealed class ShopTable
{
    public const string WrittenSchema = "shop-table/1";

    // 方案第 12.1 写定：商城只收元宝。币种编号在 currency.json 里是 yuanbao。
    public const string WrittenCurrency = "yuanbao";

    // 配置设计第 3 节：限购只有这两种口径，库存只有这三种口径。
    public static readonly string[] StockRules = { "unlimited", "daily", "weekly" };

    /// <summary>一条商品。Kind 就是配置里的 category——协议字段名沿用旧名，值不变。</summary>
    public sealed record Item(
        string Id,
        string Kind,
        string Name,
        long PriceYuanbao,
        string Currency,
        DeliveryMode Mode,
        string DeliveryItemId,
        int DeliveryCount,
        string PassId,
        string BundleId,
        long LimitPerCharacter,
        long LimitPerDay,
        string StockRule,
        long StockCount,
        bool OnSale,
        string Note,
        int RequireRealmIndex)
    {
        /// <summary>这一条接上发货了吗。没接的走 delivery_pending，一分不扣。</summary>
        public bool Deliverable => Mode != DeliveryMode.None;

        /// <summary>方案 12.1「到达金仙才能买金仙外观」：0 表示这条商品没有境界门槛。</summary>
        public bool RealmGated => RequireRealmIndex > 0;
    }

    public int Version { get; }

    public string Currency { get; }

    public string Note { get; }

    /// <summary>方案 12.1 的十八种，顺序照原表，内容在配置里。</summary>
    public IReadOnlyList<string> Categories { get; }

    /// <summary>12.1 写定绝不上架的那一串，内容在配置里。</summary>
    public IReadOnlyList<string> Forbidden { get; }

    public IReadOnlyList<Item> Items { get; }

    /// <summary>已经接了发货的商品条数（只进启动日志与只读入口，不参与判定）。</summary>
    public int DeliveredCount
    {
        get
        {
            var n = 0;
            for (var i = 0; i < Items.Count; i++)
            {
                if (Items[i].Deliverable)
                {
                    n++;
                }
            }

            return n;
        }
    }

    ShopTable(
        int version,
        string currency,
        string note,
        IReadOnlyList<string> categories,
        IReadOnlyList<string> forbidden,
        IReadOnlyList<Item> items)
    {
        Version = version;
        Currency = currency;
        Note = note;
        Categories = categories;
        Forbidden = forbidden;
        Items = items;
    }

    /// <summary>回给日志与只读入口的写法，与配置里的字面量一字不差。</summary>
    public static string ModeText(DeliveryMode mode) => mode switch
    {
        DeliveryMode.None => "none",
        DeliveryMode.Item => "item",
        DeliveryMode.SeasonPass => "season_pass",
        DeliveryMode.MonthlyBundle => "monthly_bundle",
        DeliveryMode.SecondSlot => "second_slot",
        DeliveryMode.BagExpand => "bag_expand",
        _ => "none",
    };

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

    public static ShopTable Load(
        string path,
        CurrencyTable currencies,
        ItemTable itemTable,
        EnhanceTable enhance,
        TalismanTable talismans,
        ReviveScrollTable scrolls,
        ExpBoosterTable boosters,
        UnbindTable unbinds)
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

        var categories = ReadNameList(wire.Categories, "categories", "shop.json");
        var forbidden = ReadNameList(wire.Forbidden, "forbidden", "shop.json");

        // 两张表打架就起不来：同一门类既许又禁，谁也说不清该不该上架。
        for (var i = 0; i < categories.Count; i++)
        {
            if (forbidden.Contains(categories[i]))
            {
                throw new ConfigException("shop.json 的种类同时出现在 categories 与 forbidden："
                    + categories[i]);
            }
        }

        var rows = wire.Items ?? new List<WireItem>();
        if (rows.Count == 0)
        {
            throw new ConfigException("shop.json 的 items 不能是空的");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Item>(rows.Count);
        foreach (var row in rows)
        {
            var item = ReadItem(row, categories, forbidden, itemTable);
            if (!seen.Add(item.Id))
            {
                throw new ConfigException("shop.json 的商品编号重复：" + item.Id);
            }

            // 每一门类都有方案写定的编号出处，接发货时只能指向那一件，不许另立第二种。
            CheckReference(item, itemTable, enhance, talismans, scrolls, boosters, unbinds);
            result.Add(item);
        }

        return new ShopTable(wire.Version, wire.Currency!, (wire.Note ?? "").Trim(),
            categories, forbidden, result);
    }

    /// <summary>白名单 / 黑名单：非空、无重复、不许空串。</summary>
    internal static List<string> ReadNameList(List<string>? wire, string field, string file)
    {
        var list = wire ?? new List<string>();
        if (list.Count == 0)
        {
            throw new ConfigException(file + " 缺 " + field + " 表：这张表是唯一正本，不能空着");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(list.Count);
        foreach (var raw in list)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0)
            {
                throw new ConfigException(file + " 的 " + field + " 里有空项");
            }

            if (!seen.Add(value))
            {
                throw new ConfigException(file + " 的 " + field + " 重复：" + value);
            }

            result.Add(value);
        }

        return result;
    }

    /// <summary>读一条商品并校验结构与范围；引用是否对得上由 CheckReference 管。</summary>
    internal static Item ReadItem(
        WireItem row,
        IReadOnlyList<string> categories,
        IReadOnlyList<string> forbidden,
        ItemTable itemTable)
    {
        var id = (row.Id ?? "").Trim();
        if (id.Length == 0)
        {
            throw new ConfigException("shop.json 有一条商品没有 id");
        }

        // category 是现在的字段，kind 是迁移期的旧名：两个都给且不一样就起不来。
        var kind = (row.Category ?? "").Trim();
        var legacyKind = (row.Kind ?? "").Trim();
        if (kind.Length == 0)
        {
            kind = legacyKind;
        }
        else if (legacyKind.Length > 0 && !string.Equals(kind, legacyKind, StringComparison.Ordinal))
        {
            throw new ConfigException("shop.json 的商品 " + id
                + " category 与旧字段 kind 不一致：" + kind + " / " + legacyKind);
        }

        if (kind.Length == 0)
        {
            throw new ConfigException("shop.json 的商品 " + id + " 没有 category");
        }

        if (forbidden.Contains(kind))
        {
            throw new ConfigException("shop.json 上架了方案 12.1 写定绝不上架的种类："
                + kind + "（商品 " + id + "）");
        }

        if (!categories.Contains(kind))
        {
            throw new ConfigException("shop.json 的商品 " + id + " 种类不在 categories 里：" + kind);
        }

        var name = (row.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new ConfigException("shop.json 的商品 " + id + " 没有 name");
        }

        var price = row.Price > 0 ? row.Price : row.PriceYuanbao;
        if (price < 1)
        {
            throw new ConfigException("shop.json 的商品 " + id + " 标价必须是正整数");
        }

        var mode = ParseMode(row, id);
        var deliveryId = (row.Delivery?.ItemId ?? row.DeliveryItemId ?? "").Trim();
        var deliveryCount = row.Delivery?.Count ?? row.DeliveryCount;
        var passId = (row.Delivery?.PassId ?? "").Trim();
        var bundleId = (row.Delivery?.BundleId ?? "").Trim();

        // B7+B8：方案 12.1 的购买门槛（「到达金仙才能买金仙外观」）。
        // 门槛是配置数值，代码只把它与角色的段序号比大小；不写就是没有门槛。
        var requireRealm = row.RequireRealm?.EnumIndexMin ?? 0;
        if (requireRealm < 0 || requireRealm > 99)
        {
            throw new ConfigException("shop.json 的商品 " + id
                + " 的 require_realm.enum_index_min 必须在 0 到 99 之间");
        }

        // 旧字段还在时按「进背包」解释；新旧两套同时出现视为迁移没做完。
        if (row.Delivery is null && deliveryId.Length > 0)
        {
            mode = DeliveryMode.Item;
        }

        if (row.Delivery is not null
            && ((row.DeliveryItemId ?? "").Trim().Length > 0 || row.DeliveryCount != 0))
        {
            throw new ConfigException("shop.json 的商品 " + id
                + " 同时写了 delivery 与旧的 delivery_item_id / delivery_count，只能留一套");
        }

        var limit = row.Limit;
        var perCharacter = limit?.PerCharacter ?? 0;
        var perDay = limit?.PerDay ?? 0;
        if (perCharacter < 0 || perDay < 0)
        {
            throw new ConfigException("shop.json 的商品 " + id + " 限购必须是非负整数");
        }

        var rule = (row.Stock?.Rule ?? "unlimited").Trim();
        if (Array.IndexOf(StockRules, rule) < 0)
        {
            throw new ConfigException("shop.json 的商品 " + id + " 的 stock.rule 必须是 "
                + string.Join(" / ", StockRules) + "：" + rule);
        }

        var stockCount = row.Stock?.Count ?? 0;
        if (stockCount < 0)
        {
            throw new ConfigException("shop.json 的商品 " + id + " 库存必须是非负整数");
        }

        if (!string.Equals(rule, "unlimited", StringComparison.Ordinal) && stockCount < 1)
        {
            throw new ConfigException("shop.json 的商品 " + id + " 选了 " + rule
                + " 库存却没写 count");
        }

        if (string.Equals(rule, "unlimited", StringComparison.Ordinal) && stockCount != 0)
        {
            throw new ConfigException("shop.json 的商品 " + id + " 不限库存时 count 必须是 0");
        }

        // 发货参数：缺的补不上、多的不许留，免得清单看着能买、实际发不出东西。
        switch (mode)
        {
            case DeliveryMode.Item:
                if (deliveryId.Length == 0)
                {
                    throw new ConfigException("shop.json 的商品 " + id + " 是 item 发货，缺 item_id");
                }

                if (deliveryCount < 1)
                {
                    throw new ConfigException("shop.json 的商品 " + id + " 每份数量必须是正整数");
                }

                if (passId.Length > 0 || bundleId.Length > 0)
                {
                    throw new ConfigException("shop.json 的商品 " + id
                        + " 是 item 发货，不该写 pass_id / bundle_id");
                }

                break;

            case DeliveryMode.SeasonPass:
                if (!string.Equals(passId, "current", StringComparison.Ordinal))
                {
                    // 客户端表达不了「买哪一季」，配置也只能指向当前季。
                    throw new ConfigException("shop.json 的商品 " + id
                        + " 的 pass_id 只能是 current：" + (passId.Length == 0 ? "（空）" : passId));
                }

                RequireNoGoods(id, deliveryId, deliveryCount, bundleId);
                break;

            case DeliveryMode.MonthlyBundle:
                if (!string.Equals(bundleId, "current", StringComparison.Ordinal))
                {
                    throw new ConfigException("shop.json 的商品 " + id
                        + " 的 bundle_id 只能是 current：" + (bundleId.Length == 0 ? "（空）" : bundleId));
                }

                RequireNoGoods(id, deliveryId, deliveryCount, passId);
                break;

            case DeliveryMode.SecondSlot:
                RequireNoGoods(id, deliveryId, deliveryCount, passId);
                if (bundleId.Length > 0)
                {
                    throw new ConfigException("shop.json 的商品 " + id + " 是 second_slot，不该写 bundle_id");
                }

                break;

            case DeliveryMode.BagExpand:
                // 扩容是状态发货：加格不落包，配了发货物就会看着能买、实际多发一件东西。
                RequireNoGoods(id, deliveryId, deliveryCount, passId);
                if (bundleId.Length > 0)
                {
                    throw new ConfigException("shop.json 的商品 " + id + " 是 bag_expand，不该写 bundle_id");
                }

                break;

            default:
                // none：不许留半个发货物，留着就会看着能买、实际发不出东西。
                if (deliveryId.Length != 0 || deliveryCount != 0
                    || passId.Length != 0 || bundleId.Length != 0)
                {
                    throw new ConfigException("shop.json 的商品 " + id
                        + " 还是 none（没接发货），delivery 里不许写发货物");
                }

                break;
        }

        // 物品存在性：模式为 item 时发货物必须真在物品表里。
        if (mode == DeliveryMode.Item && itemTable.Find(deliveryId) is null)
        {
            throw new ConfigException("shop.json 的商品 " + id
                + " 发货物品不在 items.json 里：" + deliveryId);
        }

        return new Item(
            id, kind, name, price, (row.Currency ?? WrittenCurrency).Trim(), mode,
            deliveryId, deliveryCount, passId, bundleId,
            perCharacter, perDay, rule, stockCount,
            row.OnSale ?? true, (row.Note ?? "").Trim(), requireRealm);
    }

    static void RequireNoGoods(string id, string deliveryId, int deliveryCount, string other)
    {
        if (deliveryId.Length != 0 || deliveryCount != 0 || other.Length != 0)
        {
            throw new ConfigException("shop.json 的商品 " + id
                + " 是状态发货，不该有发货物 / 别的参数");
        }
    }

    static DeliveryMode ParseMode(WireItem row, string id)
    {
        var mode = (row.Delivery?.Mode ?? "").Trim();
        if (mode.Length == 0)
        {
            // 旧写法（只有 delivery_item_id）由调用方按 item 解释；新旧都没有就是 none。
            return DeliveryMode.None;
        }

        return mode switch
        {
            "none" => DeliveryMode.None,
            "item" => DeliveryMode.Item,
            "season_pass" => DeliveryMode.SeasonPass,
            "monthly_bundle" => DeliveryMode.MonthlyBundle,
            "second_slot" => DeliveryMode.SecondSlot,
            "bag_expand" => DeliveryMode.BagExpand,
            _ => throw new ConfigException("shop.json 的商品 " + id + " 的 delivery.mode 不认识："
                + mode + "（现在只支持 none / item / season_pass / monthly_bundle / second_slot"
                + " / bag_expand）"),
        };
    }

    /// <summary>
    /// 方案写定的「同一种就只能是这一件」：保护券、红符、蓝符、复活卷轴、解绑道具、
    /// 限时经验道具、传送符各有唯一出处，配置写成别的编号就起不来。这里是引用校验，
    /// 不是内容——编号本身仍在各自的配置里。
    /// </summary>
    static void CheckReference(
        Item item,
        ItemTable items,
        EnhanceTable enhance,
        TalismanTable talismans,
        ReviveScrollTable scrolls,
        ExpBoosterTable boosters,
        UnbindTable unbinds)
    {
        if (item.Mode != DeliveryMode.Item)
        {
            return;
        }

        var goods = items.Find(item.DeliveryItemId);
        if (goods is null)
        {
            throw new ConfigException("shop.json 的商品 " + item.Id
                + " 发货物品不在 items.json 里：" + item.DeliveryItemId);
        }

        switch (item.Kind)
        {
            case "protect_ticket":
                RequireSame(item, enhance.ProtectItemId, "enhance.json 的 protect_item_id");
                RequireCountOne(item);
                RequireGoods(item, goods.Kind, "voucher");
                RequireBound(item, goods);
                break;

            case "red_talisman":
                RequireSame(item, talismans.RedTalisman?.ItemId, "talisman.json 的 red.item_id");
                RequireCountOne(item);
                RequireGoods(item, goods.Kind, "consumable");
                RequireBound(item, goods);
                break;

            case "blue_talisman":
                RequireSame(item, talismans.BlueTalisman?.ItemId, "talisman.json 的 blue.item_id");
                RequireCountOne(item);
                RequireGoods(item, goods.Kind, "consumable");
                RequireBound(item, goods);
                break;

            case "revive_scroll":
                RequireSame(item, scrolls.ReviveScroll.ItemId, "revive_scroll.json 的 scroll.item_id");
                RequireCountOne(item);
                RequireGoods(item, goods.Kind, "consumable");
                RequireBound(item, goods);
                break;

            case "exp_booster":
                RequireSame(item, boosters.ExpBooster.ItemId, "exp_booster.json 的 booster.item_id");
                RequireCountOne(item);
                RequireGoods(item, goods.Kind, "consumable");
                RequireBound(item, goods);
                break;

            case "unbind_item":
                RequireSame(item, unbinds.Unbind.ItemId, "unbind_item.json 的 unbind.item_id");
                RequireCountOne(item);
                RequireGoods(item, goods.Kind, "consumable");
                RequireBound(item, goods);
                break;

            case "teleport_talisman":
                RequireCountOne(item);
                RequireGoods(item, goods.Kind, "consumable");
                RequireBound(item, goods);
                break;

            default:
                // 后面几类（外观、祖府、宗门展示）接发货时照这条走：
                // 免得把装备、材料混进商城（12.1 写定那些绝不上架）。
                // B7+B8：五类外观与洞府摆件是「发一件对应种类的物品进背包」，
                // 种类必须就是这一门类本身（不许拿消耗品冒充外观），且到账即绑。
                if (string.Equals(item.Kind, goods.Kind, StringComparison.Ordinal)
                    && WardrobeRepository.IsAppearanceKind(goods.Kind)
                        || string.Equals(item.Kind, "home_decor", StringComparison.Ordinal)
                        && string.Equals(goods.Kind, "home_decor", StringComparison.Ordinal))
                {
                    RequireCountOne(item);
                    RequireBound(item, goods);
                    break;
                }

                if (!string.Equals(goods.Kind, "voucher", StringComparison.Ordinal)
                    && !string.Equals(goods.Kind, "consumable", StringComparison.Ordinal))
                {
                    throw new ConfigException("shop.json 的商品 " + item.Id
                        + " 发货物种类不是消耗或凭证：" + goods.Kind);
                }

                break;
        }
    }

    static void RequireSame(Item item, string? expected, string source)
    {
        if (string.IsNullOrEmpty(expected))
        {
            throw new ConfigException("shop.json 上架了 " + item.Kind + "，但 " + source + " 没有这一段");
        }

        if (!string.Equals(item.DeliveryItemId, expected, StringComparison.Ordinal))
        {
            throw new ConfigException("shop.json 的 " + item.Kind + " 必须发 " + source
                + "（" + expected + "），不许另立：" + item.DeliveryItemId);
        }
    }

    static void RequireCountOne(Item item)
    {
        if (item.DeliveryCount != 1)
        {
            throw new ConfigException("shop.json 的 " + item.Kind + " 按件卖，每份数量必须是 1："
                + item.DeliveryCount);
        }
    }

    static void RequireGoods(Item item, string? kind, string expected)
    {
        if (!string.Equals(kind, expected, StringComparison.Ordinal))
        {
            throw new ConfigException("shop.json 的 " + item.Kind + " 发货物必须是 "
                + expected + "：" + kind);
        }
    }

    static void RequireBound(Item item, ItemDef goods)
    {
        if (goods.Bind != BindRule.Always)
        {
            throw new ConfigException("shop.json 的 " + item.Kind
                + " 发货物必须到账即绑：" + item.DeliveryItemId);
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    internal sealed class Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
        [JsonPropertyName("forbidden")] public List<string>? Forbidden { get; set; }
        [JsonPropertyName("items")] public List<WireItem>? Items { get; set; }
    }

    internal sealed class WireItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("price")] public long Price { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("delivery")] public WireDelivery? Delivery { get; set; }
        [JsonPropertyName("limit")] public WireLimit? Limit { get; set; }
        [JsonPropertyName("stock")] public WireStock? Stock { get; set; }
        [JsonPropertyName("on_sale")] public bool? OnSale { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }

        // B7+B8：购买门槛（方案 12.1「到达金仙才能买金仙外观」）。
        [JsonPropertyName("require_realm")] public WireRequireRealm? RequireRealm { get; set; }

        // 迁移期兼容字段（配置设计第 3 节）：K1 收口后随配置一起删。
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("price_yuanbao")] public long PriceYuanbao { get; set; }
        [JsonPropertyName("delivery_item_id")] public string? DeliveryItemId { get; set; }
        [JsonPropertyName("delivery_count")] public int DeliveryCount { get; set; }
    }

    internal sealed class WireDelivery
    {
        [JsonPropertyName("mode")] public string? Mode { get; set; }
        [JsonPropertyName("item_id")] public string? ItemId { get; set; }
        [JsonPropertyName("count")] public int? Count { get; set; }
        [JsonPropertyName("pass_id")] public string? PassId { get; set; }
        [JsonPropertyName("bundle_id")] public string? BundleId { get; set; }
    }

    /// <summary>购买门槛：境界段序号下限（1 起）。方案 12.1 的门槛数值只存在配置里。</summary>
    internal sealed class WireRequireRealm
    {
        [JsonPropertyName("enum_index_min")] public int EnumIndexMin { get; set; }
    }

    internal sealed class WireLimit
    {
        [JsonPropertyName("per_character")] public long PerCharacter { get; set; }
        [JsonPropertyName("per_day")] public long PerDay { get; set; }
    }

    internal sealed class WireStock
    {
        [JsonPropertyName("rule")] public string? Rule { get; set; }
        [JsonPropertyName("count")] public long Count { get; set; }
    }
}
