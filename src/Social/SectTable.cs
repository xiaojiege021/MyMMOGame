using System.Text.Json;
using System.Text.Json.Serialization;

namespace Social;

/// <summary>
/// 宗门五级人数上限 + 四个职位席位（config/sect.json）——唯一正本。方案 11.1 写定
/// 五级与每档名册上限，以及掌门 1 / 副掌门 1 / 长老 4 / 执事 12 的席位；这两组数字
/// 只从这里读，代码、proto、SQL 都不写第二套。人数是名册上限，不是进本人数
/// （技术架构第 49 行、开发架构 4.4）。
///
/// 方案 11.1 还写定「45 级之后可以建立宗门，创建消耗金币」：门槛与金币种类、
/// 金币数（待定）也只从这份配置读，SectFounderGrant 照它向角色服提交一次事务。
/// 改这份配置重启社交服生效，不在代码里给数值兜底。
///
/// P2-1a2：disciple 是无席位上限的默认职位（不在 positions 里、不占席位）；
/// 职位不加属性、不代替境界；1 级只有 10 个席位，任满为止，不要求把执事名额填满。
/// 职位席位之和不超过 5 级名册上限（按配置挡，不写死 200）。
/// </summary>
public sealed class SectTable
{
    public const string WrittenSchema = "sect-table/1";

    // disciple 是默认职位，无席位上限；这四个才是有席位上限的职位。
    public const string DefaultPosition = "disciple";
    public static readonly string[] SeatedPositionIds =
        { "master", "vice_master", "elder", "steward" };

    public sealed record Level(int Number, int MemberCap, string Title);

    public sealed record Seat(string Id, int Cap);

    public sealed record CreateRule(
        int FounderLevelGate, string FounderGoldKind, long FounderGoldCost,
        bool FounderGoldCostPending);

    public int Version { get; }
    public string Note { get; }
    public IReadOnlyList<Level> Levels { get; }
    public IReadOnlyList<Seat> Positions { get; }
    public CreateRule Create { get; }

    SectTable(int version, string note, IReadOnlyList<Level> levels,
        IReadOnlyList<Seat> positions, CreateRule create)
    {
        Version = version;
        Note = note;
        Levels = levels;
        Positions = positions;
        Create = create;
    }

    /// <summary>按等级取名册上限。等级不在表里时抛 ConfigException，由调用方回拒绝。</summary>
    public int CapFor(int level)
    {
        foreach (var l in Levels)
        {
            if (l.Number == level)
            {
                return l.MemberCap;
            }
        }

        throw new ConfigException("宗门等级不在配置里：" + level);
    }

    /// <summary>五个职位名都认（含默认弟子）；用于校验请求里的 position。</summary>
    public static bool IsKnownPosition(string? position) =>
        position == DefaultPosition || IsSeatedPosition(position);

    /// <summary>只有这四个职位有席位上限；disciple 不占席位。</summary>
    public static bool IsSeatedPosition(string? position) =>
        position is not null && Array.IndexOf(SeatedPositionIds, position) >= 0;

    /// <summary>按职位名取席位上限。席位只从配置正本读，找不到就抛 ConfigException。</summary>
    public int SeatCapFor(string position)
    {
        foreach (var p in Positions)
        {
            if (p.Id == position)
            {
                return p.Cap;
            }
        }

        throw new ConfigException("职位不在配置里：" + position);
    }

    public static SectTable Load(string path)
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
            throw new ConfigException("sect.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("sect.json 的 version 必须是正整数");
        }

        var raw = wire.Levels;
        if (raw is null || raw.Count == 0)
        {
            throw new ConfigException("sect.json 的 levels 不能为空（缺段）");
        }

        // 方案写定等级仍是五级；少一档、多一档都起不来。
        if (raw.Count != 5)
        {
            throw new ConfigException("sect.json 必须正好五档，现在是 " + raw.Count);
        }

