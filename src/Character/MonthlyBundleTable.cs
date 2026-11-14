using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>月叠里的一条：这是哪种符、发哪一件、每天几张。总量 = 每天几张 × 天数。</summary>
public sealed record MonthlyBundleEntry(string Kind, string ItemId, int PerDay, int Count);

/// <summary>
/// 月叠的唯一正本（`config/monthly_bundle.json`）。方案 12.1 第 809 行写定：
/// 「月叠 三十天份的传送符和红符 …… 这一叠里只有这两种符，不附带金币、材料、
/// 复活卷轴、解绑道具和经验道具」，技术架构 5 节另写「蓝符不进月叠」。
///
/// 这些都是加载期的硬约束，不是运行期再判一遍：
///   · 只有两种符：种类白名单就是 {teleport_talisman, red_talisman}，两条都必须出现、
///     各只有一条；蓝符 / 复活卷轴 / 经验道具 / 解绑道具 / 金币 / 材料 / 装备 / 副本次数
///     塞一条就起不来（键白名单 + 取值扫描两道）。
///   · 编号不许另立：红符必须就是 config/talisman.json 的 red.item_id（方案写定
///     「红符不是红药」，商城卖的是同一件），传送符必须就是 config/shop.json 里那条
///     teleport_talisman 商品发出的同一件（P1-5c4 只接发货，用法留给本卡）。
///   · 两件都要 consumable 且到账即绑：月叠发的和商城卖的是同一种符。
///   · 每条每天几张不许超过物品的堆叠上限：一处数量只在这份配置里，代码里没有第二套。
/// 价格不在这份表里：月叠在商城按 shop.json 那条 monthly_bundle 商品直接标价卖。
/// 月叠是「一次买一次到账」，不写到期时刻、不写发放节奏——库表里也没有月叠状态行。
/// </summary>
public sealed class MonthlyBundleTable
{
    public const string WrittenSchema = "monthly-bundle-table/1";

    // 方案 12.1 第 809 行：这一叠里只有这两种符。多一种少一种都算玩法被改了。
    public static readonly string[] AllowedKinds = ["teleport_talisman", "red_talisman"];

    // 方案写定「不附带」的那一串。白名单已经覆盖，单列一遍是为了报错能说清是哪一条。
    static readonly string[] ForbiddenKinds =
    [
        "blue_talisman", "revive_scroll", "exp_booster", "unbind_item",
        "gold", "material", "equip", "dungeon_count", "fatigue", "weekly_share",
    ];

    static readonly string[] AllowedTopKeys =
        ["schema", "version", "note", "pending", "product_id", "days", "items"];
    static readonly string[] AllowedEntryKeys = ["kind", "item_id", "per_day"];
    static readonly string[] RequiredTopKeys = ["schema", "version", "product_id", "days", "items"];

    // 顶层与条目里一律不许出现的键：金币 / 灵石 / 材料 / 装备 / 次数 / 券 / 卷轴 / 经验。
    static readonly string[] ForbiddenKeys =
    [
        "gold", "stone", "spirit_stone", "yuanbao", "crystal", "item", "items_extra",
        "equip", "equipment", "stone_enhance", "ticket", "protect_ticket", "scroll",
        "revive_scroll", "exp_booster", "unbind", "count_per_day_extra", "reward",
        "fatigue", "dungeon_count", "weekly_share", "level", "power",
    ];

    public int Version { get; }
    public bool Pending { get; }
    public string ProductId { get; }
    public int Days { get; }
    public long PriceYuanbao { get; }
    public IReadOnlyList<MonthlyBundleEntry> Entries { get; }

    MonthlyBundleTable(
        int version, bool pending, string productId, int days, long priceYuanbao,
        IReadOnlyList<MonthlyBundleEntry> entries)
    {
        Version = version;
        Pending = pending;
        ProductId = productId;
        Days = days;
        PriceYuanbao = priceYuanbao;
        Entries = entries;
    }

