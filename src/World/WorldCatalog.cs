using System.Text.Json;
using System.Text.Json.Serialization;

namespace World;

public sealed record RegionDef(
    string Id,
    string Name,
    IReadOnlyList<int> Chapters,
    IReadOnlyList<string> Neighbors,
    string Faction,
    bool Open);

public sealed record DialogueLineDef(int Chapter, string Region, string Text, string Limit);

public sealed record DialogueDef(
    string Id,
    string Name,
    string Faction,
    IReadOnlyList<DialogueLineDef> Lines);

/// <summary>
/// 首发地表与对话的唯一读表点。config/region/ 的文件名用地名，
/// config/dialogue/ 的文件名用人物编号；id 必须与文件名、与 world.json
/// 的编号名单逐字相同。相连关系要对称，人物提到地名时那块地表必须存在。
/// 缺字段或编号对不上就抛 ConfigException，世界服起不来。
/// 对白只落在配置里，不进场景、不进 AB 资源。
/// </summary>
public sealed class WorldCatalog
{
    readonly Dictionary<string, RegionDef> regions;
    readonly Dictionary<string, DialogueDef> dialogues;

    WorldCatalog(WorldConfig config, Dictionary<string, RegionDef> regions, Dictionary<string, DialogueDef> dialogues)
    {
        this.regions = regions;
        this.dialogues = dialogues;
        Regions = config.Regions.Select(id => regions[id]).ToArray();
        Dialogues = config.Speakers.Select(id => dialogues[id]).ToArray();
        Speakers = config.Speakers;
        Factions = config.Factions;
        Version = config.Version;
    }

    public int Version { get; }

    public IReadOnlyList<RegionDef> Regions { get; }

    public IReadOnlyList<DialogueDef> Dialogues { get; }

    public IReadOnlyList<string> Speakers { get; }

    public IReadOnlyList<string> Factions { get; }

    public RegionDef? Find(string id) => regions.TryGetValue(id, out var region) ? region : null;

    /// <summary>这块地表的编号能指到哪些开口的人（按配置名单顺序）。</summary>
    public IReadOnlyList<string> DialogueIdsFor(string regionId) =>
        Dialogues.Where(d => d.Lines.Any(l => l.Region == regionId)).Select(d => d.Id).ToArray();

    public static WorldCatalog Load(WorldConfig config)
    {
        var regionMap = new Dictionary<string, RegionDef>(StringComparer.Ordinal);
        foreach (var path in JsonFiles(config.RegionDir, "region_dir"))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var wire = Read<RegionWire>(path, "region");
            var id = (wire.Id ?? "").Trim();
            var name = (wire.Name ?? "").Trim();
            if (id.Length == 0 || name.Length == 0)
            {
                throw new ConfigException(path + " 缺 id 或 name");
            }

            if (!string.Equals(id, fileName, StringComparison.Ordinal)
                || !string.Equals(id, name, StringComparison.Ordinal))
            {
                throw new ConfigException(path + " 的 id、name 与文件名必须同为地名：" + fileName);
            }

            if (!config.Regions.Contains(id, StringComparer.Ordinal))
            {
                throw new ConfigException("地点编号 " + id + " 不在 world.json 的 regions 里（方案 2.5）");
            }

            var chapters = wire.Chapters ?? throw new ConfigException(path + " 缺 chapters");
            if (chapters.Count == 0 || chapters.Any(c => c < 1 || c > 12))
            {
                throw new ConfigException(path + " 的 chapters 必须在 1 到 12 之间");
            }

            var neighbors = wire.Neighbors ?? throw new ConfigException(path + " 缺 neighbors");
            if (neighbors.Count == 0 || neighbors.Any(string.IsNullOrWhiteSpace))
            {
                throw new ConfigException(path + " 的 neighbors 至少要有一个相连地名");
            }

            if (neighbors.Contains(id, StringComparer.Ordinal) || neighbors.Distinct(StringComparer.Ordinal).Count() != neighbors.Count)
            {
                throw new ConfigException(path + " 的 neighbors 不能指向自己或重复");
            }

            var faction = (wire.Faction ?? "").Trim();
            if (faction.Length > 0 && !config.Factions.Contains(faction, StringComparer.Ordinal))
            {
                throw new ConfigException(path + " 的 faction " + faction + " 不在方案 2.6 的名单里");
            }

            var open = wire.Open ?? throw new ConfigException(path + " 缺 open");
            if (!regionMap.TryAdd(id, new RegionDef(id, name, chapters, neighbors, faction, open)))
            {
                throw new ConfigException("地点编号重复 " + id);
            }
        }

