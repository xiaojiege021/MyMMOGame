using Mmo.Shared.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Social;

/// <summary>
/// 队伍正本（config/party.json，Q7/H1）——人数上限、邀请超时、准备超时、
/// 队长继任方式与等级区间的唯一来源。方案 11 只写定「小队是 4 人战斗单元」，
/// 其余数值方案没写，全部是占位值（pending）；改这份配置重启社交服生效，
/// 代码 / proto / 库里都不写第二套数字。
///
/// 队伍状态本身**不落库**：技术架构第 3 节把在线关系类临时态放在进程内存，
/// 社交服重启或全员掉线就散，不写任何 schema。所以本表只有静态规则，
/// 没有运行期字段（人数、谁在队里、ready 与否都进不了配置）。
/// </summary>
public sealed class PartyTable
{
    public const string WrittenSchema = "party-table/1";

    static readonly string[] TopKeys =
        ["schema", "version", "pending", "note", "max_size", "invite_timeout_s",
         "ready_check_timeout_s", "leader_succession", "loot", "level_range"];

    static readonly string[] LootKeys = ["mode", "roll", "roll_min", "roll_max"];
    static readonly string[] LevelKeys = ["enabled", "max_gap"];
    static readonly string[] LootModes = ["personal", "need_greed"];
    static readonly string[] RollModes = ["none", "server"];

    /// <summary>队长离队后谁接班：转给最早加入的成员，或者整队解散。</summary>
    static readonly string[] Successions = ["earliest_joined", "disband"];

    /// <summary>
    /// 分配段（Q8 落地）。mode=personal 时每人各发整份、不掷点（roll 必须 none）；
    /// mode=need_greed 时全队只出这一份，掷点只由服务端在 [RollMin, RollMax] 里掷。
    /// 区间是数值，方案没写，留在配置里标 pending。
    /// </summary>
    public sealed record Loot(string Mode, string Roll, int RollMin, int RollMax);

    /// <summary>等级区间段。方案没写，默认关着；开着时 Q7 仍不拦人（留给后续卡）。</summary>
    public sealed record LevelRange(bool Enabled, int MaxGap);

    public int Version { get; }
    public bool Pending { get; }
    public string Note { get; }
    public int MaxSize { get; }
    public int InviteTimeoutSeconds { get; }
    public int ReadyCheckTimeoutSeconds { get; }
    public string LeaderSuccession { get; }
    public Loot LootRule { get; }
    public LevelRange Levels { get; }

    PartyTable(int version, bool pending, string note, int maxSize, int inviteTimeoutSeconds,
        int readyCheckTimeoutSeconds, string leaderSuccession, Loot loot, LevelRange levels)
    {
        Version = version;
        Pending = pending;
        Note = note;
        MaxSize = maxSize;
        InviteTimeoutSeconds = inviteTimeoutSeconds;
        ReadyCheckTimeoutSeconds = readyCheckTimeoutSeconds;
        LeaderSuccession = leaderSuccession;
        LootRule = loot;
        Levels = levels;
    }

    /// <summary>
    /// 可选表：文件不在就返回 null（老卡的临时配置目录没有 party.json 时照常起服务，
    /// 队伍 RPC 回 party_unavailable）；文件在就按硬校验读，坏了起不来。
    /// </summary>
    public static PartyTable? LoadIfPresent(string path) =>
        File.Exists(path) ? Load(path) : null;

