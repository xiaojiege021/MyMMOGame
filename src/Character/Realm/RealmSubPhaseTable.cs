using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// R1：段内阶与被动。阶数按境界配置——炼体 1–9 阶，筑基及以后仍是初/中/后/大圆满。
/// 数值只在 config/realms.json 的 sub_phases.by_realm 里，代码里不写第二套数。
/// </summary>
public sealed record RealmSubPhases(
    IReadOnlyList<string> Names,
    IReadOnlyList<int> PassivePctBps,
    IReadOnlyList<int> PromoteMinLevel,
    int BreakthroughMinIndex)
{
    public int Count => Names.Count;

    public string NameAt(int index)
        => index >= 0 && index < Names.Count ? Names[index] : "";

    public int PassiveBpsAt(int index)
        => index >= 0 && index < PassivePctBps.Count ? PassivePctBps[index] : 0;

    public int PromoteLevelAt(int index)
        => index >= 0 && index < PromoteMinLevel.Count ? PromoteMinLevel[index] : 0;
}

public sealed partial class RealmTable
{

    /// <summary>兼容旧访问：返回 * 默认那套（筑基及以后的四阶）。</summary>
    public RealmSubPhases SubPhases => _defaultSubPhases;

    /// <summary>按境界取段内阶；不认识的境界回 * 默认。</summary>
    public RealmSubPhases SubPhasesFor(string? realm)
        => _subPhasesByRealm.GetValueOrDefault(Normalize(realm), _defaultSubPhases);

    /// <summary>配置里显式写出的境界阶集（含 *），只给只读入口展示。</summary>
    public IReadOnlyDictionary<string, RealmSubPhases> SubPhasesByRealm => _subPhasesByRealm;

    static RealmSubPhases ParseSubPhase(string key, SubPhaseItem? item, int? expectedCount, bool isDefault)
    {
        var label = "realms.json 的 sub_phases.by_realm." + key;
        if (item is null)
        {
            throw new ConfigException(label + " 是空的");
        }

        var names = (item.Names ?? [])
            .Select(n => (n ?? "").Trim())
            .ToList();
        var passive = item.PassivePctBps ?? [];
        var promoteLevel = item.PromoteMinLevel ?? [];

        if (names.Count == 0)
        {
            throw new ConfigException(label + ".names 不能为空");
        }

        if (names.Count != passive.Count || names.Count != promoteLevel.Count)
        {
            throw new ConfigException(label + " 的 names / passive_pct_bps / promote_min_level 长度必须一致");
        }

        if (expectedCount is not null && names.Count != expectedCount.Value)
        {
            throw new ConfigException(
                label + " 必须正好 " + expectedCount.Value + " 阶，配置里是 " + names.Count);
        }

        if (names.Any(n => n.Length == 0) || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            throw new ConfigException(label + ".names 有空或重复");
        }

        if (passive.Any(p => p < 0 || p > 500))
        {
            throw new ConfigException(label + ".passive_pct_bps 每项须在 0–500（0%–5%）");
        }

        for (var i = 1; i < passive.Count; i++)
        {
            if (passive[i] < passive[i - 1])
            {
                throw new ConfigException(label + ".passive_pct_bps 须非递减");
            }
        }

        if (promoteLevel.Any(l => l < 1 || l > 100))
        {
            throw new ConfigException(label + ".promote_min_level 每项须在 1–100");
        }

        for (var i = 1; i < promoteLevel.Count; i++)
        {
            if (promoteLevel[i] < promoteLevel[i - 1])
            {
                throw new ConfigException(label + ".promote_min_level 须非递减");
            }
        }

        if (item.BreakthroughMinIndex is null)
        {
            throw new ConfigException(label + " 缺 breakthrough_min_index");
        }

        var breakthroughMin = item.BreakthroughMinIndex.Value;
        if (breakthroughMin < 0 || breakthroughMin >= names.Count)
        {
            throw new ConfigException(
                label + ".breakthrough_min_index 必须落在 0.." + (names.Count - 1));
        }

        // * 默认仍是方案 8.1 的四阶：第三阶「后期」、大圆满给满该段 2%–3%。
        if (isDefault)
        {
            if (!string.Equals(names[2], "后期", StringComparison.Ordinal))
            {
                throw new ConfigException(label + ".names 第三阶必须是「后期」");
            }

            if (passive[^1] < 200 || passive[^1] > 300)
            {
                throw new ConfigException(label + ".passive_pct_bps 大圆满须在 200–300（2%–3%）");
            }
        }

        return new RealmSubPhases(names, passive, promoteLevel, breakthroughMin);
    }
    /// <summary>读 realms.json 的 sub_phases 段：默认那套 + 每个境界自己那套。</summary>
    static (Dictionary<string, RealmSubPhases> ByRealm, RealmSubPhases Default) Build(
        Wire wire, List<string> order)
    {
            var sub = wire.SubPhases;
            if (sub is null)
            {
                throw new ConfigException("realms.json 缺 sub_phases（R1 段内阶按境界配置）");
            }

            if (sub.ByRealm is null || sub.ByRealm.Count == 0)
            {
                throw new ConfigException("realms.json 的 sub_phases 缺 by_realm");
            }

            if (!sub.ByRealm.ContainsKey("*"))
            {
                throw new ConfigException("realms.json 的 sub_phases.by_realm 必须含 * 默认");
            }

            var subPhasesByRealm = new Dictionary<string, RealmSubPhases>(StringComparer.Ordinal);
            foreach (var pair in sub.ByRealm)
            {
                var key = (pair.Key ?? "").Trim();
                if (key.Length == 0)
                {
                    throw new ConfigException("realms.json 的 sub_phases.by_realm 里有空名字");
                }

                if (key != "*" && !order.Contains(key))
                {
                    throw new ConfigException("realms.json 的 sub_phases.by_realm 不在顺序表里：" + key);
                }

                if (subPhasesByRealm.ContainsKey(key))
                {
                    throw new ConfigException("realms.json 的 sub_phases.by_realm 重复：" + key);
                }

                // R1 结构口径：炼体 9 阶、* 默认 4 阶；其它境界按各自配置，但长度必须自洽。
                int? expected = key switch
                {
                    "炼体" => 9,
                    "*" => 4,
                    _ => null,
                };
                subPhasesByRealm[key] = ParseSubPhase(key, pair.Value, expected, key == "*");
            }

            var defaultSubPhases = subPhasesByRealm["*"];
        return (subPhasesByRealm, defaultSubPhases);
    }
}


    sealed record SubPhasesItem
    {
        [JsonPropertyName("pending")]
        public bool? Pending { get; init; }

        [JsonPropertyName("note")]
        public string? Note { get; init; }

        [JsonPropertyName("by_realm")]
        public Dictionary<string, SubPhaseItem?>? ByRealm { get; init; }
    }


    sealed record SubPhaseItem
    {
        [JsonPropertyName("names")]
        public List<string?>? Names { get; init; }

        [JsonPropertyName("passive_pct_bps")]
        public List<int>? PassivePctBps { get; init; }

        [JsonPropertyName("promote_min_level")]
        public List<int>? PromoteMinLevel { get; init; }

        [JsonPropertyName("breakthrough_min_index")]
        public int? BreakthroughMinIndex { get; init; }
    }
