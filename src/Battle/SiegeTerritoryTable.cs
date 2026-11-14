using System.Text.Json;

namespace Battle;

/// <summary>
/// P3-3a4：城战胜负写回的领地表（config/siege_territory.json 是唯一正本）。
///
/// 方案 11 章把「阵营 · 领地」写成全服的政治单元：地块税收、灵石矿脉；9.5 / 12 章
/// 写定矿脉归主城辖区、掌管方按城池税收抽成、比例走配置。开发架构第 66 行要的是
/// 「胜负写回城池税收和矿脉归属」，所以这张表只放三样东西：这一场打哪座城、
/// 开场多久算结束、易主之后抽成比例是多少。胜方、宗门编号、事务号都不在配置里，
/// 也不从客户端请求里来，全部由战斗服与社交服给事实值。
///
/// 方案没给城战时长与抽成比例，两个数字都是正整数占位并标 pending=true；
/// 代码、proto、SQL 都不写第二套数字。配置里也不许预写 owner_sect 或商品 / 掉落 /
/// 平衡系数：归属只能由城战结果写回，不从配置里长出来。
///
/// 这是一张可选表：老卡临时配置目录没有它时走 Missing()，城战照常开场、不结束、
/// 不写回，副本 / 练习场 / 竞技场 / 战场 / 老城战断言一条都不受影响。
/// </summary>
public sealed class SiegeTerritoryTable
{
    public sealed record CityRule(
        string CityId, string MatchPrefix, int BattleTimeoutMs, int TaxPctOnCapture);

    SiegeTerritoryTable(
        int version, bool pending, string cityTable, List<CityRule> cities, bool missing)
    {
        Version = version;
        Pending = pending;
        CityTable = cityTable;
        Cities = cities;
        IsMissing = missing;
    }

    public int Version { get; }
    public bool Pending { get; }
    public string CityTable { get; }
    public IReadOnlyList<CityRule> Cities { get; }

    /// <summary>没配领地表：城战照常开场，不结束、不写回，老链路不回退。</summary>
    public bool IsMissing { get; }

    public static SiegeTerritoryTable Missing() =>
        new(0, true, "", [], missing: true);

    /// <summary>
    /// 一场城战打哪座城，由服务端的 match 前缀决定，客户端只给 match 编号，
    /// 表达不了「我要打哪座城 / 归属改成谁」。匹配不上就不写回。
    /// </summary>
    public CityRule? CityOf(string matchId)
    {
        if (IsMissing)
        {
            return null;
        }

        foreach (var city in Cities)
        {
            if (string.Equals(matchId, city.MatchPrefix, StringComparison.Ordinal)
                || matchId.StartsWith(city.MatchPrefix + "-", StringComparison.Ordinal))
            {
                return city;
            }
        }

        return null;
    }

    public static SiegeTerritoryTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"领地表不存在：{path}");
        }

        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(File.ReadAllText(path), options);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var key = property.Name.ToLowerInvariant();
            if (key is "balance" or "ratio" or "templates" or "arena_match"
                or "shop" or "mall" or "price" or "ingot" or "delivered_kinds"
                or "reward" or "probability" or "drop" or "owner_sect" or "winner")
            {
                throw new ConfigException(
                    $"{path}：领地表不许出现 {property.Name}（不做平衡 / 不发奖 / 不预定归属）");
            }

            if (key == "cities" && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var city in property.Value.EnumerateArray())
                {
                    if (city.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (var nested in city.EnumerateObject())
                    {
                        var nestedKey = nested.Name.ToLowerInvariant();
                        if (nestedKey is "item_id" or "price" or "slot" or "drop"
                            or "model" or "prefab" or "appearance" or "damage"
                            or "power" or "owner_sect" or "winner_sect")
                        {
                            throw new ConfigException(
                                $"{path}：cities 条目不许出现 {nested.Name}"
                                + "（归属由城战结果写回，不从配置里预写）");
                        }
                    }
                }
            }
        }

        var wire = ConfigJson.Read<Wire>(path);
        if (wire.Schema != "siege-territory/1")
        {
            throw new ConfigException(
                $"{path}：schema 必须是 siege-territory/1，现在是 {wire.Schema}");
        }
        if (wire.Version <= 0)
        {
            throw new ConfigException($"{path}：version 必须是正整数");
        }

        var cityTable = (wire.CityTable ?? "").Trim();
        if (cityTable.Length == 0)
        {
            throw new ConfigException($"{path}：city_table 不能为空");
        }
        var knownCities = ReadCityIds(cityTable, path);

        if (wire.Cities is null || wire.Cities.Count == 0)
        {
            throw new ConfigException($"{path}：cities 至少要有一条");
        }

        var cities = new List<CityRule>(wire.Cities.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in wire.Cities)
        {
            var cityId = (rule.CityId ?? "").Trim();
            if (cityId.Length == 0)
            {
                throw new ConfigException($"{path}：有条目缺 city_id");
            }
            if (!seen.Add(cityId))
            {
                throw new ConfigException($"{path}：city_id 重复：{cityId}");
            }
            if (!knownCities.Contains(cityId))
            {
                throw new ConfigException(
                    $"{path}：city_id 不在 city.json 里：{cityId}");
            }

            var prefix = (rule.MatchPrefix ?? "").Trim();
            if (prefix.Length == 0)
            {
                throw new ConfigException($"{path}：城池缺 match_prefix：{cityId}");
            }

            var timeout = rule.BattleTimeoutMs ?? 0;
            if (timeout < 1 || timeout > 60000)
            {
                throw new ConfigException(
                    $"{path}：battle_timeout_ms 要在 1..60000 之间：{cityId}");
            }

            var taxPct = rule.TaxPctOnCapture ?? -1;
            if (taxPct < 0 || taxPct > 100)
            {
                throw new ConfigException(
                    $"{path}：tax_pct_on_capture 要在 0..100 之间：{cityId}");
            }

            cities.Add(new CityRule(cityId, prefix, timeout, taxPct));
        }

        return new SiegeTerritoryTable(
            wire.Version, wire.Pending ?? false, cityTable, cities, missing: false);
    }

    /// <summary>直接读 config/city.json 的城池编号，不引用 Character 工程。</summary>
    static HashSet<string> ReadCityIds(string cityTable, string path)
    {
        if (!File.Exists(cityTable))
        {
            throw new ConfigException($"{path}：找不到城池表 {cityTable}");
        }

        using var cityDocument = JsonDocument.Parse(
            File.ReadAllText(cityTable),
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!cityDocument.RootElement.TryGetProperty("cities", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var city in array.EnumerateArray())
        {
            if (city.TryGetProperty("city_id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    ids.Add(value.Trim());
                }
            }
        }

        return ids;
    }

    sealed class Wire
    {
        public string Schema { get; init; } = "";
        public int Version { get; init; }
        public bool? Pending { get; init; }
        public string? CityTable { get; init; }
        public List<CityWire>? Cities { get; init; }
    }

    sealed class CityWire
    {
        public string? CityId { get; init; }
        public string? MatchPrefix { get; init; }
        public int? BattleTimeoutMs { get; init; }
        public int? TaxPctOnCapture { get; init; }
    }
}
