using System.Text.Json;

namespace Character;

/// <summary>
/// P3-3a4：角色服读 config/siege_territory.json 里「易主后的城池抽成比例」。
/// 战斗服读同一份正本的「哪场打哪座城 / 开场多久结束」，两端各取所需，不另起一套数字。
/// 方案 9.5 / 12 章写定「掌管方按城池税收抽成，比例走配置」，没给数字；
/// 这里的 0 只是配置里的待定占位，代码里不写第二个税率。
///
/// 只认 city_id 与 tax_pct_on_capture：胜方宗门、归属、奖励、商品字段一概不在这里，
/// 配置里出现 owner_sect / winner / reward / shop 直接起不来。
/// </summary>
public sealed class SiegeTerritoryPolicy
{
    readonly Dictionary<string, int> _taxPct;

    SiegeTerritoryPolicy(
        int version, bool pending, string sourcePath, Dictionary<string, int> taxPct)
    {
        Version = version;
        Pending = pending;
        SourcePath = sourcePath;
        _taxPct = taxPct;
    }

    public int Version { get; }
    public bool Pending { get; }
    public string SourcePath { get; }

    /// <summary>这个城池易主后的抽成比例；配置里没有就不写回，绝不猜一个数。</summary>
    public int TaxPctOf(string cityId)
    {
        if (!_taxPct.TryGetValue(cityId, out var pct))
        {
            throw new ConfigException("领地表里没有这座城的抽成比例：" + cityId);
        }
        return pct;
    }

    public static SiegeTerritoryPolicy Load(string path, CityTable cities)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到领地表 " + path);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new ConfigException("领地表不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            foreach (var property in root.EnumerateObject())
            {
                var key = property.Name.ToLowerInvariant();
                if (key is "owner_sect" or "winner" or "winner_sect" or "reward"
                    or "shop" or "price" or "balance" or "ratio")
                {
                    throw new ConfigException(
                        $"{path}：领地表不许出现 {property.Name}（归属只由城战结果写回）");
                }
            }

            if (!root.TryGetProperty("schema", out var schemaElement)
                || schemaElement.GetString() != "siege-territory/1")
            {
                throw new ConfigException(path + "：schema 必须是 siege-territory/1");
            }
            if (!root.TryGetProperty("version", out var versionElement)
                || !versionElement.TryGetInt32(out var version) || version <= 0)
            {
                throw new ConfigException(path + "：version 必须是正整数");
            }
            var pending = root.TryGetProperty("pending", out var pendingElement)
                && pendingElement.ValueKind == JsonValueKind.True;

            if (!root.TryGetProperty("cities", out var cityArray)
                || cityArray.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException(path + "：cities 必须是数组");
            }

            var known = new HashSet<string>(
                cities.Cities.Select(c => c.CityId), StringComparer.Ordinal);
            var taxPct = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var city in cityArray.EnumerateArray())
            {
                if (!city.TryGetProperty("city_id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String)
                {
                    throw new ConfigException(path + "：有条目缺 city_id");
                }
                var cityId = (idElement.GetString() ?? "").Trim();
                if (cityId.Length == 0 || !known.Contains(cityId))
                {
                    throw new ConfigException(path + "：city_id 不在 city.json 里：" + cityId);
                }
                if (!city.TryGetProperty("tax_pct_on_capture", out var taxElement)
                    || !taxElement.TryGetInt32(out var pct) || pct < 0 || pct > 100)
                {
                    throw new ConfigException(
                        path + "：tax_pct_on_capture 要在 0..100 之间：" + cityId);
                }
                taxPct[cityId] = pct;
            }

            if (taxPct.Count == 0)
            {
                throw new ConfigException(path + "：cities 至少要有一条");
            }

            return new SiegeTerritoryPolicy(version, pending, path, taxPct);
        }
    }
}