    public static PartyTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配置文件 " + path);
        }

        var rawText = File.ReadAllText(path);
        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(rawText, JsonOptions)
                ?? throw new ConfigException("配置是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配置不是合法 JSON：" + ex.Message);
        }

        // 反口径：小队人数是战斗单元的上限，不能靠配置放开成「想多少人多少人」。
        foreach (var claim in UnlimitedClaims)
        {
            if (rawText.Contains(claim, StringComparison.Ordinal))
            {
                throw new ConfigException("party.json 不允许出现「" + claim
                    + "」：小队人数按方案写定的战斗单元上限，不做无上限队伍");
            }
        }

        using (var doc = JsonDocument.Parse(rawText))
        {
            foreach (var key in doc.RootElement.EnumerateObject())
            {
                if (Array.IndexOf(TopKeys, key.Name) < 0)
                {
                    throw new ConfigException("party.json 顶层有未知键：" + key.Name);
                }
            }

            foreach (var section in new[] { "loot", "level_range" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var node))
                {
                    throw new ConfigException("party.json 缺 " + section + " 段");
                }

                if (node.ValueKind != JsonValueKind.Object)
                {
                    throw new ConfigException("party.json 的 " + section + " 不是对象");
                }

                var allowed = section == "loot" ? LootKeys : LevelKeys;
                foreach (var key in node.EnumerateObject())
                {
                    if (Array.IndexOf(allowed, key.Name) < 0)
                    {
                        throw new ConfigException("party.json 的 " + section
                            + " 有未知键：" + key.Name);
                    }
                }
            }
        }

        if (!string.Equals(wire.Schema, WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException("party.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("party.json 的 version 必须是正整数");
        }

        // 方案 11 写定 4 人战斗单元；配置允许 1..8 是为了龙巢 8 人上限这类后续扩展，
        // 但默认值必须是写定的那个，改大改小只能改这份配置。
        if (wire.MaxSize < 1 || wire.MaxSize > 8)
        {
            throw new ConfigException("party.json 的 max_size 必须在 1..8，现在是 " + wire.MaxSize);
        }

        if (wire.InviteTimeoutS < 1 || wire.InviteTimeoutS > 3600)
        {
            throw new ConfigException("party.json 的 invite_timeout_s 必须在 1..3600，现在是 "
                + wire.InviteTimeoutS);
        }

        if (wire.ReadyCheckTimeoutS < 1 || wire.ReadyCheckTimeoutS > 600)
        {
            throw new ConfigException("party.json 的 ready_check_timeout_s 必须在 1..600，现在是 "
                + wire.ReadyCheckTimeoutS);
        }

        var succession = (wire.LeaderSuccession ?? "").Trim();
        if (Array.IndexOf(Successions, succession) < 0)
        {
            throw new ConfigException("party.json 的 leader_succession 只认 "
                + string.Join(" / ", Successions) + "，现在是 " + (succession.Length == 0 ? "空" : succession));
        }

        var loot = wire.Loot ?? throw new ConfigException("party.json 缺 loot 段");
        var mode = (loot.Mode ?? "").Trim();
        var roll = (loot.Roll ?? "").Trim();
        if (Array.IndexOf(LootModes, mode) < 0)
        {
            throw new ConfigException("party.json 的 loot.mode 只认 "
                + string.Join(" / ", LootModes) + "，现在是 " + (mode.Length == 0 ? "空" : mode));
        }

        if (Array.IndexOf(RollModes, roll) < 0)
        {
            throw new ConfigException("party.json 的 loot.roll 只认 "
                + string.Join(" / ", RollModes) + "，现在是 " + (roll.Length == 0 ? "空" : roll));
        }

        // 掷点只在服务端：方案写定「掷点只在服务端」，配置里不许出现客户端掷点。
        // Q8：personal 不掷点（每人各发整份）；need_greed 必须显式写 server，
        // 不允许「mode 开了需求/贪婪却没写掷点方式」这种半落地配置。
        if (mode == "personal" && roll != "none")
        {
            throw new ConfigException("party.json 的 loot.mode=personal 时 loot.roll 必须是 none（"
                + "个人份额不掷点）；掷点只在 need_greed 下、且只在服务端");
        }

        if (mode == "need_greed" && roll != "server")
        {
            throw new ConfigException("party.json 的 loot.mode=need_greed 时 loot.roll 必须是 server（"
                + "掷点只在服务端；客户端不参与掷点）");
        }

        // 区间只在配置里：代码不写死 1..100，改区间重启社交服生效。
        if (loot.RollMin < 1 || loot.RollMin > 1000)
        {
            throw new ConfigException("party.json 的 loot.roll_min 必须在 1..1000，现在是 "
                + loot.RollMin);
        }

        if (loot.RollMax < 1 || loot.RollMax > 1000)
        {
            throw new ConfigException("party.json 的 loot.roll_max 必须在 1..1000，现在是 "
                + loot.RollMax);
        }

        if (loot.RollMin > loot.RollMax)
        {
            throw new ConfigException("party.json 的 loot.roll_min 不能大于 loot.roll_max（"
                + loot.RollMin + " > " + loot.RollMax + "）");
        }

        var levels = wire.LevelRange ?? throw new ConfigException("party.json 缺 level_range 段");
        if (levels.MaxGap < 1 || levels.MaxGap > 100)
        {
            throw new ConfigException("party.json 的 level_range.max_gap 必须在 1..100，现在是 "
                + levels.MaxGap);
        }

        return new PartyTable(wire.Version, wire.Pending, (wire.Note ?? "").Trim(),
            wire.MaxSize, wire.InviteTimeoutS, wire.ReadyCheckTimeoutS, succession,
            new Loot(mode, roll, loot.RollMin, loot.RollMax),
            new LevelRange(levels.Enabled, levels.MaxGap));
    }

    // 反口径词只针对「把人数上限放开」这一种写法，正常说明里不会出现。
    static readonly string[] UnlimitedClaims =
    {
        "队伍人数不设上限", "队伍可超过上限", "人数不限", "无上限队伍", "队伍不限人数",
    };

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("pending")] public bool Pending { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("max_size")] public int MaxSize { get; set; }
        [JsonPropertyName("invite_timeout_s")] public int InviteTimeoutS { get; set; }
        [JsonPropertyName("ready_check_timeout_s")] public int ReadyCheckTimeoutS { get; set; }
        [JsonPropertyName("leader_succession")] public string? LeaderSuccession { get; set; }
        [JsonPropertyName("loot")] public WireLoot? Loot { get; set; }
        [JsonPropertyName("level_range")] public WireLevelRange? LevelRange { get; set; }
    }

    sealed class WireLoot
    {
        [JsonPropertyName("mode")] public string? Mode { get; set; }
        [JsonPropertyName("roll")] public string? Roll { get; set; }
        [JsonPropertyName("roll_min")] public int RollMin { get; set; }
        [JsonPropertyName("roll_max")] public int RollMax { get; set; }
    }

    sealed class WireLevelRange
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("max_gap")] public int MaxGap { get; set; }
    }
}
