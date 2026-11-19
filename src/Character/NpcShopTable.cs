using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// K2+Q13：NPC 商店的唯一正本（config/npc_shop.json）。
///
/// 这一份文件同时管四件事，因为它们本来就是同一个面板：
///   · shops   城镇 / 宗门 NPC 卖什么、多少钱、每日库存、每人限购；
///   · sell    NPC 回收白名单（不在名单里的一律不能卖）；
///   · buyback 回购窗口（留几笔、按卖价的百分之几、几天过期）；
///   · repair  修理单价与按品阶的耐久上限。
///
/// 加载期硬校验，缺一样就起不来（开发架构第 7 节「配置谁读」）：
///   · 商品必须在物品表里，种类必须在 shelf_kinds 里，编号不许出现在 forbidden_items 里
///     ——「NPC 不卖战力材料」是结构约束，不是文案约定；
///   · npc_id 必须在 npc.json 里，而且那位 NPC 的 shop_id 指回来就是这家店；
///   · 币种必须在 currency.json 里；价格、库存、限购、回购、修理的每个数都要在区间内；
///   · 物品表里出现过的每个品阶都要有耐久上限，否则穿到那件就修不了。
/// </summary>
public sealed class NpcShopTable
{
    public const string WrittenSchema = "npc-shop-table/2";

    /// <summary>一个货架条目。StockPerDay=0 表示不限量；LimitPerCharacter=0 表示不限购。</summary>
    public sealed record Goods(string ItemId, long Price, int StockPerDay, int LimitPerCharacter);

    public sealed record Shop(
        string Id, string NpcId, string Currency, string Refresh, IReadOnlyList<Goods> Items);

    /// <summary>一条回收价。只有白名单里的编号能卖给 NPC。</summary>
    public sealed record SellLine(string ItemId, long Price);

    public sealed record SellSection(string Currency, IReadOnlyList<SellLine> Items);

    /// <summary>回购窗口：每人每店留 Keep 笔、按 PricePct 回购、ExpireDays 天后作废。</summary>
    public sealed record BuybackWindow(int Keep, int PricePct, int ExpireDays);

    public sealed record RepairRule(
        string Currency, long PricePerPoint, IReadOnlyDictionary<string, int> DurabilityMax);

    public int Version { get; }

    public string Note { get; }

    public IReadOnlyList<string> ShelfKinds { get; }

    public IReadOnlyList<string> ForbiddenItems { get; }

    public IReadOnlyList<Shop> Shops { get; }

    public SellSection Sell { get; }

    public BuybackWindow Buyback { get; }

    public RepairRule Repair { get; }

    NpcShopTable(
        int version,
        string note,
        IReadOnlyList<string> shelfKinds,
        IReadOnlyList<string> forbiddenItems,
        IReadOnlyList<Shop> shops,
        SellSection sell,
        BuybackWindow buyback,
        RepairRule repair)
    {
        Version = version;
        Note = note;
        ShelfKinds = shelfKinds;
        ForbiddenItems = forbiddenItems;
        Shops = shops;
        Sell = sell;
        Buyback = buyback;
        Repair = repair;
    }

    public Shop? FindShop(string shopId)
    {
        for (var i = 0; i < Shops.Count; i++)
        {
            if (string.Equals(Shops[i].Id, shopId, StringComparison.Ordinal))
            {
                return Shops[i];
            }
        }

        return null;
    }

    /// <summary>按 NPC 找店：客户端拿着 npc.json 的 shop_id 进来，只认这一条路。</summary>
    public Shop? FindShopByNpc(string npcId)
    {
        for (var i = 0; i < Shops.Count; i++)
        {
            if (string.Equals(Shops[i].NpcId, npcId, StringComparison.Ordinal))
            {
                return Shops[i];
            }
        }

        return null;
    }

    public Goods? FindGoods(Shop shop, string itemId)
    {
        for (var i = 0; i < shop.Items.Count; i++)
        {
            if (string.Equals(shop.Items[i].ItemId, itemId, StringComparison.Ordinal))
            {
                return shop.Items[i];
            }
        }

        return null;
    }

    public SellLine? FindSell(string itemId)
    {
        for (var i = 0; i < Sell.Items.Count; i++)
        {
            if (string.Equals(Sell.Items[i].ItemId, itemId, StringComparison.Ordinal))
            {
                return Sell.Items[i];
            }
        }

        return null;
    }

