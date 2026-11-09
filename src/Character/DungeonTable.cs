using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>一个副本：编号、名字、人数上限、进本时扣不扣疲劳与周份额。</summary>
public sealed record DungeonDef(string Id, string Name, int MaxPlayers, bool ChargesFatigue, bool TakesWeeklyShare);

/// <summary>
/// 副本表。与战斗服读同一份 config/dungeons.json：战斗服按它开本，
/// 角色服按它校验人数与扣减，不各存一套。
/// </summary>
public sealed class DungeonTable
{
    readonly Dictionary<string, DungeonDef> _byId = new(StringComparer.Ordinal);

    public int Version { get; }

    public IReadOnlyCollection<DungeonDef> All => _byId.Values;

    public DungeonDef? Find(string id) => _byId.GetValueOrDefault(id);

    DungeonTable(Dictionary<string, DungeonDef> byId, int version)
    {
        _byId = byId;
        Version = version;
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

            map[id] = new DungeonDef(
                id,
                (item.Name ?? id).Trim(),
                item.MaxPlayers.Value,
                item.ChargesFatigue ?? false,
                item.TakesWeeklyShare ?? false);
        }

        if (map.Count == 0)
        {
            throw new ConfigException("dungeons.json 里一个副本都没有");
        }

        return new DungeonTable(map, wire.Version);
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
    }
}
