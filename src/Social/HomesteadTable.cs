using System.Text.Json;
using System.Text.RegularExpressions;

namespace Social;

/// <summary>
/// C3 洞府摆放的可选配置正本（config/homestead.json）。
///
/// 方案 12.1 写定「洞府与家园：洞府外观、摆件、家园装饰。只做展示和生活」；
/// 开发架构 4.4 写定「摆放和访客可见。家园币只换外观和便捷」。P3-4a2 已经把
/// 洞府摆件（home_decor、到账即绑）发进角色背包，本表只补「摆在哪一格」的内容：
/// 格位编号与数量是方案没写定的占位，全标 pending=true；允许摆件不另写一份名单，
/// 只认 config/items.json 里 kind=home_decor 且 bind=always 的物品——这就是
/// 「等 P3-4a2 的洞府摆件能发货之后再派」的加载期硬约束，表里一件都没有就起不来。
///
/// 这张表是可选的：老卡的临时配置目录里没有它，社交服照常起来（缺表降级），
/// P2-1 系列的启动日志、宗门判定与老断言一个字都不用改；坏表照样起不来。
/// </summary>
public sealed class HomesteadTable
{
    public const string Schema = "homestead-table/1";
    public const int Version = 1;
    public const string DefaultItemsConfig = "config/items.json";

    // 结构上限，不是玩法数值：格位表本身在配置里，代码里没有第二套格子数。
    const int MaxSlots = 64;

    static readonly HashSet<string> TopKeys = new(StringComparer.Ordinal)
    {
        "schema", "version", "pending", "note", "items_config", "slots",
    };

    static readonly HashSet<string> SlotKeys = new(StringComparer.Ordinal) { "id" };

    // 「只做展示和生活」的反向闸门：奖励 / 掉落 / 家园币 / 属性 / 概率 / 价格 /
    // 归属 / 绑定 / 数量一类字段不许从这份配置里长出来（白名单之外再点一次名）。
    static readonly HashSet<string> ForbiddenKeys = new(StringComparer.Ordinal)
    {
        "reward", "drop", "loot", "gold", "exp", "equip", "fatigue", "price", "probability",
        "tax", "spirit_stone", "vein", "home_coin", "currency", "attribute", "damage",
        "owner", "bind", "count", "level", "realm", "items", "consume", "visitors",
    };

    static readonly Regex SlotIdPattern = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    HomesteadTable(
        IReadOnlyList<string> slots, IReadOnlySet<string> itemIds, string itemsConfig, bool pending)
    {
        Slots = slots;
        ItemIds = itemIds;
        ItemsConfig = itemsConfig;
        Pending = pending;
    }

    /// <summary>方案没写定格位数 / 摆件编号 / 摆放是否消耗，这份内容表整体待定。</summary>
    public bool Pending { get; }

    public IReadOnlyList<string> Slots { get; }

    /// <summary>允许摆的物品编号：只来自 items.json 的 home_decor + bind=always。</summary>
    public IReadOnlySet<string> ItemIds { get; }

    public string ItemsConfig { get; }

    public string SlotList => string.Join(",", Slots);

    public string ItemList => string.Join(",", ItemIds.OrderBy(x => x, StringComparer.Ordinal));

    public bool HasSlot(string id) => Slots.Contains(id, StringComparer.Ordinal);

    public bool Allows(string itemId) => ItemIds.Contains(itemId);

    /// <summary>缺这份可选表就返回 null，社交服按 P2-1 的老链路照常启动。</summary>
    public static HomesteadTable? LoadIfPresent(string path)
        => File.Exists(path) ? Load(path) : null;

    public static HomesteadTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到洞府摆放配置 " + path);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigException("洞府摆放配置不是合法 JSON：" + ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("homestead.json 顶层必须是对象");
            }

            RequireKeys(root, TopKeys, "homestead.json 顶层");
            BanKeys(root, "homestead.json 顶层");

            if ((Str(root, "schema") ?? "") != Schema)
            {
                throw new ConfigException("homestead.json 的 schema 必须是 " + Schema);
            }

            if (Num(root, "version") != Version)
            {
                throw new ConfigException("homestead.json 的 version 必须是 " + Version);
            }

            var pending = root.TryGetProperty("pending", out var pendingValue)
                && pendingValue.ValueKind == JsonValueKind.True;

            var itemsConfig = (Str(root, "items_config") ?? "").Trim();
            if (itemsConfig.Length == 0)
            {
                itemsConfig = DefaultItemsConfig;
            }

            if (!root.TryGetProperty("slots", out var slotArray)
                || slotArray.ValueKind != JsonValueKind.Array
                || slotArray.GetArrayLength() == 0 || slotArray.GetArrayLength() > MaxSlots)
            {
                throw new ConfigException("homestead.json 的 slots 必须是 1 到 " + MaxSlots + " 条");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var slots = new List<string>(slotArray.GetArrayLength());
            foreach (var raw in slotArray.EnumerateArray())
            {
                if (raw.ValueKind != JsonValueKind.Object)
                {
                    throw new ConfigException("homestead.json 的 slots 里有一条不是对象");
                }

                RequireKeys(raw, SlotKeys, "homestead.json 格位");
                BanKeys(raw, "homestead.json 格位");

                var id = (Str(raw, "id") ?? "").Trim();
                if (!SlotIdPattern.IsMatch(id))
                {
                    throw new ConfigException("homestead.json 的格位编号只能是小写字母 / 数字 / 下划线：" + id);
                }

                if (!seen.Add(id))
                {
                    throw new ConfigException("homestead.json 里有重复格位编号 " + id);
                }

                slots.Add(id);
            }

            var itemIds = LoadDecorItems(itemsConfig);
            if (itemIds.Count == 0)
            {
                // C3 排在 P3-4a2 之后：没有能发货的洞府摆件时，不自己发明物品。
                throw new ConfigException("items.json 里没有 home_decor 且 bind=always 的洞府摆件");
            }

            return new HomesteadTable(slots, itemIds, itemsConfig, pending);
        }
    }

    static IReadOnlySet<string> LoadDecorItems(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到物品表 " + path);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigException("物品表不是合法 JSON：" + ex.Message);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("items", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("items.json 缺 items");
            }

            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in array.EnumerateArray())
            {
                if (raw.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = (Str(raw, "id") ?? "").Trim();
                var kind = (Str(raw, "kind") ?? "").Trim();
                var bind = (Str(raw, "bind") ?? "").Trim();
                // 只做展示：摆件必须到账即绑，不能拿可交易的矿石 / 装备来摆。
                if (id.Length > 0 && kind == "home_decor" && bind == "always")
                {
                    result.Add(id);
                }
            }

            return result;
        }
    }

    static void RequireKeys(JsonElement obj, HashSet<string> allowed, string where)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ConfigException(where + " 出现不认识的键 " + property.Name);
            }
        }
    }

    static void BanKeys(JsonElement obj, string where)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (ForbiddenKeys.Contains(property.Name))
            {
                throw new ConfigException(where + " 不许出现 " + property.Name);
            }
        }
    }

    static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static int? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;
}
