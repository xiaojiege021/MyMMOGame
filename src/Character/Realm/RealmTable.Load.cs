using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

public sealed partial class RealmTable
{

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
        // P3-1a2：每一段（极境除外）一条突破要求，数值只在配置里。
        var requirements = Build(wire, order, currencies);
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

        var (subPhasesByRealm, defaultSubPhases) = Build(wire, order);

        return new RealmTable(
            order, aliases, open, requirements, subPhasesByRealm, defaultSubPhases,
            wire.Version, launch);
    }
}


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