        var levels = new List<Level>();
        var seen = new HashSet<int>();
        long previousCap = 0;
        for (var i = 0; i < raw.Count; i++)
        {
            var l = raw[i];
            if (l is null)
            {
                throw new ConfigException("sect.json 的 levels 有空项");
            }

            // 等级必须正好 1..5 顺序：重复、跳号、缺档都在这里挡住。
            if (l.Level != i + 1 || !seen.Add(l.Level))
            {
                throw new ConfigException("sect.json 第 " + (i + 1) + " 档的 level 必须是 "
                    + (i + 1) + "（等级重复或缺档）");
            }

            if (l.MemberCap <= 0)
            {
                throw new ConfigException("sect.json 第 " + l.Level + " 档 member_cap 必须是正整数");
            }

            if (l.MemberCap <= previousCap)
            {
                throw new ConfigException("sect.json 的 member_cap 必须严格递增，第 "
                    + l.Level + " 档没有比上一档大");
            }

            previousCap = l.MemberCap;

            if ((l.Title ?? "").Trim().Length == 0)
            {
                throw new ConfigException("sect.json 第 " + l.Level + " 档没有 title");
            }

            levels.Add(new Level(l.Level, l.MemberCap, l.Title!.Trim()));
        }

        // ---- P2-1a2：四个有席位职位的硬校验 ----
        var rawPositions = wire.Positions;
        if (rawPositions is null || rawPositions.Count == 0)
        {
            throw new ConfigException("sect.json 缺 positions 段");
        }

        var positions = new List<Seat>();
        var seenPositions = new HashSet<string>();
        foreach (var p in rawPositions)
        {
            if (p is null)
            {
                throw new ConfigException("sect.json 的 positions 有空项");
            }

            var id = (p.Id ?? "").Trim();
            if (id.Length == 0 || !IsSeatedPosition(id))
            {
                throw new ConfigException("sect.json 的 positions 有未知职位：" + id);
            }

            if (!seenPositions.Add(id))
            {
                throw new ConfigException("sect.json 的 positions 职位编号重复：" + id);
            }

            if (p.SeatCap <= 0)
            {
                throw new ConfigException("sect.json 的 " + id + " seat_cap 必须是正整数");
            }

            positions.Add(new Seat(id, p.SeatCap));
        }

        foreach (var required in SeatedPositionIds)
        {
            if (!seenPositions.Contains(required))
            {
                throw new ConfigException("sect.json 的 positions 缺职位：" + required);
            }
        }

        // 方案写定掌门只有 1 席；不是 1 就起不来。
        foreach (var p in positions)
        {
            if (p.Id == "master" && p.Cap != 1)
            {
                throw new ConfigException("sect.json 的掌门席位必须是 1，现在是 " + p.Cap);
            }
        }

        // 职位席位之和不超过 5 级名册上限（按配置挡，不写死 200）。
        var maxCap = levels.Max(l => l.MemberCap);
        long seatSum = 0;
        foreach (var p in positions)
        {
            seatSum += p.Cap;
        }

        if (seatSum > maxCap)
        {
            throw new ConfigException("sect.json 的职位席位之和 " + seatSum
                + " 超过 5 级名册上限 " + maxCap);
        }

        var c = wire.Create;
        if (c is null)
        {
            throw new ConfigException("sect.json 缺 create 段");
        }

        if (c.FounderLevelGate < 1)
        {
            throw new ConfigException("sect.json 的 founder_level_gate 必须是正整数，现在是 "
                + c.FounderLevelGate);
        }

        var kind = (c.FounderGoldKind ?? "").Trim();
        if (kind.Length == 0)
        {
            throw new ConfigException("sect.json 的 founder_gold_kind 不能为空");
        }

        if (c.FounderGoldCost < 0)
        {
            throw new ConfigException("sect.json 的 founder_gold_cost 不能为负");
        }

        var create = new CreateRule(c.FounderLevelGate, kind, c.FounderGoldCost,
            c.FounderGoldCostPending);

        return new SectTable(wire.Version, (wire.Note ?? "").Trim(), levels, positions, create);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("levels")] public List<WireLevel?>? Levels { get; set; }
        [JsonPropertyName("positions")] public List<WirePosition?>? Positions { get; set; }
        [JsonPropertyName("create")] public WireCreate? Create { get; set; }
    }

    sealed class WireLevel
    {
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("member_cap")] public int MemberCap { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }

    sealed class WirePosition
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("seat_cap")] public int SeatCap { get; set; }
    }

    sealed class WireCreate
    {
        [JsonPropertyName("founder_level_gate")] public int FounderLevelGate { get; set; }
        [JsonPropertyName("founder_gold_kind")] public string? FounderGoldKind { get; set; }
        [JsonPropertyName("founder_gold_cost")] public long FounderGoldCost { get; set; }
        [JsonPropertyName("founder_gold_cost_pending")] public bool FounderGoldCostPending { get; set; }
    }
}
