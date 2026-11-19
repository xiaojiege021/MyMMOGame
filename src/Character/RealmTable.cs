using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 进本时按境界判定的结果。Ok 为 false 时 Reason 是 realm_locked / realm_not_enough / realm_unknown；
/// 旧副本不写 realm，Required 为空，一律放行。
/// </summary>
public sealed record DungeonRealmGate(bool Ok, string Reason, string Realm, string Required);

/// <summary>
/// 突破到下一段的版本开关判定。本卡只判「下一段有没有在配置里打开」；
/// 修为、历练、炼心与灵石留给 P3-1a2，那一卡复用同一处判定，不另写一套。
/// </summary>
public sealed record RealmAdvance(bool Ok, string Reason, string From, string Next, bool NextOpen);

/// <summary>
/// 一段的突破要求（方案 8.1）。修为就是当前境界，这里不另记一池点数；
/// Drill 是这一笔要扣掉的历练值，Stone / StoneCost 是灵石档位与数量，
/// MainStage 是炼心要查到的已经打开的主线场次，SideQuests 是这一段列出的必要支线。
/// Pill / PillCount 是 B1 的境界丹与枚数（方案 12.1「筑基丹、破镜丹用于境界突破」）：
/// 丹与灵石、历练同一笔事务，掷失败也不退。没配丹的段（B1 之前的临时表）是空串 / 0。
/// 数值都没在方案里写定，全部来自 config/realms.json，代码里不写第二套。
/// </summary>
public sealed record RealmAdvanceRequirement(
    string From,
    string Next,
    long Drill,
    string Stone,
    long StoneCost,
    int SuccessPct,
    int MainStage,
    IReadOnlyList<string> SideQuests,
    string Pill,
    long PillCount);

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

/// <summary>
/// 十一段境界的顺序与版本开关，读 config/realms.json。首发这一段（含）以前一律开放，
/// 之后的每段各有一个开关；开关按顺序打开，后面的段不能在前面还关着时先开。
/// 境界本身仍只由角色服写，这里只回答「这一段开没开」「这个人能不能进这个副本」。
/// </summary>
public sealed class RealmTable
{
    readonly List<string> _order;
    readonly Dictionary<string, string> _aliases;
    readonly HashSet<string> _open;
    readonly Dictionary<string, RealmAdvanceRequirement> _requirements;
    readonly Dictionary<string, RealmSubPhases> _subPhasesByRealm;
    readonly RealmSubPhases _defaultSubPhases;

    public int Version { get; }

    public string LaunchUntil { get; }

    /// <summary>兼容旧访问：返回 * 默认那套（筑基及以后的四阶）。</summary>
    public RealmSubPhases SubPhases => _defaultSubPhases;

    /// <summary>按境界取段内阶；不认识的境界回 * 默认。</summary>
    public RealmSubPhases SubPhasesFor(string? realm)
        => _subPhasesByRealm.GetValueOrDefault(Normalize(realm), _defaultSubPhases);

    /// <summary>配置里显式写出的境界阶集（含 *），只给只读入口展示。</summary>
    public IReadOnlyDictionary<string, RealmSubPhases> SubPhasesByRealm => _subPhasesByRealm;

    public IReadOnlyList<string> Realms => _order;

    public IReadOnlyDictionary<string, string> Aliases => _aliases;

    /// <summary>每一段（极境除外）的突破要求，顺序与 Realms 一致。</summary>
    public IReadOnlyCollection<RealmAdvanceRequirement> Requirements => _requirements.Values;

    RealmTable(
        List<string> order,
        Dictionary<string, string> aliases,
        HashSet<string> open,
        Dictionary<string, RealmAdvanceRequirement> requirements,
        Dictionary<string, RealmSubPhases> subPhasesByRealm,
        RealmSubPhases defaultSubPhases,
        int version,
        string launchUntil)
    {
        _order = order;
        _aliases = aliases;
        _open = open;
        _requirements = requirements;
        _subPhasesByRealm = subPhasesByRealm;
        _defaultSubPhases = defaultSubPhases;
        Version = version;
        LaunchUntil = launchUntil;
    }

    /// <summary>这一段的突破要求；极境没有下一段，返回 null。</summary>
    public RealmAdvanceRequirement? Requirement(string? realm)
        => _requirements.GetValueOrDefault(Normalize(realm));