        foreach (var id in config.Regions)
        {
            if (!regionMap.ContainsKey(id))
            {
                throw new ConfigException("regions 里的 " + id + " 没有对应的区域配置");
            }
        }

        if (regionMap.Count != config.Regions.Count)
        {
            throw new ConfigException("region_dir 里的文件数与 regions 编号名单不一致");
        }

        // 无缝相连：A 的邻居里有 B，B 的邻居里就得有 A。
        foreach (var region in regionMap.Values)
        {
            foreach (var neighbor in region.Neighbors)
            {
                if (!regionMap.TryGetValue(neighbor, out var other))
                {
                    throw new ConfigException(region.Id + " 连着未知地点 " + neighbor);
                }

                if (!other.Neighbors.Contains(region.Id, StringComparer.Ordinal))
                {
                    throw new ConfigException(region.Id + " 与 " + neighbor + " 的相连关系不对称");
                }
            }
        }

        var dialogueMap = new Dictionary<string, DialogueDef>(StringComparer.Ordinal);
        foreach (var path in JsonFiles(config.DialogueDir, "dialogue_dir"))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var wire = Read<DialogueWire>(path, "dialogue");
            var id = (wire.Id ?? "").Trim();
            var name = (wire.Name ?? "").Trim();
            if (!string.Equals(id, fileName, StringComparison.Ordinal)
                || !string.Equals(id, name, StringComparison.Ordinal))
            {
                throw new ConfigException(path + " 的 id、name 与文件名必须同为人物编号：" + fileName);
            }

            if (!config.Speakers.Contains(id, StringComparer.Ordinal))
            {
                throw new ConfigException("人物编号 " + id + " 不在 world.json 的 speakers 里（方案 2.7）");
            }

            var faction = (wire.Faction ?? "").Trim();
            if (faction.Length > 0 && !config.Factions.Contains(faction, StringComparer.Ordinal))
            {
                throw new ConfigException(path + " 的 faction " + faction + " 不在方案 2.6 的名单里");
            }

            var wireLines = wire.Lines ?? throw new ConfigException(path + " 缺 lines");
            if (wireLines.Count == 0)
            {
                throw new ConfigException(path + " 至少要有一句定词");
            }

            var lines = new List<DialogueLineDef>();
            foreach (var line in wireLines)
            {
                var text = (line.Text ?? "").Trim();
                var limit = (line.Limit ?? "").Trim();
                var where = (line.Region ?? "").Trim();
                if (line.Chapter is null || line.Chapter < 1 || line.Chapter > 12
                    || text.Length == 0 || limit.Length == 0)
                {
                    throw new ConfigException(path + " 的定词缺场次、正文或边界");
                }

                // 方案没写定这一场在哪块地表时留空，不臆造地点。
                if (where.Length > 0 && !regionMap.ContainsKey(where))
                {
                    throw new ConfigException(path + " 指到了未知地点 " + where);
                }

                lines.Add(new DialogueLineDef(line.Chapter.Value, where, text, limit));
            }

            if (!dialogueMap.TryAdd(id, new DialogueDef(id, name, faction, lines)))
            {
                throw new ConfigException("人物编号重复 " + id);
            }
        }

        foreach (var id in config.Speakers)
        {
            if (!dialogueMap.ContainsKey(id))
            {
                throw new ConfigException("speakers 里的 " + id + " 没有对应的对话配置");
            }
        }

        if (dialogueMap.Count != config.Speakers.Count)
        {
            throw new ConfigException("dialogue_dir 里的文件数与 speakers 编号名单不一致");
        }

        return new WorldCatalog(config, regionMap, dialogueMap);
    }

    static IEnumerable<string> JsonFiles(string dir, string key)
    {
        if (!Directory.Exists(dir))
        {
            throw new ConfigException("找不到 " + key + " 指向的目录 " + dir);
        }

        return Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    static T Read<T>(string path, string kind)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException(path + " 是空的");
        }
        catch (JsonException ex)
        {
            throw new ConfigException(kind + " 配置不是合法 JSON：" + path + " " + ex.Message);
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed record RegionWire
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("chapters")] public List<int>? Chapters { get; init; }
        [JsonPropertyName("neighbors")] public List<string>? Neighbors { get; init; }
        [JsonPropertyName("faction")] public string? Faction { get; init; }
        [JsonPropertyName("open")] public bool? Open { get; init; }
    }

    sealed record DialogueWire
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("faction")] public string? Faction { get; init; }
        [JsonPropertyName("lines")] public List<DialogueLineWire>? Lines { get; init; }
    }

    sealed record DialogueLineWire
    {
        [JsonPropertyName("chapter")] public int? Chapter { get; init; }
        [JsonPropertyName("region")] public string? Region { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("limit")] public string? Limit { get; init; }
    }
}
