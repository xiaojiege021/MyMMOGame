using System.Text.Json;

namespace Battle;

/// <summary>战斗服侧读 config/dungeon_drops.json，只用于通关时报事实。缺表 = 无掉落。</summary>
public sealed class DungeonDropTable
{
    public const string WrittenSchema = "dungeon-drops/1";

    readonly Dictionary<string, IReadOnlyList<(string ItemId, int Count)>> _byDungeon =
        new(StringComparer.Ordinal);

    public int Version { get; }
    public bool IsMissing { get; }

    DungeonDropTable(
        Dictionary<string, IReadOnlyList<(string, int)>> byDungeon, int version, bool missing)
    {
        _byDungeon = byDungeon;
        Version = version;
        IsMissing = missing;
    }

    public static DungeonDropTable Missing() => new([], 0, missing: true);

    public static DungeonDropTable LoadIfPresent(string path)
        => File.Exists(path) ? Load(path) : Missing();

    public static DungeonDropTable Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (Str(root, "schema") != WrittenSchema)
        {
            throw new ConfigException(path + "：schema 必须是 " + WrittenSchema);
        }

        var version = root.GetProperty("version").GetInt32();
        var map = new Dictionary<string, IReadOnlyList<(string, int)>>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("entries").EnumerateArray())
        {
            var dungeonId = entry.GetProperty("dungeon_id").GetString() ?? "";
            if (dungeonId.Length == 0)
            {
                throw new ConfigException(path + "：dungeon_id 不能为空");
            }

            if (map.ContainsKey(dungeonId))
            {
                throw new ConfigException(path + "：副本重复 " + dungeonId);
            }

            var lines = new List<(string, int)>();
            foreach (var line in entry.GetProperty("lines").EnumerateArray())
            {
                var itemId = line.GetProperty("item_id").GetString() ?? "";
                var count = line.GetProperty("count").GetInt32();
                if (itemId.Length == 0 || count < 1)
                {
                    throw new ConfigException(path + "：掉落行无效");
                }

                lines.Add((itemId, count));
            }

            map[dungeonId] = lines;
        }

        return new DungeonDropTable(map, version, missing: false);
    }

    public IReadOnlyList<(string ItemId, int Count)> LinesFor(string dungeonId) =>
        _byDungeon.GetValueOrDefault(dungeonId) ?? [];

    static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}