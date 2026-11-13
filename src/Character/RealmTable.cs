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
/// 十一段境界的顺序与版本开关，读 config/realms.json。首发这一段（含）以前一律开放，
/// 之后的每段各有一个开关；开关按顺序打开，后面的段不能在前面还关着时先开。
/// 境界本身仍只由角色服写，这里只回答「这一段开没开」「这个人能不能进这个副本」。
/// </summary>
public sealed class RealmTable
{
    readonly List<string> _order;
    readonly Dictionary<string, string> _aliases;
    readonly HashSet<string> _open;

    public int Version { get; }

    public string LaunchUntil { get; }

    public IReadOnlyList<string> Realms => _order;

    public IReadOnlyDictionary<string, string> Aliases => _aliases;

    RealmTable(
        List<string> order,
        Dictionary<string, string> aliases,
        HashSet<string> open,
        int version,
        string launchUntil)
    {
        _order = order;
        _aliases = aliases;
        _open = open;
        Version = version;
        LaunchUntil = launchUntil;
    }

    /// <summary>把「散仙」这类别名归到顺序表里的名字；不认识的境界原样返回。</summary>
    public string Normalize(string? realm)
    {
        var value = (realm ?? "").Trim();
        return _aliases.TryGetValue(value, out var canonical) ? canonical : value;
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
    public static RealmTable Load(string path, DungeonTable dungeons)
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

        return new RealmTable(order, aliases, open, wire.Version, launch);
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
    }

    sealed record SwitchItem
    {
        [JsonPropertyName("realm")]
        public string? Realm { get; init; }

        [JsonPropertyName("open")]
        public bool? Open { get; init; }
    }
}
