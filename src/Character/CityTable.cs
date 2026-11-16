using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>一座城池：灵石矿脉归属记在城上，不做成玩家个人采集点。</summary>
public sealed record CityDef(
    string CityId, string Name, string VeinId, string? OwnerSect, int TaxPct);

/// <summary>
/// 城池表的唯一正本（方案 9.5、12 章与开发架构 4.2 / 6 节）。
/// vein_id 必须对应 gather.json 里 kind == spirit_stone 的节点；gather.json 用
/// System.Text.Json 直接读，不引用 World 工程。
/// 城池税收账本（社交.city_tax_ledger）与拍卖销毁税（交易.auction_settlement.tax_gold）
/// 是分开的两张表，本卡只登记归属、建账本，不写回、不改税率。
/// </summary>
public sealed class CityTable
{
    public const string WrittenSchema = "city-table/1";

    // 「个人采集点或商品」字样：城池只登记归属，出现任何一个都直接起不来。
    static readonly string[] ForbiddenKeys =
        ["item_id", "count", "bag_slot", "drop", "price", "slot"];

    static readonly string[] RequiredKeys =
        ["schema", "version", "note", "gather_table", "task_grant_city_id", "cities"];

    public int Version { get; }
    public string GatherTable { get; }
    public string TaskGrantCityId { get; }
    public IReadOnlyList<CityDef> Cities { get; }

    CityTable(int version, string gatherTable, string taskGrantCityId, List<CityDef> cities)
    {
        Version = version;
        GatherTable = gatherTable;
        TaskGrantCityId = taskGrantCityId;
        Cities = cities;
    }