    /// <summary>把「散仙」这类别名归到顺序表里的名字；不认识的境界原样返回。</summary>
    public string Normalize(string? realm)
    {
        var value = (realm ?? "").Trim();
        return _aliases.TryGetValue(value, out var canonical) ? canonical : value;
    }

    /// <summary>
    /// 方案 8.1 的展示名：第十段按第十场选的路显示——立仙仍显示金仙，立魔显示别名散仙；
    /// 其余境界原样显示顺序表规范名。别名只在这里做展示兼容，库里的 realm 与世界服门槛
    /// 仍用 <see cref="Normalize"/> 后的规范名，不写回配置、不按展示名判定。
    /// </summary>
    public string DisplayName(string? realm, string? alignment)
    {
        var canonical = Normalize(realm);
        if (canonical.Length == 0)
        {
            return "";
        }

        // 第十段是极境之前那一段；别名表里散仙=金仙，只在立魔这一条路显示别名。
        if (_order.Count >= 2
            && string.Equals(canonical, _order[^2], StringComparison.Ordinal)
            && string.Equals((alignment ?? "").Trim(), "立魔", StringComparison.Ordinal))
        {
            foreach (var pair in _aliases)
            {
                if (string.Equals(pair.Value, canonical, StringComparison.Ordinal))
                {
                    return pair.Key;
                }
            }
        }

        return canonical;
    }

    /// <summary>在十一段里的序号，从 1 开始；不认识的是 -1。</summary>
    public int Rank(string? realm) => _order.IndexOf(Normalize(realm)) + 1;

    public bool IsOpen(string? realm) => _open.Contains(Normalize(realm));

    public string? Next(string? realm)
    {
        var rank = Rank(realm);
        return rank < 1 || rank >= _order.Count ? null : _order[rank];
    }

    /// <summary>修为到下一段这一句：下一段没开就 realm_locked，已到极境就 realm_max。</summary>
    public RealmAdvance CheckAdvance(string? current)
    {
        var from = Normalize(current);
        var rank = Rank(from);
        if (rank < 1)
        {
            return new RealmAdvance(false, "realm_unknown", from, "", false);
        }

        if (rank >= _order.Count)
        {
            return new RealmAdvance(false, "realm_max", from, "", true);
        }

        var next = _order[rank];
        var open = IsOpen(next);
        return new RealmAdvance(open, open ? "" : "realm_locked", from, next, open);
    }

    /// <summary>
    /// 进本门槛。先看这一段开没开（关着时谁都进不去），再看个人境界够不够；
    /// 旧副本 Required 为空，直接放行，不看境界。
    /// </summary>
    public DungeonRealmGate CheckDungeon(string? required, string? current)
    {
        var realm = Normalize(current);
        var requiredName = Normalize(required);
        if (requiredName.Length == 0)
        {
            return new DungeonRealmGate(true, "", realm, "");
        }

        var requiredRank = Rank(requiredName);
        if (requiredRank < 1)
        {
            return new DungeonRealmGate(false, "realm_unknown", realm, requiredName);
        }

        if (!IsOpen(requiredName))
        {
            return new DungeonRealmGate(false, "realm_locked", realm, requiredName);
        }

        var rank = Rank(realm);
        if (rank < 1)
        {
            return new DungeonRealmGate(false, "realm_unknown", realm, requiredName);
        }

        return rank < requiredRank
            ? new DungeonRealmGate(false, "realm_not_enough", realm, requiredName)
            : new DungeonRealmGate(true, "", realm, requiredName);
    }

    /// <summary>
    /// 同时校验副本表：写了 realm 的副本只能是首发之后（元婴起）的境界，
    /// 名字必须在十一段里；旧副本写了 realm 直接起不来，堵住「顺手抬高旧门槛」。
    /// </summary>
    public static RealmTable Load(string path, DungeonTable dungeons, CurrencyTable currencies)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到境界表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("境界表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("境界表不是合法 JSON：" + ex.Message);
        }

        var order = (wire.Realms ?? new List<string?>())
            .Select(r => (r ?? "").Trim())
            .ToList();
        if (order.Count == 0)
        {
            throw new ConfigException("realms.json 里一段境界都没有");
        }

        if (order.Any(r => r.Length == 0))
        {
            throw new ConfigException("realms.json 的 realms 里有空名字");
        }

        if (order.Distinct(StringComparer.Ordinal).Count() != order.Count)
        {
            throw new ConfigException("realms.json 的 realms 有重复");
        }