    /// <summary>这一品阶的耐久上限。加载期已经保证物品表里出现过的品阶都有值。</summary>
    public int DurabilityMaxOf(string grade) =>
        Repair.DurabilityMax.TryGetValue(grade ?? "", out var max) ? max : 0;

    /// <summary>回购单价：卖价 × PricePct / 100，向下取整。取整方式只在这里一处。</summary>
    public long BuybackPriceOf(long sellPrice) => sellPrice * Buyback.PricePct / 100;

    public static NpcShopTable Load(
        string path,
        CurrencyTable currencies,
        ItemTable items,
        string npcPath = "config/npc.json")
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
            throw new ConfigException("npc_shop.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 2)
        {
            throw new ConfigException("npc_shop.json 的 version 必须 ≥ 2（K2+Q13 的四段）");
        }

        var shelfKinds = ReadKinds(wire.ShelfKinds);
        var forbidden = ReadTextList(wire.ForbiddenItems, "forbidden_items");

        var npcShops = ReadNpcShopIds(npcPath);

        var shops = ReadShops(wire.Shops, shelfKinds, forbidden, currencies, items, npcShops);
        var sell = ReadSell(wire.Sell, currencies, items);
        var buyback = ReadBuyback(wire.Buyback);
        var repair = ReadRepair(wire.Repair, currencies, items);

        return new NpcShopTable(wire.Version, (wire.Note ?? "").Trim(), shelfKinds, forbidden,
            shops, sell, buyback, repair);
    }

    /// <summary>shelf_kinds 是白名单：货架上只许出现这几种。空表等于「什么都不能卖」。</summary>
    static List<string> ReadKinds(List<string>? wire)
    {
        var list = ReadTextList(wire, "shelf_kinds");
        if (list.Count == 0)
        {
            throw new ConfigException("npc_shop.json 的 shelf_kinds 不能空着：它是货架白名单");
        }

        return list;
    }

    static List<string> ReadTextList(List<string>? wire, string field)
    {
        var list = wire ?? new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(list.Count);
        foreach (var raw in list)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0)
            {
                throw new ConfigException("npc_shop.json 的 " + field + " 里有空串");
            }

            if (!seen.Add(value))
            {
                throw new ConfigException("npc_shop.json 的 " + field + " 重复：" + value);
            }