    /// <summary>这一种符在这份表里发几件（每天几张 × 天数）。表里没有这种返回 0。</summary>
    public int TotalOf(string kind)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Kind, kind, StringComparison.Ordinal))
            {
                return entry.Count;
            }
        }

        return 0;
    }

    public static bool IsAllowedKind(string kind) =>
        Array.IndexOf(AllowedKinds, kind) >= 0;

    /// <summary>
    /// 与角色服真实启动共用同一条校验链：shop 用来核对商品与标价、items 核对物品种类与
    /// 堆叠上限、talismans 核对红符就是那一个编号、shop 里那条传送符商品核对另一件编号。
    /// </summary>
    public static MonthlyBundleTable Load(
        string path, ShopTable shop, ItemTable items, TalismanTable talismans)
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
            throw new ConfigException("月叠表不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            foreach (var key in RequiredTopKeys)
            {
                if (!root.TryGetProperty(key, out _))
                {
                    throw new ConfigException("monthly_bundle.json 缺字段 " + key);
                }
            }

            RejectUnknownKeys(root, AllowedTopKeys, "顶层");
            RejectForbiddenKeys(root, "顶层");

            Wire wire;
            try
            {
                wire = JsonSerializer.Deserialize<Wire>(text, JsonOptions)
                    ?? throw new ConfigException("月叠表是空的");
            }
            catch (JsonException ex)
            {
                throw new ConfigException("月叠表字段类型不对：" + ex.Message);
            }

            if (!string.Equals((wire.Schema ?? "").Trim(), WrittenSchema, StringComparison.Ordinal))
            {
                throw new ConfigException("monthly_bundle.json 的 schema 必须是 " + WrittenSchema);
            }

            if (wire.Version is null || wire.Version.Value <= 0)
            {
                throw new ConfigException("monthly_bundle.json 的 version 必须大于 0");
            }

            var productId = (wire.ProductId ?? "").Trim();
            if (productId.Length == 0)
            {
                throw new ConfigException("monthly_bundle.json 的 product_id 不能为空");
            }

            // 月叠是商城里的那条商品：标价只从 shop.json 读，本表不写第二套价格。
            var product = shop.Find(productId)
                ?? throw new ConfigException("monthly_bundle.json 的 product_id 不在 shop.json 里："
                    + productId);
            if (!string.Equals(product.Kind, "monthly_bundle", StringComparison.Ordinal))
            {
                throw new ConfigException("monthly_bundle.json 的 product_id 指到的不是月叠："
                    + productId + " kind=" + product.Kind);
            }

            if (product.Deliverable)
            {
                // 月叠一次放两种符进背包，发什么与发多少只在本表；清单里不许填一个发货物。
                throw new ConfigException("monthly_bundle.json 的月叠商品不许配 delivery_item_id："
                    + productId);
            }

            // 方案写定「三十天份」：天数只在这里，代码里没有第二个数字。
            var days = wire.Days ?? 0;
            if (days < 1 || days > 365)
            {
                throw new ConfigException("monthly_bundle.json 的 days 要在 1..365 之间");
            }

            var array = root.GetProperty("items");
            if (array.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("monthly_bundle.json 的 items 必须是数组");
            }

            if (wire.Items is null || wire.Items.Count != AllowedKinds.Length)
            {
                // 只有两种符，各一条：少了哪一种都是玩法被改了，多了就是往这一叠里塞别的。
                throw new ConfigException("monthly_bundle.json 的 items 必须正好是 "
                    + AllowedKinds.Length + " 条（传送符与红符各一条）");
            }

            // 传送符的编号取自商城那条传送符商品：两处发的是同一件，不另立第二份配置。
            var teleportId = TeleportItemIdOf(shop);

            var entries = new List<MonthlyBundleEntry>(wire.Items.Count);
            var seenKinds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < wire.Items.Count; i++)
            {
                var element = array[i];
                RejectUnknownKeys(element, AllowedEntryKeys, "第 " + (i + 1) + " 条");
                RejectForbiddenKeys(element, "第 " + (i + 1) + " 条");

                var entry = wire.Items[i];
                var kind = (entry.Kind ?? "").Trim();
                if (kind.Length == 0)
                {
                    throw new ConfigException("月叠表第 " + (i + 1) + " 条没有 kind");
                }

                if (Array.IndexOf(ForbiddenKinds, kind) >= 0)
                {
                    // 方案 12.1 点名不许附带的那几样，命中就说清楚是哪一条。
                    throw new ConfigException("月叠表里不许出现 " + kind + "：方案 12.1 写定"
                        + "这一叠里只有传送符和红符，不附带金币、材料、复活卷轴、解绑道具和经验道具");
                }

                if (!IsAllowedKind(kind))
                {
                    throw new ConfigException("月叠表第 " + (i + 1) + " 条的种类不在两种符里：" + kind);
                }

                if (!seenKinds.Add(kind))
                {
                    throw new ConfigException("月叠表的种类重复：" + kind);
                }

                var itemId = (entry.ItemId ?? "").Trim();
                if (itemId.Length == 0)
                {
                    throw new ConfigException("月叠表第 " + (i + 1) + " 条没有 item_id");
                }

                var goods = items.Find(itemId)
                    ?? throw new ConfigException("月叠表的物品不在 items.json 里：" + itemId);

                if (!string.Equals(goods.Kind, "consumable", StringComparison.Ordinal))
                {
                    throw new ConfigException("月叠表的物品必须是消耗品：" + itemId + " kind=" + goods.Kind);
                }

                if (goods.Bind != BindRule.Always)
                {
                    throw new ConfigException("月叠表的物品必须到账即绑：" + itemId);
                }

                var perDay = entry.PerDay ?? 0;
                if (perDay < 1 || perDay > goods.StackMax)
                {
                    throw new ConfigException("月叠表 " + kind + " 的 per_day 要在 1.."
                        + goods.StackMax + " 之间");
                }

                if (string.Equals(kind, "red_talisman", StringComparison.Ordinal))
                {
                    // 方案写定「红符不是红药」：这里发的必须就是 talisman.json 那一个编号，
                    // 与商城卖的是同一件；另立一个编号就是把两处拆成两种东西。
                    var red = talismans.RedTalisman
                        ?? throw new ConfigException("月叠表发红符，但 talisman.json 没有 red 段");
                    if (!string.Equals(itemId, red.ItemId, StringComparison.Ordinal))
                    {
                        throw new ConfigException("月叠表的红符必须发 talisman.json 的 red.item_id（"
                            + red.ItemId + "），不许另立：" + itemId);
                    }
                }
                else if (teleportId.Length == 0
                    || !string.Equals(itemId, teleportId, StringComparison.Ordinal))
                {
                    // 传送符只在 shop.json 那条商品与 items.json 两处；月叠发的必须是同一件。
                    throw new ConfigException("月叠表的传送符必须发商城那条传送符商品的同一件（"
                        + teleportId + "），不许另立：" + itemId);
                }

                entries.Add(new MonthlyBundleEntry(kind, itemId, perDay, perDay * days));
            }

            foreach (var kind in AllowedKinds)
            {
                if (!seenKinds.Contains(kind))
                {
                    throw new ConfigException("月叠表缺 " + kind + "：这一叠里只有这两种符，两样都要有");
                }
            }

            return new MonthlyBundleTable(
                wire.Version!.Value, wire.Pending ?? false, productId, days,
                product.PriceYuanbao, entries);
        }
    }

    /// <summary>商城那条传送符商品发的物品编号。清单里没有这条商品就返回空串。</summary>
    static string TeleportItemIdOf(ShopTable shop)
    {
        foreach (var item in shop.Items)
        {
            if (string.Equals(item.Kind, "teleport_talisman", StringComparison.Ordinal))
            {
                return item.DeliveryItemId;
            }
        }

        return "";
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
        [JsonPropertyName("days")] public int? Days { get; init; }
        [JsonPropertyName("items")] public List<WireEntry>? Items { get; init; }
    }

    sealed record WireEntry
    {
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("item_id")] public string? ItemId { get; init; }
        [JsonPropertyName("per_day")] public int? PerDay { get; init; }
    }

    static void RejectUnknownKeys(JsonElement row, string[] allowed, string where)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in row.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                throw new ConfigException("monthly_bundle.json 的 " + where
                    + " 出现不许出现的键：" + property.Name);
            }
        }
    }

    /// <summary>
    /// 键名扫描：金币、灵石、材料、装备、券、卷轴、经验道具、次数这些词出现在任何一层
    /// 就直接拒掉——「这一叠里只有两种符」不能靠一条一条判，得让那些键长不出来。
    /// </summary>
    static void RejectForbiddenKeys(JsonElement row, string where)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in row.EnumerateObject())
        {
            var name = property.Name;
            foreach (var forbidden in ForbiddenKeys)
            {
                if (string.Equals(name, forbidden, StringComparison.Ordinal))
                {
                    throw new ConfigException("monthly_bundle.json 的 " + where
                        + " 出现不许出现的键：" + name);
                }
            }
        }
    }
}