        var launch = (wire.LaunchUntil ?? "").Trim();
        if (launch.Length == 0)
        {
            throw new ConfigException("realms.json 缺 launch_until");
        }

        var launchIndex = order.IndexOf(launch);
        if (launchIndex < 0)
        {
            throw new ConfigException("realms.json 的 launch_until 不在 realms 里：" + launch);
        }

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in wire.Aliases ?? new Dictionary<string, string?>())
        {
            var key = (pair.Key ?? "").Trim();
            var value = (pair.Value ?? "").Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                throw new ConfigException("realms.json 的 aliases 里有空名字");
            }

            if (order.Contains(key))
            {
                throw new ConfigException("realms.json 的别名不能与顺序表里的名字相同：" + key);
            }

            if (!order.Contains(value))
            {
                throw new ConfigException("realms.json 的别名指向不在顺序表里的境界：" + key + "→" + value);
            }

            aliases[key] = value;
        }

        var byRealm = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in wire.Switches ?? new List<SwitchItem>())
        {
            var realm = (item.Realm ?? "").Trim();
            if (realm.Length == 0)
            {
                throw new ConfigException("realms.json 的 switches 里有条目缺 realm");
            }

            if (!order.Contains(realm))
            {
                throw new ConfigException("realms.json 的开关不在顺序表里：" + realm);
            }

            if (byRealm.ContainsKey(realm))
            {
                throw new ConfigException("realms.json 的开关重复：" + realm);
            }

            if (item.Open is null)
            {
                throw new ConfigException("realms.json 的开关缺 open：" + realm);
            }

            byRealm[realm] = item.Open.Value;
        }

        var open = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            if (i <= launchIndex)
            {
                if (byRealm.ContainsKey(order[i]))
                {
                    throw new ConfigException(
                        "realms.json 的 " + order[i] + " 在首发范围内，按顺序开放，不该有开关");
                }

                open.Add(order[i]);
                continue;
            }

            if (!byRealm.TryGetValue(order[i], out var value))
            {
                throw new ConfigException("realms.json 缺 " + order[i] + " 的开关");
            }

            if (value)
            {
                open.Add(order[i]);
            }
        }

        // 十一段按顺序开放：前面那段还关着，后面不能先开。
        var closedSeen = false;
        for (var i = launchIndex + 1; i < order.Count; i++)
        {
            if (!byRealm[order[i]])
            {
                closedSeen = true;
            }
            else if (closedSeen)
            {
                throw new ConfigException(
                    "realms.json 的 " + order[i] + " 开着，但前面的境界还关着；十一段要按顺序开放");
            }
        }

        // P3-1a2：每一段（极境除外）一条突破要求。数值只在这份配置里；
        // 灵石必须是灵石五档、成功率 0–100、历练与主线场次非负。
        var requirements = new Dictionary<string, RealmAdvanceRequirement>(StringComparer.Ordinal);
        foreach (var item in wire.Advance ?? new List<AdvanceItem>())
        {
            var from = (item.From ?? "").Trim();
            if (from.Length == 0)
            {
                throw new ConfigException("realms.json 的 advance 里有条目缺 from");
            }

            if (!order.Contains(from))
            {
                throw new ConfigException("realms.json 的突破要求不在顺序表里：" + from);
            }

            if (from == order[^1])
            {
                throw new ConfigException("realms.json 的 " + from + " 是最后一段，没有下一段，不该配突破要求");
            }

            if (requirements.ContainsKey(from))
            {
                throw new ConfigException("realms.json 的突破要求重复：" + from);
            }

            if (item.Drill is null || item.Drill < 0)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破要求缺 drill 或 drill 是负数");
            }

            if (item.StoneCost is null || item.StoneCost <= 0)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破要求缺 stone_cost 或不是正数");
            }

            var stone = (item.Stone ?? "").Trim();
            if (stone.Length == 0)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破要求缺 stone");
            }

            if (!stone.StartsWith("stone_", StringComparison.Ordinal))
            {
                throw new ConfigException(
                    "realms.json 的 " + from + " 突破灵石必须是灵石五档（stone_1..stone_5）：" + stone);
            }

            if (currencies.Find(stone) is null)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破灵石不在币种表里：" + stone);
            }

            if (item.SuccessPct is null || item.SuccessPct < 0 || item.SuccessPct > 100)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破成功率 success_pct 必须在 0–100");
            }

            if (item.MainStage is null || item.MainStage < 0)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破的主线场次 main_stage 必须是非负数");
            }

            var sides = new List<string>();
            foreach (var raw in item.SideQuests ?? new List<string?>())
            {
                var quest = (raw ?? "").Trim();
                if (quest.Length == 0)
                {
                    throw new ConfigException("realms.json 的 " + from + " 必要支线里有空名字");
                }

                if (sides.Contains(quest))
                {
                    throw new ConfigException("realms.json 的 " + from + " 必要支线重复：" + quest);
                }

                sides.Add(quest);
            }

            // B1：境界丹。配了丹就必须是正整数枚数；没配丹就不许留一个枚数，
            // 「有数没丹」会让日志里看着像扣了丹而实际没扣。
            var pill = (item.Pill ?? "").Trim();
            var pillCount = item.PillCount ?? 0;
            if (pill.Length == 0 && pillCount != 0)
            {
                throw new ConfigException("realms.json 的 " + from
                    + " 配了 pill_count 却没有 pill");
            }

            if (pill.Length > 0 && pillCount < 1)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破丹枚数必须是正整数");
            }

            if (pill.StartsWith("stone_", StringComparison.Ordinal))
            {
                throw new ConfigException("realms.json 的 " + from + " 突破丹配成了灵石：" + pill);
            }

            var index = order.IndexOf(from);
            requirements[from] = new RealmAdvanceRequirement(
                from, order[index + 1], item.Drill.Value, stone, item.StoneCost.Value,
                item.SuccessPct.Value, item.MainStage.Value, sides, pill, pillCount);
        }

        for (var i = 0; i < order.Count - 1; i++)
        {
            if (!requirements.ContainsKey(order[i]))
            {
                throw new ConfigException("realms.json 缺 " + order[i] + " 的突破要求");
            }
        }

        // 副本表的境界门槛只许落在首发之后，名字必须在十一段里。
        foreach (var dungeon in dungeons.All)
        {
            var required = (dungeon.Realm ?? "").Trim();
            if (required.Length == 0)
            {
                continue;
            }

            var canonical = aliases.TryGetValue(required, out var target) ? target : required;
            var rank = order.IndexOf(canonical);
            if (rank < 0)
            {
                throw new ConfigException(
                    "副本 " + dungeon.Id + " 的境界门槛不在 realms 里：" + required);
            }

            if (rank <= launchIndex)
            {
                throw new ConfigException(
                    "副本 " + dungeon.Id + " 的境界门槛是首发的 " + order[rank]
                    + "；旧副本不提高境界门槛");
            }
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("realms.json 的 version 必须是正整数");
        }

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

        return new RealmTable(
            order, aliases, open, requirements, subPhasesByRealm, defaultSubPhases,
            wire.Version, launch);
    }

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

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("realms")]
        public List<string?>? Realms { get; init; }

        [JsonPropertyName("aliases")]
        public Dictionary<string, string?>? Aliases { get; init; }

        [JsonPropertyName("launch_until")]
        public string? LaunchUntil { get; init; }

        [JsonPropertyName("switches")]
        public List<SwitchItem>? Switches { get; init; }

        [JsonPropertyName("advance")]
        public List<AdvanceItem>? Advance { get; init; }

        [JsonPropertyName("sub_phases")]
        public SubPhasesItem? SubPhases { get; init; }
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

    sealed record AdvanceItem
    {
        [JsonPropertyName("from")]
        public string? From { get; init; }

        [JsonPropertyName("drill")]
        public long? Drill { get; init; }

        [JsonPropertyName("stone")]
        public string? Stone { get; init; }

        [JsonPropertyName("stone_cost")]
        public long? StoneCost { get; init; }

        [JsonPropertyName("success_pct")]
        public int? SuccessPct { get; init; }

        [JsonPropertyName("main_stage")]
        public int? MainStage { get; init; }

        [JsonPropertyName("side_quests")]
        public List<string?>? SideQuests { get; init; }

        // B1：境界丹与枚数。编号必须是 config/pill.json 的突破丹（由 PillTable 互校），
        // 这里只管形状与范围。
        [JsonPropertyName("pill")]
        public string? Pill { get; init; }

        [JsonPropertyName("pill_count")]
        public long? PillCount { get; init; }
    }

    sealed record SwitchItem
    {
        [JsonPropertyName("realm")]
        public string? Realm { get; init; }

        [JsonPropertyName("open")]
        public bool? Open { get; init; }
    }
}
