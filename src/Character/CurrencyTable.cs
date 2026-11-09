using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>一个币种：编号、中文名、流通说明。这一行只关心编号与名字。</summary>
public sealed record CurrencyDef(string Id, string Name);

public sealed class CurrencyTable
{
    readonly Dictionary<string, CurrencyDef> _byId = new(StringComparer.Ordinal);

    public int Version { get; }

    public IReadOnlyList<CurrencyDef> All { get; }

    public CurrencyDef? Find(string id) => _byId.GetValueOrDefault(id);

    CurrencyTable(List<CurrencyDef> all, int version)
    {
        All = all;
        Version = version;
        foreach (var item in all)
        {
            _byId[item.Id] = item;
        }
    }

    public static CurrencyTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到币种表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("币种表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("币种表不是合法 JSON：" + ex.Message);
        }

        var list = new List<CurrencyDef>();
        foreach (var item in wire.Currencies)
        {
            var id = (item.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("currency.json 里有条目缺 id");
            }

            list.Add(new CurrencyDef(id, (item.Name ?? id).Trim()));
        }

        if (list.Count == 0)
        {
            throw new ConfigException("currency.json 里一个币种都没有");
        }

        return new CurrencyTable(list, wire.Version);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    sealed record Wire
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("currencies")]
        public List<CurrencyItem> Currencies { get; init; } = [];
    }

    sealed record CurrencyItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }
}
