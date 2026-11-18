using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>通行证的一个点数来源：哪种事、给多少点、按什么周期封顶、一周期几次。</summary>
public sealed record SeasonPassSourceDef(
    string Id, string Kind, string DungeonId, int Points, string Period, int MaxPerPeriod);

/// <summary>
/// 通行证进度的唯一正本（`config/season_pass_progress.json`）。Q6/J4 只定两件事：
/// 每阶要多少点、点数从哪来。发什么奖励仍归 config/season_pass.json，两者不互相顶替。
///
/// 加载期硬约束（不是运行期再判一遍）：
///   · schema / version / points_per_stage 齐全，points_per_stage 大于 0；
///   · 来源的 kind 只能是 dungeon_clear 或 mainline_stage，period 只能是 daily / weekly / once；
///   · dungeon_clear 必须挂 config/dungeons.json 里真实存在的副本；mainline_stage 不许挂副本；
///   · daily / weekly 必须写次数上限（一次都不给或给负数都起不来），once 的上限恒为 1；
///   · 表里只许出现这些键：金币、灵石、物品、装备、副本次数一类字段长不出来——
///     通行证点数不是货币，也不该变成第二条发货路。
/// </summary>
public sealed class SeasonPassProgressTable
{
    public const string WrittenSchema = "season-pass-progress/1";

    public const string DungeonClear = "dungeon_clear";
    public const string MainlineStage = "mainline_stage";

    public static readonly string[] Kinds = [DungeonClear, MainlineStage];
    public static readonly string[] Periods = ["daily", "weekly", "once"];

    static readonly string[] AllowedTopKeys =
        ["schema", "version", "note", "pending", "points_per_stage", "sources"];
    static readonly string[] AllowedSourceKeys =
        ["id", "kind", "dungeon_id", "points", "period", "max_per_period"];

    readonly List<SeasonPassSourceDef> _sources;

    SeasonPassProgressTable(
        int version, bool pending, int pointsPerStage, List<SeasonPassSourceDef> sources,
        bool missing = false)
    {
        IsMissing = missing;
        Version = version;
        Pending = pending;
        PointsPerStage = pointsPerStage;
        _sources = sources;
    }

    /// <summary>缺表（老卡的临时配置目录里没有这一份）：领取与加点都拒绝，不按 0 点放行。</summary>
    public bool IsMissing { get; }

    public int Version { get; }
    public bool Pending { get; }
    public int PointsPerStage { get; }
    public IReadOnlyList<SeasonPassSourceDef> Sources => _sources;

    /// <summary>缺表时的空表：没有来源、每阶点数 0，但 IsMissing=true，领取直接拒绝。</summary>
    public static SeasonPassProgressTable Missing() =>
        new(0, false, 0, new List<SeasonPassSourceDef>(), missing: true);

    /// <summary>这一阶要多少点：阶号 × points_per_stage。阶号从 1 起，阶门槛也跟着走。</summary>
    public int RequiredPoints(int stage) => checked(stage * PointsPerStage);

    /// <summary>按来源编号找。表外的一律 null（调用方拒绝，不猜一个来源）。</summary>
    public SeasonPassSourceDef? Find(string sourceId)
    {
        for (var i = 0; i < _sources.Count; i++)
        {
            if (string.Equals(_sources[i].Id, sourceId, StringComparison.Ordinal))
            {
                return _sources[i];
            }
        }

        return null;
    }

    /// <summary>副本通关按副本编号找来源。没有这个副本的来源返回 null（这一场不加点）。</summary>
    public SeasonPassSourceDef? FindByDungeon(string dungeonId)
    {
        for (var i = 0; i < _sources.Count; i++)
        {
            var source = _sources[i];
            if (string.Equals(source.Kind, DungeonClear, StringComparison.Ordinal)
                && string.Equals(source.DungeonId, dungeonId, StringComparison.Ordinal))
            {
                return source;
            }
        }

        return null;
    }

    /// <summary>主线推进按种类找唯一的那个来源；没配就是 null。</summary>
    public SeasonPassSourceDef? FindMainline()
    {
        for (var i = 0; i < _sources.Count; i++)
        {
            if (string.Equals(_sources[i].Kind, MainlineStage, StringComparison.Ordinal))
            {
                return _sources[i];
            }
        }

        return null;
    }

    /// <summary>与角色服真实启动、所有只读入口共用同一条校验链。</summary>
    public static SeasonPassProgressTable Load(string path, DungeonTable dungeons)
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
            throw new ConfigException("通行证进度表不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("通行证进度表顶层必须是对象");
            }

            foreach (var key in RequiredTopKeys)
            {
                if (!root.TryGetProperty(key, out _))
                {
                    throw new ConfigException("season_pass_progress.json 缺字段 " + key);
                }
            }

            RejectUnknownKeys(root, AllowedTopKeys, "顶层");

            Wire wire;
            try
            {
                wire = JsonSerializer.Deserialize<Wire>(text, JsonOptions)
                    ?? throw new ConfigException("通行证进度表是空的");
            }
            catch (JsonException ex)
            {
                throw new ConfigException("通行证进度表字段类型不对：" + ex.Message);
            }

            if (!string.Equals((wire.Schema ?? "").Trim(), WrittenSchema, StringComparison.Ordinal))
            {
                throw new ConfigException(
                    "season_pass_progress.json 的 schema 必须是 " + WrittenSchema);
            }

