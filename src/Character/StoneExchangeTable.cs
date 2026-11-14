using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 灵石五档兑换表的唯一正本（方案第 12 章「成长资源」、8.1；开发架构 P3-2a）。
/// 相邻档的比例与每次产出都从 config/stone_exchange.json 读，代码里不写死数值；
/// 最高一档的中文名也不写死，而是与币种表里的 Name 对齐。
/// 兑换只在角色服做，不经过拍卖（开发架构 4.1）。
/// </summary>
public sealed class StoneExchangeTable
{
    public const string WrittenSchema = "stone-exchange-table/1";

    public int Version { get; }
    public string Note { get; }
    public IReadOnlyList<string> Tiers { get; }
    public int Ratio { get; }
    public int Gain { get; }
    public string HighestKind { get; }
    public string HighestName { get; }
    public string IngotKind { get; }

    readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    StoneExchangeTable(int version, string note, List<string> tiers, int ratio, int gain,
        string highestKind, string highestName, string ingotKind)
    {
        Version = version;
        Note = note;
        Tiers = tiers;
        Ratio = ratio;
        Gain = gain;
        HighestKind = highestKind;
        HighestName = highestName;
        IngotKind = ingotKind;
        for (var i = 0; i < tiers.Count; i++)
        {
            _index[tiers[i]] = i;
        }
    }

    /// <summary>档位下标；不在表里返回 -1。用它把 unknown_tier 与 top_tier 分开。</summary>
    public int IndexOf(string kind) => _index.GetValueOrDefault(kind, -1);

    /// <summary>相邻高一档。不在表里、已经是最后一档，都返回 null。</summary>
    public string? Next(string from)
    {
        var i = IndexOf(from);
        return i < 0 || i + 1 >= Tiers.Count ? null : Tiers[i + 1];
    }

    public static StoneExchangeTable Load(string path, CurrencyTable currencies)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到灵石兑换表：" + path);
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("灵石兑换表不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            // 缺字段就起不来，不带着半截配置跑；字段的具体类型在下面逐项校验。
            foreach (var key in RequiredKeys)
            {
                if (!root.TryGetProperty(key, out _))
                {
                    throw new ConfigException("stone_exchange.json 缺字段 " + key);
                }
            }

            Wire wire;
            try
            {
                wire = JsonSerializer.Deserialize<Wire>(text, JsonOptions)
                    ?? throw new ConfigException("灵石兑换表是空的：" + path);
            }
            catch (JsonException ex)
            {
                throw new ConfigException("灵石兑换表字段类型不对：" + ex.Message);
            }

            var schema = (wire.Schema ?? "").Trim();
            if (!string.Equals(schema, WrittenSchema, StringComparison.Ordinal))
            {
                throw new ConfigException("stone_exchange.json 的 schema 必须是 " + WrittenSchema);
            }

            var version = wire.Version ?? 0;
            if (version <= 0)
            {
                throw new ConfigException("stone_exchange.json 的 version 必须大于 0");
            }

            if (wire.Tiers is null || wire.Tiers.Count == 0)
            {
                throw new ConfigException("stone_exchange.json 的 tiers 不能为空");
            }

            var tiers = new List<string>(wire.Tiers.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in wire.Tiers)
            {
                var tier = (raw ?? "").Trim();
                if (tier.Length == 0)
                {
                    throw new ConfigException("stone_exchange.json 的 tiers 里有空档位");
                }

                if (!seen.Add(tier))
                {
                    throw new ConfigException("stone_exchange.json 的 tiers 有重复档位：" + tier);
                }

                tiers.Add(tier);
            }

            var ingotKind = (wire.IngotKind ?? "").Trim();
            if (ingotKind.Length == 0)
            {
                throw new ConfigException("stone_exchange.json 的 ingot_kind 不能为空");
            }

            foreach (var tier in tiers)
            {
                if (currencies.Find(tier) is null)
                {
                    throw new ConfigException("stone_exchange.json 的档位不在 currency.json 里：" + tier);
                }

                // 最高一档是灵石、充值货币是另一类：两者不能混进同一组档位。
                if (string.Equals(tier, ingotKind, StringComparison.Ordinal))
                {
                    throw new ConfigException("stone_exchange.json 的 tiers 不能含 ingot_kind：" + tier);
                }
            }

            var ratio = wire.Ratio ?? 0;
            if (ratio <= 1)
            {
                throw new ConfigException("stone_exchange.json 的 ratio 必须大于 1");
            }

            var gain = wire.Gain ?? 0;
            if (gain <= 0)
            {
                throw new ConfigException("stone_exchange.json 的 gain 必须大于 0");
            }

            var highestKind = (wire.HighestKind ?? "").Trim();
            if (highestKind.Length == 0
                || !string.Equals(highestKind, tiers[^1], StringComparison.Ordinal))
            {
                throw new ConfigException("stone_exchange.json 的 highest_kind 必须等于 tiers 最后一档");
            }

            var highestName = (wire.HighestName ?? "").Trim();
            if (highestName.Length == 0)
            {
                throw new ConfigException("stone_exchange.json 的 highest_name 不能为空");
            }

            // 中文名只认币种表：保证「最高一档」与 currency.json 里写的是同一个名字，
            // 代码里不写死任何中文名。
            var highest = currencies.Find(highestKind);
            if (highest is null
                || !string.Equals(highestName, highest.Name, StringComparison.Ordinal))
            {
                throw new ConfigException("stone_exchange.json 的 highest_name 必须等于 currency.json 里 "
                    + highestKind + " 的 name");
            }

            if (currencies.Find(ingotKind) is null)
            {
                throw new ConfigException("stone_exchange.json 的 ingot_kind 不在 currency.json 里：" + ingotKind);
            }

            if (string.Equals(ingotKind, highestKind, StringComparison.Ordinal))
            {
                throw new ConfigException("stone_exchange.json 的 ingot_kind 不能等于 highest_kind");
            }

            return new StoneExchangeTable(version, (wire.Note ?? "").Trim(), tiers,
                ratio, gain, highestKind, highestName, ingotKind);
        }
    }

    static readonly string[] RequiredKeys =
    [
        "schema", "version", "note", "tiers", "ratio", "gain",
        "highest_kind", "highest_name", "ingot_kind",
    ];

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed record Wire
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        [JsonPropertyName("version")]
        public int? Version { get; init; }

        [JsonPropertyName("note")]
        public string? Note { get; init; }

        [JsonPropertyName("tiers")]
        public List<string?>? Tiers { get; init; }

        [JsonPropertyName("ratio")]
        public int? Ratio { get; init; }

        [JsonPropertyName("gain")]
        public int? Gain { get; init; }

        [JsonPropertyName("highest_kind")]
        public string? HighestKind { get; init; }

        [JsonPropertyName("highest_name")]
        public string? HighestName { get; init; }

        [JsonPropertyName("ingot_kind")]
        public string? IngotKind { get; init; }
    }
}
