using System.Text.Json;
using System.Text.Json.Serialization;

namespace Global;

/// <summary>
/// 全局服务挂单时用的轻量档位表：只读 config/stone_exchange.json 的 tiers，
/// 判断 stone_kind 是不是灵石档位之一。比例、产出、最高档中文名都不在这里读，
/// 也不写死任何一个币种 id 或 10000——数值留在配置，档位校验只看这一组 id。
/// 方案第 12 章：仙晶是最高档灵石，不是充值货币；元宝不在这组档位里。
/// </summary>
public sealed class StoneTierTable
{
    public const string WrittenSchema = "stone-exchange-table/1";

    // 充值货币不在灵石档位里（方案第 12 章「仙晶是最高档灵石，不是充值货币」）。
    // 只作硬校验的禁入项，不是档位集合里的成员。
    const string RechargeCurrencyId = "yuanbao";

    public int Version { get; }
    public IReadOnlyList<string> Tiers { get; }

    readonly HashSet<string> _tiers;

    StoneTierTable(int version, List<string> tiers)
    {
        Version = version;
        Tiers = tiers;
        _tiers = new HashSet<string>(tiers, StringComparer.Ordinal);
    }

    public int Count => Tiers.Count;

    public bool Contains(string kind) => _tiers.Contains(kind);

    public static StoneTierTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到灵石档位表：" + path);
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("灵石档位表不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            // 缺字段就起不来，不带着半截配置跑。
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
                    ?? throw new ConfigException("灵石档位表是空的：" + path);
            }
            catch (JsonException ex)
            {
                throw new ConfigException("灵石档位表字段类型不对：" + ex.Message);
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

            var ingot = (wire.IngotKind ?? "").Trim();
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

                // 充值货币不是灵石档位：写定的元宝 id 与配置里的 ingot_kind 都挡，
                // 坏配置改了 ingot_kind 也拦得住。
                if (string.Equals(tier, RechargeCurrencyId, StringComparison.Ordinal)
                    || (ingot.Length > 0 && string.Equals(tier, ingot, StringComparison.Ordinal)))
                {
                    throw new ConfigException("stone_exchange.json 的 tiers 不能含充值货币：" + tier);
                }

                tiers.Add(tier);
            }

            return new StoneTierTable(version, tiers);
        }
    }

    static readonly string[] RequiredKeys = ["schema", "version", "tiers"];

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

        [JsonPropertyName("tiers")]
        public List<string?>? Tiers { get; init; }

        [JsonPropertyName("ingot_kind")]
        public string? IngotKind { get; init; }
    }
}
