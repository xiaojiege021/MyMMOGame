using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 等级经验这一笔的唯一数值正本。方案只写定「都结算等级经验」「同一场至少两人
/// 多一截少量加成，只加经验」「等级封顶 100」，没写定基础经验、加成幅度、
/// 每级所需经验和升级曲线：这些全在 config/exp.json，代码里不给兜底数字。
/// 缺字段或越界就起不来，免得看起来在跑其实用的是另一套数值。
/// </summary>
public sealed class ExpTable
{
    public int Version { get; }
    public int LevelCap { get; }
    public long BaseExp { get; }
    public int PartyBonusPct { get; }
    public int PartyMinSize { get; }
    public int PartyMaxSize { get; }
    public int SameSectBonusPct { get; }
    public long ExpPerLevel { get; }

    ExpTable(int version, int levelCap, long baseExp, int partyBonusPct,
        int partyMinSize, int partyMaxSize, int sameSectBonusPct, long expPerLevel)
    {
        Version = version;
        LevelCap = levelCap;
        BaseExp = baseExp;
        PartyBonusPct = partyBonusPct;
        PartyMinSize = partyMinSize;
        PartyMaxSize = partyMaxSize;
        SameSectBonusPct = sameSectBonusPct;
        ExpPerLevel = expPerLevel;
    }

    /// <summary>同一场至少两人才有那一截加成；人数更多也不叠加，方案写的是「一截」。</summary>
    public long PartyBonus(int partySize) =>
        partySize >= PartyMinSize ? BaseExp * PartyBonusPct / 100 : 0L;

    /// <summary>
    /// 同宗门这一截：整队都同宗门、且至少两人（本就是组队结算）才有；方案写的是
    /// 「更窄」，独立字段叠在组队加成之外，幅度由 same_sect_bonus_pct 决定，不碰别的账。
    /// </summary>
    public long SameSectBonus(int partySize, bool allSameSect) =>
        allSameSect && partySize >= PartyMinSize ? BaseExp * SameSectBonusPct / 100 : 0L;

    /// <summary>这一笔一共结算多少经验。只加经验，不碰别的账。</summary>
    public long Gain(int partySize, bool allSameSect) =>
        BaseExp + PartyBonus(partySize) + SameSectBonus(partySize, allSameSect);

    /// <summary>
    /// 把这一笔加进当前级经验，够一次升一级。到 level_cap 就停，等级不再往上写；
    /// 剩下的经验仍留在账上，不夹取、不假装成功。库层 CHECK 是最后一道闸。
    /// </summary>
    public (int Level, long Exp) Apply(int level, long exp, long gain)
    {
        var total = exp + gain;
        var next = level;
        while (next < LevelCap && total >= ExpPerLevel)
        {
            total -= ExpPerLevel;
            next++;
        }

        return (next, total);
    }

    public static ExpTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到经验表 " + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("经验表是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("经验表不是合法 JSON：" + ex.Message);
        }

        var version = wire.Version ?? throw new ConfigException("exp.json 缺 version");
        var cap = wire.LevelCap ?? throw new ConfigException("exp.json 缺 level_cap");
        var baseExp = wire.BaseExp ?? throw new ConfigException("exp.json 缺 base_exp");
        var bonusPct = wire.PartyBonusPct ?? throw new ConfigException("exp.json 缺 party_bonus_pct");
        var minSize = wire.PartyMinSize ?? throw new ConfigException("exp.json 缺 party_min_size");
        var maxSize = wire.PartyMaxSize ?? throw new ConfigException("exp.json 缺 party_max_size");
        var sameSectPct = wire.SameSectBonusPct ?? throw new ConfigException("exp.json 缺 same_sect_bonus_pct");
        var perLevel = wire.ExpPerLevel ?? throw new ConfigException("exp.json 缺 exp_per_level");

        // 方案写定上限 100，大版本也不抬；配置只能在这条线以内取值。
        if (cap < 1 || cap > 100)
        {
            throw new ConfigException("level_cap 必须在 1 到 100 之间，现在是 " + cap);
        }

        if (baseExp < 1)
        {
            throw new ConfigException("base_exp 必须大于 0，现在是 " + baseExp);
        }

        if (bonusPct < 0 || bonusPct > 1000)
        {
            throw new ConfigException("party_bonus_pct 必须在 0 到 1000 之间，现在是 " + bonusPct);
        }

        if (sameSectPct < 0 || sameSectPct > 1000)
        {
            throw new ConfigException("same_sect_bonus_pct 必须在 0 到 1000 之间，现在是 " + sameSectPct);
        }

        // 方案写的是「至少两个人」，2 以下的起算人数会改成单人也有加成。
        if (minSize < 2)
        {
            throw new ConfigException("party_min_size 必须至少是 2，现在是 " + minSize);
        }

        if (maxSize < minSize)
        {
            throw new ConfigException("party_max_size 不能小于 party_min_size，现在是 " + maxSize);
        }

        if (perLevel < 1)
        {
            throw new ConfigException("exp_per_level 必须大于 0，现在是 " + perLevel);
        }

        return new ExpTable(version, cap, baseExp, bonusPct, minSize, maxSize, sameSectPct, perLevel);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        [JsonPropertyName("version")]
        public int? Version { get; init; }

        [JsonPropertyName("level_cap")]
        public int? LevelCap { get; init; }

        [JsonPropertyName("base_exp")]
        public long? BaseExp { get; init; }

        [JsonPropertyName("party_bonus_pct")]
        public int? PartyBonusPct { get; init; }

        [JsonPropertyName("party_min_size")]
        public int? PartyMinSize { get; init; }

        [JsonPropertyName("party_max_size")]
        public int? PartyMaxSize { get; init; }

        [JsonPropertyName("same_sect_bonus_pct")]
        public int? SameSectBonusPct { get; init; }

        [JsonPropertyName("exp_per_level")]
        public long? ExpPerLevel { get; init; }
    }
}
