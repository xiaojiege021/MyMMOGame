using System.Text.Json;
using System.Text.Json.Serialization;

namespace World;

/// <summary>
/// 飞行与跨区门槛的唯一配置正本（config/flight.json）。
/// 方案 02 与 8.1 写定「境界决定能去地表哪些地方、飞行只由境界打开」，
/// 并写定首发三段大境界依次是炼体、筑基、结丹；但没写定哪一段能飞、
/// 每块地表要哪一段境界。这些数值只在这里记一次，注明待定，代码里不给兜底。
/// 地面高度与移动速度上限沿用 config/world.json 的 spawn_y / max_move_speed，
/// 不在这份配置里再写第二套数字。
/// 突破、境界版本开关与元婴之后的新区域都在后面的卡，本表只读不改境界。
/// </summary>
public sealed class FlightTable
{
    // 方案 8.1 写定的首发三段，顺序不能改，也不能删掉其中一段来绕过门槛。
    public static readonly string[] WrittenRealms = ["炼体", "筑基", "结丹"];

    readonly Dictionary<string, string> regionRealms;
    readonly Dictionary<string, int> realmOrder;

    FlightTable(
        string characterUpstream,
        IReadOnlyList<string> realms,
        string flightRealm,
        Dictionary<string, string> regionRealms)
    {
        CharacterUpstream = characterUpstream;
        Realms = realms;
        FlightRealm = flightRealm;
        this.regionRealms = regionRealms;
        realmOrder = realms.Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
    }

    public string CharacterUpstream { get; }

    public IReadOnlyList<string> Realms { get; }

    public string FlightRealm { get; }

    /// <summary>境界在方案顺序里的位置。不认识的境界返回 -1，一律按没有权限处理。</summary>
    public int IndexOf(string realm) =>
        realmOrder.TryGetValue((realm ?? "").Trim(), out var index) ? index : -1;

    public string? RequiredFor(string regionId) =>
        regionRealms.TryGetValue((regionId ?? "").Trim(), out var realm) ? realm : null;

    /// <summary>只读门槛表，供启动日志与 --flight-state 逐块打出来。</summary>
    public IReadOnlyDictionary<string, string> Gates => regionRealms;

    /// <summary>regions 给定时顺带核对门槛表覆盖所有已配置地表（启动用）；只读入口可不传。</summary>
    public static FlightTable Load(string path, IReadOnlySet<string>? regions = null)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到飞行配置 " + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("飞行配置是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("飞行配置不是合法 JSON：" + ex.Message);
        }

        var version = wire.Version ?? throw new ConfigException("flight.json 缺 version");
        if (version < 1)
        {
            throw new ConfigException("flight.json version 必须大于 0，现在是 " + version);
        }

        var upstream = (wire.CharacterUpstream ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(upstream, UriKind.Absolute, out var upstreamUri)
            || (upstreamUri.Scheme != Uri.UriSchemeHttp && upstreamUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ConfigException("flight.json 的 character_upstream 必须是 http(s) 地址：" + upstream);
        }

        if (wire.Realms is null || wire.Realms.Count == 0)
        {
            throw new ConfigException("flight.json 缺 realms");
        }

        var seenRealms = new HashSet<string>(StringComparer.Ordinal);
        var realms = new List<string>(wire.Realms.Count);
        foreach (var raw in wire.Realms)
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0)
            {
                throw new ConfigException("flight.json 的 realms 里有空名字");
            }

            if (!seenRealms.Add(name))
            {
                throw new ConfigException("flight.json 的 realms 里有重复名字 " + name);
            }

            realms.Add(name);
        }

        // 方案写定的首发三段必须在，且顺序不能动。后面版本可以在末尾追加新大境界。
        for (var i = 0; i < WrittenRealms.Length; i++)
        {
            if (i >= realms.Count || !string.Equals(realms[i], WrittenRealms[i], StringComparison.Ordinal))
            {
                throw new ConfigException("flight.json 的 realms 必须按方案 8.1 以 "
                    + string.Join(",", WrittenRealms) + " 开头，现在是 " + string.Join(",", realms));
            }
        }

        var flightRealm = (wire.FlightRealm ?? "").Trim();
        if (!seenRealms.Contains(flightRealm))
        {
            throw new ConfigException("flight.json 的 flight_realm 不在 realms 里：" + flightRealm);
        }

        if (wire.RegionRealms is null || wire.RegionRealms.Count == 0)
        {
            throw new ConfigException("flight.json 缺 region_realms");
        }

        var regionRealms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in wire.RegionRealms)
        {
            var id = (raw.RegionId ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("flight.json 的 region_realms 里有空地点编号");
            }

            if (!regionRealms.TryAdd(id, (raw.Realm ?? "").Trim()))
            {
                throw new ConfigException("flight.json 的 region_realms 里有重复地点 " + id);
            }

            if (regions is not null && !regions.Contains(id))
            {
                throw new ConfigException("flight.json 的地点不在 world.json 里：" + id);
            }

            if (!seenRealms.Contains(regionRealms[id]))
            {
                throw new ConfigException("flight.json 的地点 " + id
                    + " 门槛境界不在 realms 里：" + regionRealms[id]);
            }
        }

        // 漏配一块地表就等于给它开零门槛，不许带着半截表跑。
        if (regions is not null)
        {
            foreach (var id in regions)
            {
                if (!regionRealms.ContainsKey(id))
                {
                    throw new ConfigException("flight.json 缺地点 " + id + " 的境界门槛");
                }
            }
        }

        return new FlightTable(upstream, realms, flightRealm, regionRealms);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    sealed record Wire
    {
        [JsonPropertyName("version")] public int? Version { get; init; }
        [JsonPropertyName("character_upstream")] public string? CharacterUpstream { get; init; }
        [JsonPropertyName("realms")] public List<string?>? Realms { get; init; }
        [JsonPropertyName("flight_realm")] public string? FlightRealm { get; init; }
        [JsonPropertyName("region_realms")] public List<RegionWire>? RegionRealms { get; init; }
    }

    sealed record RegionWire
    {
        [JsonPropertyName("region_id")] public string? RegionId { get; init; }
        [JsonPropertyName("realm")] public string? Realm { get; init; }
    }
}