    public static CityTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到城池表：" + path);
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("城池表不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            foreach (var key in RequiredKeys)
            {
                if (!root.TryGetProperty(key, out _))
                {
                    throw new ConfigException("city.json 缺字段 " + key);
                }
            }

            // 用 JsonDocument 逐条扫 cities 的键：矿脉归属不能夹带个人采集点或商品字段。
            if (!root.TryGetProperty("cities", out var cityArray)
                || cityArray.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("city.json 的 cities 必须是数组");
            }

            foreach (var cityElement in cityArray.EnumerateArray())
            {
                if (cityElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ConfigException("city.json 的 cities 每条必须是对象");
                }

                foreach (var property in cityElement.EnumerateObject())
                {
                    foreach (var forbidden in ForbiddenKeys)
                    {
                        if (property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ConfigException(
                                "city.json 的 cities 不许出现个人采集点或商品字段：" + property.Name);
                        }
                    }
                }
            }

            Wire wire;
            try
            {
                wire = JsonSerializer.Deserialize<Wire>(text, JsonOptions)
                    ?? throw new ConfigException("城池表是空的：" + path);
            }
            catch (JsonException ex)
            {
                throw new ConfigException("城池表字段类型不对：" + ex.Message);
            }

            var schema = (wire.Schema ?? "").Trim();
            if (!string.Equals(schema, WrittenSchema, StringComparison.Ordinal))
            {
                throw new ConfigException("city.json 的 schema 必须是 " + WrittenSchema);
            }

            var version = wire.Version ?? 0;
            if (version <= 0)
            {
                throw new ConfigException("city.json 的 version 必须大于 0");
            }

            if (wire.Cities is null || wire.Cities.Count == 0)
            {
                throw new ConfigException("city.json 的 cities 至少要有一条");
            }

            var gatherTable = (wire.GatherTable ?? "").Trim();
            if (gatherTable.Length == 0)
            {
                throw new ConfigException("city.json 的 gather_table 不能为空");
            }

            var taskGrantCityId = (wire.TaskGrantCityId ?? "").Trim();
            if (taskGrantCityId.Length == 0)
            {
                throw new ConfigException("city.json 的 task_grant_city_id 不能为空");
            }

            var spiritVeins = ReadSpiritVeins(gatherTable);
            if (spiritVeins.Count == 0)
            {
                throw new ConfigException("gather.json 里没有 spirit_stone 节点：" + gatherTable);
            }

            var cities = new List<CityDef>(wire.Cities.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var city in wire.Cities)
            {
                var cityId = (city.CityId ?? "").Trim();
                if (cityId.Length == 0)
                {
                    throw new ConfigException("city.json 里有条目缺 city_id");
                }

                if (!seen.Add(cityId))
                {
                    throw new ConfigException("city.json 的 city_id 重复：" + cityId);
                }

                var name = (city.Name ?? "").Trim();
                if (name.Length == 0)
                {
                    throw new ConfigException("city.json 的城池缺 name：" + cityId);
                }

                var veinId = (city.VeinId ?? "").Trim();
                if (veinId.Length == 0)
                {
                    throw new ConfigException("city.json 的城池缺 vein_id：" + cityId);
                }

                if (!spiritVeins.Contains(veinId))
                {
                    throw new ConfigException("city.json 的 vein_id 不是 gather.json 里的 "
                        + "spirit_stone 节点：" + veinId);
                }

                var taxPct = city.TaxPct ?? -1;
                if (taxPct < 0 || taxPct > 100)
                {
                    throw new ConfigException("city.json 的 tax_pct 必须在 0 到 100 之间：" + cityId);
                }

                var ownerSect = string.IsNullOrWhiteSpace(city.OwnerSect)
                    ? null
                    : city.OwnerSect.Trim();

                cities.Add(new CityDef(cityId, name, veinId, ownerSect, taxPct));
            }

            if (!seen.Contains(taskGrantCityId))
            {
                throw new ConfigException(
                    "city.json 的 task_grant_city_id 必须对应 cities 里的一条：" + taskGrantCityId);
            }

            return new CityTable(version, gatherTable, taskGrantCityId, cities);
        }
    }

    /// <summary>直接读 gather.json 的 nodes，挑出 kind == spirit_stone 的节点编号。</summary>
    static HashSet<string> ReadSpiritVeins(string gatherTable)
    {
        if (!File.Exists(gatherTable))
        {
            throw new ConfigException("找不到 gather 表：" + gatherTable);
        }

        var text = File.ReadAllText(gatherTable);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("gather 表不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var veins = new HashSet<string>(StringComparer.Ordinal);
            if (!document.RootElement.TryGetProperty("nodes", out var nodes)
                || nodes.ValueKind != JsonValueKind.Array)
            {
                return veins;
            }

            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var kind = node.TryGetProperty("kind", out var kindElement)
                    && kindElement.ValueKind == JsonValueKind.String
                        ? kindElement.GetString() ?? ""
                        : "";
                if (!string.Equals(kind, "spirit_stone", StringComparison.Ordinal))
                {
                    continue;
                }

                // 灵石矿脉节点只登记归属，不许配成个人采集点或商品。
                foreach (var property in node.EnumerateObject())
                {
                    foreach (var forbidden in ForbiddenKeys)
                    {
                        if (property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ConfigException(
                                "gather.json 的 spirit_stone 节点不许出现个人采集点或商品字段："
                                + property.Name);
                        }
                    }
                }

                var id = node.TryGetProperty("id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? ""
                        : "";
                if (id.Length > 0)
                {
                    veins.Add(id);
                }
            }

            return veins;
        }
    }

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

        [JsonPropertyName("gather_table")]
        public string? GatherTable { get; init; }

        [JsonPropertyName("task_grant_city_id")]
        public string? TaskGrantCityId { get; init; }

        [JsonPropertyName("cities")]
        public List<CityWire>? Cities { get; init; }
    }

    sealed record CityWire
    {
        [JsonPropertyName("city_id")]
        public string? CityId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("vein_id")]
        public string? VeinId { get; init; }

        [JsonPropertyName("owner_sect")]
        public string? OwnerSect { get; init; }

        [JsonPropertyName("tax_pct")]
        public int? TaxPct { get; init; }
    }
}
