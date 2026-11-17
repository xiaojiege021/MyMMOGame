using System.Text.Json;

namespace Mmo.Shared.Domain;

public sealed record DropLineDef(string ItemId, int Count);

/// <summary>
/// config/dungeon_drops.json。战斗服按它报事实，角色服按它验收，
/// 两服读同一份，不各存一套。缺表降级：不报、不收任何掉落行。
///
/// C6/D12：解析器从两服各一份合并到这里。需要校验「物品在 items 表里、
/// 掉落不许含灵石币种」的进程（角色服）把两张表的编号集合传进来；战斗服
/// 只报事实，不传集合，只做结构与白名单校验。这是同一个加载器，不是第二套规则。
/// </summary>
public sealed class DungeonDropTable
{
    public const string WrittenSchema = "dungeon-drops/1";

    static readonly HashSet<string> TopKeys = new(StringComparer.Ordinal)
    {
        "schema", "version", "pending", "note", "items_config", "entries",
    };

    static readonly HashSet<string> EntryKeys = new(StringComparer.Ordinal)
    {
        "dungeon_id", "lines",
    };

    static readonly HashSet<string> LineKeys = new(StringComparer.Ordinal)
    {
        "item_id", "count",
    };

    static readonly string[] ForbiddenKeys =
    [
        "reward", "rewards", "gold", "yuanbao", "spirit_stone", "stone", "exp",
        "fatigue", "weekly_share", "probability", "success_pct", "tax", "price",
        "dungeon_count", "vein", "owner_sect", "attribute", "power", "level", "realm",
        "drop", "drops", "loot",
    ];

    static readonly string[] SpiritStoneCurrency =
        ["stone_1", "stone_2", "stone_3", "stone_4", "stone_5"];

    readonly Dictionary<string, IReadOnlyList<DropLineDef>> _byDungeon = new(StringComparer.Ordinal);

    public int Version { get; }
    public bool Pending { get; }
    public bool IsMissing { get; }

    public int DungeonCount => _byDungeon.Count;

    DungeonDropTable(
        Dictionary<string, IReadOnlyList<DropLineDef>> byDungeon,
        int version, bool pending, bool missing)
    {
        _byDungeon = byDungeon;
        Version = version;
        Pending = pending;
        IsMissing = missing;
    }

    public static DungeonDropTable Missing() => new([], 0, false, missing: true);

    /// <summary>
    /// 表存在就读、不存在就降级。knownItemIds / currencyIds 由调用方决定传不传：
    /// 角色服传（表外物品、灵石币种在加载期就挡住），战斗服不传（只报事实）。
    /// </summary>
    public static DungeonDropTable LoadIfPresent(
        string path,
        IEnumerable<string>? knownItemIds = null,
        IEnumerable<string>? currencyIds = null)
        => File.Exists(path) ? Load(path, knownItemIds, currencyIds) : Missing();

    public static DungeonDropTable Load(
        string path,
        IEnumerable<string>? knownItemIds = null,
        IEnumerable<string>? currencyIds = null)
    {
        var items = knownItemIds?.ToHashSet(StringComparer.Ordinal);
        var currencies = currencyIds?.ToHashSet(StringComparer.Ordinal);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigException("掉落表 JSON 无效：" + path + " " + ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException(path + "：顶层必须是对象");
            }

            RequireKeys(root, TopKeys, path + " 顶层");
            RejectForbidden(root, path);

            var schema = Str(root, "schema");
            if (schema != WrittenSchema)
            {
                throw new ConfigException(path + "：schema 必须是 " + WrittenSchema);
            }

            var version = Num(root, "version");
            if (version is < 1 or > 9999)
            {
                throw new ConfigException(path + "：version 无效");
            }

            var pending = root.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.True;
            var itemsConfig = (Str(root, "items_config") ?? "").Trim();
            ResolveItemsPath(path, itemsConfig);

            var map = new Dictionary<string, IReadOnlyList<DropLineDef>>(StringComparer.Ordinal);
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException(path + "：entries 必须是数组");
            }

            foreach (var entry in entries.EnumerateArray())
            {
                RequireKeys(entry, EntryKeys, path + " entry");
                RejectForbidden(entry, path);
                var dungeonId = (Str(entry, "dungeon_id") ?? "").Trim();
                if (dungeonId.Length == 0)
                {
                    throw new ConfigException(path + "：dungeon_id 不能为空");
                }

                if (map.ContainsKey(dungeonId))
                {
                    throw new ConfigException(path + "：副本 " + dungeonId + " 重复");
                }

                if (!entry.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array)
                {
                    throw new ConfigException(path + "：lines 必须是数组");
                }

                var lines = new List<DropLineDef>();
                foreach (var line in linesEl.EnumerateArray())
                {
                    RequireKeys(line, LineKeys, path + " line");
                    RejectForbidden(line, path);
                    var itemId = (Str(line, "item_id") ?? "").Trim();
                    if (itemId.Length == 0)
                    {
                        throw new ConfigException(path + "：item_id 不能为空");
                    }

                    var count = Num(line, "count");
                    if (count is < 1 or > 9999)
                    {
                        throw new ConfigException(path + "：count 无效 item=" + itemId);
                    }

                    if (currencies?.Contains(itemId) == true || SpiritStoneCurrency.Contains(itemId))
                    {
                        throw new ConfigException(path + "：掉落不许含灵石币种 item=" + itemId);
                    }

                    if (items is not null && !items.Contains(itemId))
                    {
                        throw new ConfigException(path + "：物品不在 items 表 item=" + itemId);
                    }

                    if (lines.Any(l => l.ItemId == itemId))
                    {
                        throw new ConfigException(path + "：同一副本重复物品 " + itemId);
                    }

                    lines.Add(new DropLineDef(itemId, count));
                }

                map[dungeonId] = lines;
            }

            return new DungeonDropTable(map, version, pending, missing: false);
        }
    }

    public IReadOnlyList<DropLineDef> LinesFor(string dungeonId) =>
        _byDungeon.GetValueOrDefault(dungeonId) ?? [];

    public bool MatchesReported(string dungeonId, IReadOnlyList<DropLineDef> reported)
    {
        var expected = LinesFor(dungeonId);
        if (expected.Count != reported.Count)
        {
            return false;
        }

        var a = expected.OrderBy(x => x.ItemId, StringComparer.Ordinal).ToList();
        var b = reported.OrderBy(x => x.ItemId, StringComparer.Ordinal).ToList();
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].ItemId, b[i].ItemId, StringComparison.Ordinal)
                || a[i].Count != b[i].Count)
            {
                return false;
            }
        }

        return true;
    }

    static string ResolveItemsPath(string dropPath, string itemsConfig)
    {
        if (itemsConfig.Length == 0)
        {
            throw new ConfigException(dropPath + "：items_config 不能为空");
        }

        var full = Path.IsPathRooted(itemsConfig)
            ? itemsConfig
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dropPath)!, itemsConfig));
        if (!File.Exists(full))
        {
            throw new ConfigException(dropPath + "：items_config 指向的文件不存在");
        }

        return full;
    }

    static void RequireKeys(JsonElement element, HashSet<string> allowed, string where)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ConfigException(where + "：不允许的键 " + property.Name);
            }
        }
    }

    static void RejectForbidden(JsonElement element, string path)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (ForbiddenKeys.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new ConfigException(path + "：禁止键 " + property.Name);
            }
        }
    }

    static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static int Num(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw new ConfigException("缺少 " + name);
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt32(),
            _ => throw new ConfigException(name + " 必须是数字"),
        };
    }
}
