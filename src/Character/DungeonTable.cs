using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 一个副本：编号、名字、人数上限、进本时扣不扣疲劳与周份额。
/// FatiguePerPlayer 是单人进一次的基础消耗，扣的时候再乘进本人数；
/// 不扣疲劳的本（主线）它是 0。
/// </summary>
public sealed record DungeonDef(
    string Id,
    string Name,
    int MaxPlayers,
    bool ChargesFatigue,
    bool TakesWeeklyShare,
    int FatiguePerPlayer);

/// <summary>
/// 副本表。与战斗服读同一份 config/dungeons.json：战斗服按它开本，
/// 角色服按它校验人数与扣减，不各存一套。
/// </summary>
public sealed class DungeonTable
{
    readonly Dictionary<string, DungeonDef> _byId = new(StringComparer.Ordinal);

    public int Version { get; }

    /// <summary>一天能累计到的疲劳上限（方案写定 6000）。</summary>
    public int FatigueDailyCap { get; }

    public IReadOnlyCollection<DungeonDef> All => _byId.Values;

    public DungeonDef? Find(string id) => _byId.GetValueOrDefault(id);

    DungeonTable(Dictionary<string, DungeonDef> byId, int version, int fatigueDailyCap)
    {
        _byId = byId;
        Version = version;
        FatigueDailyCap = fatigueDailyCap;
    }

    public static DungeonTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到副本表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("副本表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("副本表不是合法 JSON：" + ex.Message);
        }

        var cap = wire.FatigueDailyCap ?? 6000;
        if (cap < 1 || cap > 1_000_000)
        {
            throw new ConfigException("fatigue_daily_cap 必须在 1 到 1000000 之间，现在是 " + cap);
        }

        var map = new Dictionary<string, DungeonDef>(StringComparer.Ordinal);
        foreach (var item in wire.Dungeons)
        {
            var id = (item.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("dungeons.json 里有条目缺 id");
            }

            if (item.MaxPlayers is null or < 1 or > 100)
            {
                throw new ConfigException("副本 " + id + " 的人数上限要在 1 到 100 之间");
            }

            var charges = item.ChargesFatigue ?? false;
            var perPlayer = item.FatiguePerPlayer ?? 0;
            if (charges && perPlayer < 1)
            {
                throw new ConfigException("副本 " + id + " 扣疲劳，fatigue_per_player 必须是正数");
            }

            if ((long)perPlayer * item.MaxPlayers.Value > cap)
            {
                throw new ConfigException("副本 " + id + " 满员一次的疲劳就超过日上限，永远进不去");
            }

            map[id] = new DungeonDef(
                id,
                (item.Name ?? id).Trim(),
                item.MaxPlayers.Value,
                charges,
                item.TakesWeeklyShare ?? false,
                charges ? perPlayer : 0);
        }

        if (map.Count == 0)
        {
            throw new ConfigException("dungeons.json 里一个副本都没有");
        }

        return new DungeonTable(map, wire.Version, cap);
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

        [JsonPropertyName("fatigue_daily_cap")]
        public int? FatigueDailyCap { get; init; }

        [JsonPropertyName("dungeons")]
        public List<DungeonItem> Dungeons { get; init; } = [];
    }

    sealed record DungeonItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("max_players")]
        public int? MaxPlayers { get; init; }

        [JsonPropertyName("charges_fatigue")]
        public bool? ChargesFatigue { get; init; }

        [JsonPropertyName("takes_weekly_share")]
        public bool? TakesWeeklyShare { get; init; }

        [JsonPropertyName("fatigue_per_player")]
        public int? FatiguePerPlayer { get; init; }
    }
}
