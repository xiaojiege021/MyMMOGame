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
public sealed partial class RealmTable
{
    readonly List<string> _order;
    readonly Dictionary<string, string> _aliases;
    readonly HashSet<string> _open;
    readonly Dictionary<string, RealmAdvanceRequirement> _requirements;
    readonly Dictionary<string, RealmSubPhases> _subPhasesByRealm;
    readonly RealmSubPhases _defaultSubPhases;

    public int Version { get; }

    public string LaunchUntil { get; }

    public IReadOnlyList<string> Realms => _order;

    public IReadOnlyDictionary<string, string> Aliases => _aliases;

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

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
