using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>一种出身。只有名字、来源、外观与对白键——没有属性字段，也不许加。</summary>
public sealed record RaceDef(string Id, string Name, string Origin, string DialogueKey);

public sealed record RaceTable(IReadOnlyList<RaceDef> All, int Version)
{
    public RaceDef? Find(string id) => All.FirstOrDefault(r => r.Id == id);

    public static RaceTable Load(string path)
    {
        var wire = Read<RaceWire>(path, "races");
        var races = new List<RaceDef>();
        foreach (var item in wire.Races)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name))
            {
                throw new ConfigException("races.json 里有条目缺 id 或 name");
            }

            races.Add(new RaceDef(item.Id.Trim(), item.Name.Trim(), item.Origin ?? "", item.DialogueKey ?? ""));
        }

        if (races.Count == 0)
        {
            throw new ConfigException("races.json 里一种出身都没有");
        }

        return new RaceTable(races, wire.Version);
    }

    sealed record RaceWire
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("races")]
        public List<RaceItem> Races { get; init; } = [];
    }

    sealed record RaceItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("origin")]
        public string? Origin { get; init; }

        [JsonPropertyName("dialogue_key")]
        public string? DialogueKey { get; init; }
    }

    internal static T Read<T>(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到 " + what + " 表：" + path);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException(what + " 表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException(what + " 表不是合法 JSON：" + ex.Message);
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
