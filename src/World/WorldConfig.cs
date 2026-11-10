using System.Text.Json;
using System.Text.Json.Serialization;

namespace World;

/// <summary>
/// 世界服启动配置。地点、开口的人与势力的编号正本都在这里，
/// 名字一律照方案正文 2.5 到 2.7 原文；区域与对话内容分目录放。
/// 分线容量、每区最多几条线、同步频率取 5–10Hz 里的哪一个值、移动速度上限
/// 与初始站位，方案都没写定数值，留在这里注明待定，代码里不给兜底。
/// </summary>
public sealed record WorldConfig(
    string Listen,
    int Version,
    string RegionDir,
    string DialogueDir,
    int LineCapacity,
    int MaxLinesPerRegion,
    int SyncHz,
    double MaxMoveSpeed,
    double SpawnX,
    double SpawnY,
    double SpawnZ,
    double SpawnFacing,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Speakers,
    IReadOnlyList<string> Factions)
{
    public static WorldConfig Load(string path)
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

        var listen = (wire.Listen ?? "").Trim();
        if (!listen.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !listen.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigException("listen 必须是 http:// 或 https:// 开头的地址：" + listen);
        }

        var version = wire.Version ?? throw new ConfigException("world.json 缺 version");
        if (version < 1)
        {
            throw new ConfigException("version 必须大于 0，现在是 " + version);
        }

        var regionDir = (wire.RegionDir ?? "").Trim();
        if (regionDir.Length == 0)
        {
            throw new ConfigException("world.json 缺 region_dir");
        }

        var dialogueDir = (wire.DialogueDir ?? "").Trim();
        if (dialogueDir.Length == 0)
        {
            throw new ConfigException("world.json 缺 dialogue_dir");
        }

        var capacity = wire.LineCapacity ?? throw new ConfigException("world.json 缺 line_capacity");
        if (capacity < 1 || capacity > 10_000)
        {
            throw new ConfigException("line_capacity 必须在 1 到 10000 之间，现在是 " + capacity);
        }

        var maxLines = wire.MaxLinesPerRegion ?? throw new ConfigException("world.json 缺 max_lines_per_region");
        if (maxLines < 1 || maxLines > 1_000)
        {
            throw new ConfigException("max_lines_per_region 必须在 1 到 1000 之间，现在是 " + maxLines);
        }

        // 方案写定大世界同步频率是 5–10Hz，具体取哪个值没写定，留在配置里。
        var syncHz = wire.SyncHz ?? throw new ConfigException("world.json 缺 sync_hz");
        if (syncHz < 5 || syncHz > 10)
        {
            throw new ConfigException("sync_hz 必须在方案写定的 5 到 10 之间，现在是 " + syncHz);
        }

        var maxSpeed = Coord(wire.MaxMoveSpeed, "max_move_speed");
        if (maxSpeed <= 0)
        {
            throw new ConfigException("max_move_speed 必须大于 0，现在是 " + maxSpeed);
        }

        return new WorldConfig(
            listen, version, regionDir, dialogueDir, capacity, maxLines,
            syncHz, maxSpeed,
            Coord(wire.SpawnX, "spawn_x"),
            Coord(wire.SpawnY, "spawn_y"),
            Coord(wire.SpawnZ, "spawn_z"),
            Coord(wire.SpawnFacing, "spawn_facing"),
            Names(wire.Regions, "regions"),
            Names(wire.Speakers, "speakers"),
            Names(wire.Factions, "factions"));
    }

    static double Coord(double? value, string key)
    {
        var number = value ?? throw new ConfigException("world.json 缺 " + key);
        if (!double.IsFinite(number))
        {
            throw new ConfigException(key + " 必须是有限数，现在是 " + number);
        }

        return number;
    }

    // 编号名单一个都不能少，也不能重名；缺了就起不来，不带着半截表跑。
    static IReadOnlyList<string> Names(List<string>? value, string key)
    {
        if (value is null || value.Count == 0)
        {
            throw new ConfigException("world.json 缺 " + key);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ConfigException(key + " 里有空编号");
            }

            if (!seen.Add(name))
            {
                throw new ConfigException(key + " 里有重复编号 " + name);
            }
        }

        return value;
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        [JsonPropertyName("listen")] public string? Listen { get; init; }
        [JsonPropertyName("version")] public int? Version { get; init; }
        [JsonPropertyName("region_dir")] public string? RegionDir { get; init; }
        [JsonPropertyName("dialogue_dir")] public string? DialogueDir { get; init; }
        [JsonPropertyName("line_capacity")] public int? LineCapacity { get; init; }
        [JsonPropertyName("max_lines_per_region")] public int? MaxLinesPerRegion { get; init; }
        [JsonPropertyName("sync_hz")] public int? SyncHz { get; init; }
        [JsonPropertyName("max_move_speed")] public double? MaxMoveSpeed { get; init; }
        [JsonPropertyName("spawn_x")] public double? SpawnX { get; init; }
        [JsonPropertyName("spawn_y")] public double? SpawnY { get; init; }
        [JsonPropertyName("spawn_z")] public double? SpawnZ { get; init; }
        [JsonPropertyName("spawn_facing")] public double? SpawnFacing { get; init; }
        [JsonPropertyName("regions")] public List<string>? Regions { get; init; }
        [JsonPropertyName("speakers")] public List<string>? Speakers { get; init; }
        [JsonPropertyName("factions")] public List<string>? Factions { get; init; }
    }
}

public sealed class ConfigException(string message) : Exception(message);
