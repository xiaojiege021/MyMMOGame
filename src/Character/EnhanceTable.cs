using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>强化表里的一档。等级区间是方案 9.2 写定的规则，消耗与成功率是待定数值。</summary>
public sealed record EnhanceTier(
    string Id,
    int FromLevel,
    int ToLevel,
    string StoneId,
    int StoneCost,
    long GoldCost,
    double SuccessRate);

/// <summary>
/// config/enhance.json 是强化数值的唯一正本：三档材料、每段成功率、金币与石头消耗。
/// 方案写定的「最高 +12」「+1~+6 低级石 / +7~+9 中级石 / +10~+12 高级石」
/// 在这里逐条校验，改配置不能悄悄改掉玩法；没写定的数值只从表里读，代码里不写第二套。
/// 角色服是唯一读表方（开发架构第 7 节「角色服读强化概率」）。
/// </summary>
public sealed class EnhanceTable
{
    /// <summary>方案 9.2 写定的上限，配置必须一致。</summary>
    public const int WrittenMaxLevel = 12;

    /// <summary>方案 9.2 写定的三档顺序。</summary>
    public static readonly string[] WrittenTierIds = ["low", "mid", "high"];

    /// <summary>方案 9.2 写定的三档等级分界。</summary>
    static readonly (int From, int To)[] WrittenBounds = [(1, 6), (7, 9), (10, 12)];

    readonly EnhanceTier[] tiers;

    public int Version { get; }

    public int MaxLevel { get; }

    public string GoldKind { get; }

    public IReadOnlyList<EnhanceTier> Tiers => tiers;

    EnhanceTable(EnhanceTier[] tiers, int version, int maxLevel, string goldKind)
    {
        this.tiers = tiers;
        Version = version;
        MaxLevel = maxLevel;
        GoldKind = goldKind;
    }

    /// <summary>目标等级落在哪一档。只接受 1 到 MaxLevel，越界由调用方先挡。</summary>
    public EnhanceTier TierFor(int targetLevel) =>
        tiers.First(t => targetLevel >= t.FromLevel && targetLevel <= t.ToLevel);

    public static EnhanceTable Load(string path, ItemTable items, CurrencyTable currencies)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到强化表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("强化表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("强化表不是合法 JSON：" + ex.Message);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("enhance.json 的 version 必须大于 0");
        }

        // 方案 9.2 写定的上限：配置写别的就是改玩法，直接起不来。
        var maxLevel = wire.MaxLevel ?? throw new ConfigException("enhance.json 缺 max_level");
        if (maxLevel != WrittenMaxLevel)
        {
            throw new ConfigException("enhance.json 的 max_level 必须是方案写定的 12，现在是 " + maxLevel);
        }

        var goldKind = (wire.GoldKind ?? "").Trim();
        if (goldKind.Length == 0)
        {
            throw new ConfigException("enhance.json 缺 gold_kind");
        }

        if (currencies.Find(goldKind) is null)
        {
            throw new ConfigException("enhance.json 的 gold_kind 不在币种表里：" + goldKind);
        }

        if (wire.Tiers is null || wire.Tiers.Count != WrittenTierIds.Length)
        {
            throw new ConfigException("enhance.json 必须正好是方案 9.2 写定的三档");
        }

        var parsed = new EnhanceTier[wire.Tiers.Count];
        var stoneIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < wire.Tiers.Count; i++)
        {
            var raw = wire.Tiers[i];
            var id = (raw.Id ?? "").Trim();
            if (!string.Equals(id, WrittenTierIds[i], StringComparison.Ordinal))
            {
                throw new ConfigException("enhance.json 第 " + (i + 1) + " 档必须是 " + WrittenTierIds[i]);
            }

            var from = raw.FromLevel ?? throw new ConfigException("enhance.json 的 " + id + " 缺 from_level");
            var to = raw.ToLevel ?? throw new ConfigException("enhance.json 的 " + id + " 缺 to_level");
            if (from != WrittenBounds[i].From || to != WrittenBounds[i].To)
            {
                throw new ConfigException("enhance.json 的 " + id + " 等级分界必须照方案 9.2 写定，"
                    + "现在是 " + from + "-" + to);
            }

            var stoneId = (raw.StoneId ?? "").Trim();
            if (stoneId.Length == 0)
            {
                throw new ConfigException("enhance.json 的 " + id + " 缺 stone_id");
            }

            if (items.Find(stoneId) is null)
            {
                throw new ConfigException("enhance.json 的 " + id + " 材料不在 items.json 里：" + stoneId);
            }

            if (!stoneIds.Add(stoneId))
            {
                throw new ConfigException("enhance.json 三档不能共用同一种强化石：" + stoneId);
            }

            var stoneCost = raw.StoneCost ?? throw new ConfigException("enhance.json 的 " + id + " 缺 stone_cost");
            if (stoneCost < 1)
            {
                throw new ConfigException("enhance.json 的 " + id + " stone_cost 必须大于 0");
            }

            var goldCost = raw.GoldCost ?? throw new ConfigException("enhance.json 的 " + id + " 缺 gold_cost");
            if (goldCost < 1)
            {
                throw new ConfigException("enhance.json 的 " + id + " gold_cost 必须大于 0");
            }

            var rate = raw.SuccessRate
                ?? throw new ConfigException("enhance.json 的 " + id + " 缺 success_rate");
            if (double.IsNaN(rate) || rate < 0.0 || rate > 1.0)
            {
                throw new ConfigException("enhance.json 的 " + id + " success_rate 必须在 0 到 1 之间");
            }

            parsed[i] = new EnhanceTier(id, from, to, stoneId, stoneCost, goldCost, rate);
        }

        return new EnhanceTable(parsed, wire.Version, maxLevel, goldKind);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("max_level")]
        public int? MaxLevel { get; init; }

        [JsonPropertyName("gold_kind")]
        public string? GoldKind { get; init; }

        [JsonPropertyName("tiers")]
        public List<TierWire>? Tiers { get; init; }
    }

    sealed record TierWire
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("from_level")]
        public int? FromLevel { get; init; }

        [JsonPropertyName("to_level")]
        public int? ToLevel { get; init; }

        [JsonPropertyName("stone_id")]
        public string? StoneId { get; init; }

        [JsonPropertyName("stone_cost")]
        public int? StoneCost { get; init; }

        [JsonPropertyName("gold_cost")]
        public long? GoldCost { get; init; }

        [JsonPropertyName("success_rate")]
        public double? SuccessRate { get; init; }
    }
}
