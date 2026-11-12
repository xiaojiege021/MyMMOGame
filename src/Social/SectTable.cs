using System.Text.Json;
using System.Text.Json.Serialization;

namespace Social;

/// <summary>
/// 宗门五级人数上限（config/sect.json）——唯一正本。方案 11.1 写定五级与每档
/// 名册上限；这两个数字只从这里读，代码、proto、SQL 都不写第二套。
/// 人数是名册上限，不是进本人数（技术架构第 49 行、开发架构 4.4）。
///
/// 方案 11.1 还写定「45 级之后可以建立宗门，创建消耗金币」：门槛与金币种类、
/// 金币数（待定）也只从这份配置读，SectFounderGrant 照它向角色服提交一次事务。
/// 改这份配置重启社交服生效，不在代码里给数值兜底。
/// </summary>
public sealed class SectTable
{
    public const string WrittenSchema = "sect-table/1";

    public sealed record Level(int Number, int MemberCap, string Title);

    public sealed record CreateRule(
        int FounderLevelGate, string FounderGoldKind, long FounderGoldCost,
        bool FounderGoldCostPending);

    public int Version { get; }
    public string Note { get; }
    public IReadOnlyList<Level> Levels { get; }
    public CreateRule Create { get; }

    SectTable(int version, string note, IReadOnlyList<Level> levels, CreateRule create)
    {
        Version = version;
        Note = note;
        Levels = levels;
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

        return new SectTable(wire.Version, (wire.Note ?? "").Trim(), levels, create);
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
        [JsonPropertyName("create")] public WireCreate? Create { get; set; }
    }

    sealed class WireLevel
    {
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("member_cap")] public int MemberCap { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }

    sealed class WireCreate
    {
        [JsonPropertyName("founder_level_gate")] public int FounderLevelGate { get; set; }
        [JsonPropertyName("founder_gold_kind")] public string? FounderGoldKind { get; set; }
        [JsonPropertyName("founder_gold_cost")] public long FounderGoldCost { get; set; }
        [JsonPropertyName("founder_gold_cost_pending")] public bool FounderGoldCostPending { get; set; }
    }
}