            result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// 读 npc.json 的 npc_id → shop_id 对应表。这一份不是选配：NPC 商店的商品挂在 NPC 身上，
    /// 找不到人就没有入口。文件缺失按「没有可校验的 NPC」处理，让不带 npc.json 的老卡环境照常起。
    /// </summary>
    static Dictionary<string, string> ReadNpcShopIds(string npcPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(npcPath))
        {
            return map;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(npcPath));
            if (!doc.RootElement.TryGetProperty("npcs", out var rows)
                || rows.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var shopId = row.TryGetProperty("shop_id", out var shop)
                    && shop.ValueKind == JsonValueKind.String
                        ? (shop.GetString() ?? "").Trim()
                        : "";
                map[(id.GetString() ?? "").Trim()] = shopId;
            }
        }
        catch (JsonException ex)
        {
            throw new ConfigException("npc.json 不是合法 JSON：" + ex.Message);
        }

        return map;
    }

    static List<Shop> ReadShops(
        List<WireShop>? wire,
        IReadOnlyList<string> shelfKinds,
        IReadOnlyList<string> forbidden,
        CurrencyTable currencies,
        ItemTable items,
        IReadOnlyDictionary<string, string> npcShops)
    {
        var rows = wire ?? new List<WireShop>();
        if (rows.Count == 0)
        {
            throw new ConfigException("npc_shop.json 的 shops 不能是空的");
        }

        var seenShop = new HashSet<string>(StringComparer.Ordinal);
        var seenNpc = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Shop>(rows.Count);

        foreach (var row in rows)
        {
            var id = (row.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("npc_shop.json 有商店缺 id");
            }

            if (!seenShop.Add(id))
            {
                throw new ConfigException("npc_shop.json 的商店编号重复：" + id);
            }

            var npcId = (row.NpcId ?? "").Trim();
            if (npcId.Length == 0)
            {
                throw new ConfigException("npc_shop.json 的 " + id + " 缺 npc_id");
            }

            if (!seenNpc.Add(npcId))
            {
                throw new ConfigException("npc_shop.json 有两个商店挂在同一位 NPC 上：" + npcId);
            }

            if (npcShops.Count > 0)
            {
                if (!npcShops.TryGetValue(npcId, out var npcShopId))
                {
                    throw new ConfigException("npc_shop.json 的 npc_id 不在 npc.json 里：" + npcId);
                }

                if (!string.Equals(npcShopId, id, StringComparison.Ordinal))
                {
                    throw new ConfigException("npc.json 的 " + npcId + " 指向商店 "
                        + (npcShopId.Length == 0 ? "（空）" : npcShopId) + "，npc_shop.json 这边是 " + id);
                }
            }

            var currency = (row.Currency ?? "").Trim();
            if (currencies.Find(currency) is null)
            {
                throw new ConfigException("npc_shop.json 的 " + id + " currency 不在 currency.json 里：" + currency);
            }

            var refresh = (row.Refresh ?? "").Trim();
            if (refresh != "daily" && refresh != "weekly" && refresh != "never")
            {
                throw new ConfigException("npc_shop.json 的 " + id + " refresh 只认 daily / weekly / never：" + refresh);
            }

            var goodsRows = row.Items ?? new List<WireGoods>();
            if (goodsRows.Count == 0)
            {
                throw new ConfigException("npc_shop.json 的 " + id + " 一件商品都没有");
            }

            var seenItem = new HashSet<string>(StringComparer.Ordinal);
            var goods = new List<Goods>(goodsRows.Count);
            foreach (var g in goodsRows)
            {
                var itemId = (g.ItemId ?? "").Trim();
                var def = items.Find(itemId)
                    ?? throw new ConfigException("npc_shop.json 的 " + id + " 卖了不存在的物品：" + itemId);

                if (!seenItem.Add(itemId))
                {
                    throw new ConfigException("npc_shop.json 的 " + id + " 商品重复：" + itemId);
                }

                if (!shelfKinds.Contains(def.Kind))
                {
                    // 方案 12：装备、材料、结晶、灵石这些不进 NPC 货架。种类白名单是硬闸。
                    throw new ConfigException("npc_shop.json 的 " + id + " 卖了禁售种类 "
                        + def.Kind + "：" + itemId);
                }

                if (forbidden.Contains(itemId))
                {
                    // 方案 12 的禁售清单（回春丹、气血丹、凝神丹、回灵丹、境界丹…）。
                    throw new ConfigException("npc_shop.json 的 " + id + " 卖了禁售物品：" + itemId);
                }

                if (g.Price <= 0)
                {
                    throw new ConfigException("npc_shop.json 的 " + id + "/" + itemId + " price 必须是正整数");
                }

                if (g.StockPerDay < 0)
                {
                    throw new ConfigException("npc_shop.json 的 " + id + "/" + itemId + " stock_per_day 不能是负数");
                }

                if (g.LimitPerCharacter < 0)
                {
                    throw new ConfigException("npc_shop.json 的 " + id + "/" + itemId
                        + " limit_per_character 不能是负数（0 = 不限）");
                }

                goods.Add(new Goods(itemId, g.Price, g.StockPerDay, g.LimitPerCharacter));
            }

            result.Add(new Shop(id, npcId, currency, refresh, goods));
        }

        return result;
    }

    static SellSection ReadSell(WireSell? wire, CurrencyTable currencies, ItemTable items)
    {
        if (wire is null)
        {
            throw new ConfigException("npc_shop.json 缺 sell 段：没有回收价，卖什么都得现编");
        }

        var currency = (wire.Currency ?? "").Trim();
        if (currencies.Find(currency) is null)
        {
            throw new ConfigException("npc_shop.json 的 sell.currency 不在 currency.json 里：" + currency);
        }

        var rows = wire.Items ?? new List<WireSellLine>();
        if (rows.Count == 0)
        {
            throw new ConfigException("npc_shop.json 的 sell.items 不能是空的");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SellLine>(rows.Count);
        foreach (var row in rows)
        {
            var itemId = (row.ItemId ?? "").Trim();
            var def = items.Find(itemId)
                ?? throw new ConfigException("npc_shop.json 的 sell 收了不存在的物品：" + itemId);

            if (!seen.Add(itemId))
            {
                throw new ConfigException("npc_shop.json 的 sell 重复：" + itemId);
            }

            // 只收材料：装备、外观、丹、券都不在白名单里，避免「卖装备换钱」绕开绑定与拍卖。
            if (!string.Equals(def.Kind, "material", StringComparison.Ordinal))
            {
                throw new ConfigException("npc_shop.json 的 sell 只收材料，这一条是 "
                    + def.Kind + "：" + itemId);
            }

            if (row.Price < 0)
            {
                throw new ConfigException("npc_shop.json 的 sell " + itemId + " price 不能是负数");
            }

            result.Add(new SellLine(itemId, row.Price));
        }

        return new SellSection(currency, result);
    }

    static BuybackWindow ReadBuyback(WireBuyback? wire)
    {
        if (wire is null)
        {
            throw new ConfigException("npc_shop.json 缺 buyback 段");
        }

        if (wire.Keep < 1 || wire.Keep > 20)
        {
            throw new ConfigException("npc_shop.json 的 buyback.keep 必须在 1–20 之间");
        }

        if (wire.PricePct < 1 || wire.PricePct > 100)
        {
            throw new ConfigException("npc_shop.json 的 buyback.price_pct 必须在 1–100 之间");
        }

        if (wire.ExpireDays < 1 || wire.ExpireDays > 365)
        {
            throw new ConfigException("npc_shop.json 的 buyback.expire_days 必须在 1–365 之间");
        }

        return new BuybackWindow(wire.Keep, wire.PricePct, wire.ExpireDays);
    }

    static RepairRule ReadRepair(WireRepair? wire, CurrencyTable currencies, ItemTable items)
    {
        if (wire is null)
        {
            throw new ConfigException("npc_shop.json 缺 repair 段");
        }

        var currency = (wire.Currency ?? "").Trim();
        if (currencies.Find(currency) is null)
        {
            throw new ConfigException("npc_shop.json 的 repair.currency 不在 currency.json 里：" + currency);
        }

        if (wire.PricePerPoint <= 0)
        {
            throw new ConfigException("npc_shop.json 的 repair.price_per_point 必须是正整数");
        }

        var max = wire.DurabilityMax ?? new Dictionary<string, int>();
        if (max.Count == 0)
        {
            throw new ConfigException("npc_shop.json 的 repair.durability_max 不能是空的");
        }

        foreach (var pair in max)
        {
            if (pair.Value <= 0)
            {
                throw new ConfigException("npc_shop.json 的 repair.durability_max."
                    + pair.Key + " 必须是正整数");
            }
        }

        // 物品表里出现过的每个品阶都要有上限，否则穿到那件装备就修不了。
        foreach (var def in items.All)
        {
            if (def.IsEquipment && !max.ContainsKey(def.Grade))
            {
                throw new ConfigException("npc_shop.json 的 repair.durability_max 缺品阶 "
                    + def.Grade + "（" + def.Id + " 是这一档）");
            }
        }

        return new RepairRule(currency, wire.PricePerPoint, max);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    internal sealed class Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("shelf_kinds")] public List<string>? ShelfKinds { get; set; }
        [JsonPropertyName("forbidden_items")] public List<string>? ForbiddenItems { get; set; }
        [JsonPropertyName("shops")] public List<WireShop>? Shops { get; set; }
        [JsonPropertyName("sell")] public WireSell? Sell { get; set; }
        [JsonPropertyName("buyback")] public WireBuyback? Buyback { get; set; }
        [JsonPropertyName("repair")] public WireRepair? Repair { get; set; }
    }

    internal sealed class WireShop
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("npc_id")] public string? NpcId { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("refresh")] public string? Refresh { get; set; }
        [JsonPropertyName("items")] public List<WireGoods>? Items { get; set; }
    }

    internal sealed class WireGoods
    {
        [JsonPropertyName("item_id")] public string? ItemId { get; set; }
        [JsonPropertyName("price")] public long Price { get; set; }
        [JsonPropertyName("stock_per_day")] public int StockPerDay { get; set; }
        [JsonPropertyName("limit_per_character")] public int LimitPerCharacter { get; set; }
    }

    internal sealed class WireSell
    {
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("items")] public List<WireSellLine>? Items { get; set; }
    }

    internal sealed class WireSellLine
    {
        [JsonPropertyName("item_id")] public string? ItemId { get; set; }
        [JsonPropertyName("price")] public long Price { get; set; }
    }

    internal sealed class WireBuyback
    {
        [JsonPropertyName("keep")] public int Keep { get; set; }
        [JsonPropertyName("price_pct")] public int PricePct { get; set; }
        [JsonPropertyName("expire_days")] public int ExpireDays { get; set; }
    }

    internal sealed class WireRepair
    {
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("price_per_point")] public long PricePerPoint { get; set; }
        [JsonPropertyName("durability_max")] public Dictionary<string, int>? DurabilityMax { get; set; }
    }
}