            if (wire.Version is null || wire.Version.Value <= 0)
            {
                throw new ConfigException("season_pass_progress.json 的 version 必须大于 0");
            }

            var pointsPerStage = wire.PointsPerStage ?? 0;
            if (pointsPerStage is < 1 or > 1_000_000)
            {
                throw new ConfigException(
                    "season_pass_progress.json 的 points_per_stage 要在 1..1000000 之间");
            }

            var sourcesElement = root.GetProperty("sources");
            if (sourcesElement.ValueKind != JsonValueKind.Array
                || sourcesElement.GetArrayLength() == 0)
            {
                throw new ConfigException("season_pass_progress.json 的 sources 必须是非空数组");
            }

            var sources = new List<SeasonPassSourceDef>(sourcesElement.GetArrayLength());
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenDungeons = new HashSet<string>(StringComparer.Ordinal);
            var mainlineSeen = 0;
            var index = 0;
            foreach (var row in sourcesElement.EnumerateArray())
            {
                index++;
                RejectUnknownKeys(row, AllowedSourceKeys, "第 " + index + " 个来源");

                var id = (row.TryGetProperty("id", out var idValue)
                    ? (idValue.GetString() ?? "") : "").Trim();
                if (id.Length == 0)
                {
                    throw new ConfigException("通行证进度表第 " + index + " 个来源没有 id");
                }

                if (!seenIds.Add(id))
                {
                    throw new ConfigException("通行证进度表的来源重复：" + id);
                }

                var kind = (row.TryGetProperty("kind", out var kindValue)
                    ? (kindValue.GetString() ?? "") : "").Trim();
                if (Array.IndexOf(Kinds, kind) < 0)
                {
                    throw new ConfigException("通行证进度表 " + id + " 的 kind 只认 "
                        + string.Join(" / ", Kinds) + "，现在是「" + kind + "」");
                }

                var points = row.TryGetProperty("points", out var pointsValue)
                    ? pointsValue.GetInt32() : 0;
                if (points is < 1 or > 100_000)
                {
                    throw new ConfigException(
                        "通行证进度表 " + id + " 的 points 要在 1..100000 之间，现在是 " + points);
                }

                var period = (row.TryGetProperty("period", out var periodValue)
                    ? (periodValue.GetString() ?? "") : "").Trim();
                if (Array.IndexOf(Periods, period) < 0)
                {
                    throw new ConfigException("通行证进度表 " + id + " 的 period 只认 "
                        + string.Join(" / ", Periods) + "，现在是「" + period + "」");
                }

                var dungeonId = (row.TryGetProperty("dungeon_id", out var dungeonValue)
                    ? (dungeonValue.GetString() ?? "") : "").Trim();
                if (string.Equals(kind, DungeonClear, StringComparison.Ordinal))
                {
                    if (dungeonId.Length == 0)
                    {
                        throw new ConfigException("通行证进度表 " + id + " 是副本来源，必须写 dungeon_id");
                    }

                    if (dungeons.Find(dungeonId) is null)
                    {
                        throw new ConfigException("通行证进度表 " + id
                            + " 的 dungeon_id 不在 config/dungeons.json 里：" + dungeonId);
                    }

                    if (!seenDungeons.Add(dungeonId))
                    {
                        throw new ConfigException("同一个副本不能配两个通行证点数来源：" + dungeonId);
                    }
                }
                else
                {
                    if (dungeonId.Length > 0)
                    {
                        throw new ConfigException("通行证进度表 " + id + " 是主线来源，不许挂 dungeon_id");
                    }

                    mainlineSeen++;
                    if (mainlineSeen > 1)
                    {
                        throw new ConfigException("主线来源只能配一个（同一个主线场次不能算两次）");
                    }
                }

                var maxPerPeriod = row.TryGetProperty("max_per_period", out var maxValue)
                    ? maxValue.GetInt32() : 0;
                if (string.Equals(period, "once", StringComparison.Ordinal))
                {
                    // once 就是「这一季之外也只给一次」：写别的数就是想把一次变成多次。
                    if (maxPerPeriod is not (0 or 1))
                    {
                        throw new ConfigException("通行证进度表 " + id + " 的 period=once 时"
                            + " max_per_period 只能是 1（或不写）");
                    }

                    maxPerPeriod = 1;
                }
                else if (maxPerPeriod is < 1 or > 1000)
                {
                    throw new ConfigException("通行证进度表 " + id + " 的 max_per_period 要在 1..1000 之间");
                }

                sources.Add(new SeasonPassSourceDef(id, kind, dungeonId, points, period, maxPerPeriod));
            }

            return new SeasonPassProgressTable(
                wire.Version!.Value, wire.Pending ?? false, pointsPerStage, sources);
        }
    }

    static readonly string[] RequiredTopKeys =
        ["schema", "version", "points_per_stage", "sources"];

    static void RejectUnknownKeys(JsonElement row, string[] allowed, string where)
    {
        foreach (var property in row.EnumerateObject())
        {
            var ok = false;
            foreach (var candidate in allowed)
            {
                if (string.Equals(property.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    ok = true;
                    break;
                }
            }

            if (!ok)
            {
                throw new ConfigException("season_pass_progress.json " + where
                    + " 不许出现 " + property.Name);
            }
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; init; }
        [JsonPropertyName("version")] public int? Version { get; init; }
        [JsonPropertyName("pending")] public bool? Pending { get; init; }
        [JsonPropertyName("points_per_stage")] public int? PointsPerStage { get; init; }
    }
}
