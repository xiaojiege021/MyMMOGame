using System.Text.Json;

namespace Character;

/// <summary>
/// config/mainline_drops.json。交任务时按场次发奖，与那场战斗的 exp_settlement 共用 txn_id。
/// 缺表时 N7 降级为 C1 口径（不校验收奖、不发物品）。
/// </summary>
public sealed class MainlineDropTable
{
    public const string WrittenSchema = "mainline-drops/1";

    static readonly HashSet<string> TopKeys = new(StringComparer.Ordinal)
    {
        "schema", "version", "pending", "note", "items_config", "entries",
    };

    static readonly HashSet<string> EntryKeys = new(StringComparer.Ordinal)
    {
        "stage", "lines",
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

    readonly Dictionary<int, IReadOnlyList<DropLineDef>> _byStage = new();

    public int Version { get; }
    public bool Pending { get; }
    public bool IsMissing { get; }

    public int StageCount => _byStage.Count;

    MainlineDropTable(
        Dictionary<int, IReadOnlyList<DropLineDef>> byStage,
        int version, bool pending, bool missing)
    {
        _byStage = byStage;
        Version = version;
        Pending = pending;
        IsMissing = missing;
    }

    public static MainlineDropTable Missing() => new([], 0, false, missing: true);

    public static MainlineDropTable LoadIfPresent(string path, ItemTable items, CurrencyTable currencies)
        => File.Exists(path) ? Load(path, items, currencies) : Missing();

    public static MainlineDropTable Load(string path, ItemTable items, CurrencyTable currencies)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigException("主线发奖表 JSON 无效：" + path + " " + ex.Message);
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

            var map = new Dictionary<int, IReadOnlyList<DropLineDef>>();
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException(path + "：entries 必须是数组");
            }

            foreach (var entry in entries.EnumerateArray())
            {
                RequireKeys(entry, EntryKeys, path + " entry");
                RejectForbidden(entry, path);
                var stage = Num(entry, "stage");
                if (stage is < 1 or > 12)
                {
                    throw new ConfigException(path + "：stage 必须是 1..12");
                }

                if (map.ContainsKey(stage))
                {
                    throw new ConfigException(path + "：场次 " + stage + " 重复");
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

                    if (currencies.Find(itemId) is not null || SpiritStoneCurrency.Contains(itemId))
                    {
                        throw new ConfigException(path + "：发奖不许含灵石币种 item=" + itemId);
                    }

                    if (items.Find(itemId) is null)
                    {
                        throw new ConfigException(path + "：物品不在 items 表 item=" + itemId);
                    }

                    if (lines.Any(l => l.ItemId == itemId))
                    {
                        throw new ConfigException(path + "：同一场次重复物品 " + itemId);
                    }

                    lines.Add(new DropLineDef(itemId, count));
                }

                map[stage] = lines;
            }

            return new MainlineDropTable(map, version, pending, missing: false);
        }
    }

    public IReadOnlyList<DropLineDef> LinesFor(int stage) =>
        _byStage.GetValueOrDefault(stage) ?? [];

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