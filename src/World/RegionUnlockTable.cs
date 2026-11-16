using System.Text.Json;

namespace World;

/// <summary>一块「静态不开放」的地表按剧情解锁的条件：交完第几场、以及走的是哪一路。</summary>
public sealed record RegionUnlockDef(string RegionId, int UnlockStage, string Alignment);

/// <summary>
/// N1 地表解锁的可选配置正本（config/region_unlock.json）。
///
/// 方案 2.5 写定旧天城区与焦原「第 11 场才开」，方案 2.8 又写定「立仙或立魔在第 10 场写入，
/// 两项都空着时两块都不开」，而 2.5 的两条短章分别是「立仙开放旧天城区」「立魔开放焦原」。
/// 也就是说一块地表能不能进由两件事决定：剧情走到第几场、第 10 场选了哪一路。
/// 剧情进度与立仙立魔都只在角色服（技术架构 6 节），世界服不连角色库、也不自己存一份，
/// 每次判定都去问角色服；本表只记「这块地表要第几场、要哪一路」这一份内容。
///
/// 这张表是可选的：老卡的临时配置目录里没有它，世界服按 P1-3a1 的静态 open 照常跑，
/// 于是 P1-3c / C2 的老断言（旧天城区与焦原 region_locked）一个字都不用改。
/// 坏表照样起不来，不带着半截解锁条件放人进封闭地表。
/// </summary>
public sealed class RegionUnlockTable
{
    public const string Schema = "world-region-unlock/1";
    public const int Version = 1;

    /// <summary>方案 2.5 的两条路；与 config/mainline.json 的 alignment_choices 同一套字面量。</summary>
    public const string AlignmentImmortal = "立仙";
    public const string AlignmentDemon = "立魔";

    static readonly string[] Alignments = [AlignmentImmortal, AlignmentDemon];

    static readonly HashSet<string> TopKeys = new(StringComparer.Ordinal)
    {
        "schema", "version", "pending", "note", "regions",
    };

    static readonly HashSet<string> EntryKeys = new(StringComparer.Ordinal)
    {
        "region_id", "unlock_stage", "alignment",
    };

    RegionUnlockTable(IReadOnlyList<RegionUnlockDef> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<RegionUnlockDef> Entries { get; }

    public RegionUnlockDef? Find(string regionId) =>
        Entries.FirstOrDefault(e => string.Equals(e.RegionId, regionId, StringComparison.Ordinal));

    /// <summary>一张表里出现过几路（用于启动日志与只读口，不参与判定）。</summary>
    public string Routes => string.Join(",", Entries.Select(e => e.RegionId + ":" + e.Alignment));

    /// <summary>缺这份可选表就返回 null，世界服按 P1-3a1 的静态 open 照常启动。</summary>
    public static RegionUnlockTable? LoadIfPresent(string path, WorldCatalog catalog)
        => File.Exists(path) ? Load(path, catalog) : null;

    public static RegionUnlockTable Load(string path, WorldCatalog catalog)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到地表解锁配置 " + path);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigException("地表解锁配置不是合法 JSON：" + ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("region_unlock.json 顶层必须是对象");
            }

            RequireKeys(root, TopKeys, "region_unlock.json 顶层");

            var schema = Str(root, "schema");
            if (schema != Schema)
            {
                throw new ConfigException("region_unlock.json 的 schema 必须是 " + Schema);
            }

            var version = Num(root, "version");
            if (version != Version)
            {
                throw new ConfigException("region_unlock.json 的 version 必须是 " + Version);
            }

            if (!root.TryGetProperty("regions", out var array) || array.ValueKind != JsonValueKind.Array
                || array.GetArrayLength() == 0)
            {
                throw new ConfigException("region_unlock.json 缺 regions");
            }

            var entries = new List<RegionUnlockDef>(array.GetArrayLength());
            var seenRegion = new HashSet<string>(StringComparer.Ordinal);
            var seenAlignment = new HashSet<string>(StringComparer.Ordinal);

            foreach (var raw in array.EnumerateArray())
            {
                if (raw.ValueKind != JsonValueKind.Object)
                {
                    throw new ConfigException("region_unlock.json 的 regions 里有一条不是对象");
                }

                var regionId = (Str(raw, "region_id") ?? "").Trim();
                if (regionId.Length == 0)
                {
                    throw new ConfigException("region_unlock.json 里有空 region_id");
                }

                RequireKeys(raw, EntryKeys, "region_unlock.json 条目 " + regionId);

                if (!seenRegion.Add(regionId))
                {
                    throw new ConfigException("region_unlock.json 里同一块地表配了两次：" + regionId);
                }

                var region = catalog.Find(regionId)
                    ?? throw new ConfigException("region_unlock.json 指到了不存在的地表 " + regionId);

                if (region.Open)
                {
                    // 已经静态开放的地表再配一条解锁条件，等于给同一块地开了两套开关。
                    throw new ConfigException(
                        "region_unlock.json 只配静态不开放的地表，" + regionId + " 的 open=true");
                }

                var stage = Num(raw, "unlock_stage");
                if (stage < 1 || stage > 12)
                {
                    // 主线一共十二场（方案 2 章「主线关卡」表），解锁场次超出这个范围就是配错了。
                    throw new ConfigException(
                        "region_unlock.json 的 unlock_stage 必须在 1 到 12 之间：" + regionId);
                }

                var alignment = (Str(raw, "alignment") ?? "").Trim();
                if (!Alignments.Contains(alignment, StringComparer.Ordinal))
                {
                    // 只有立仙 / 立魔两条路（方案 2.5），别的字面量一律不肯收。
                    throw new ConfigException(
                        "region_unlock.json 的 alignment 只许立仙或立魔：" + regionId + " = " + alignment);
                }

                if (!seenAlignment.Add(alignment))
                {
                    // 两期方案都只给了「一条路走一段短章」，同一路两个入口会让人不知道该去哪。
                    throw new ConfigException("region_unlock.json 里同一条路配了两个入口：" + alignment);
                }

                entries.Add(new RegionUnlockDef(regionId, stage, alignment));
            }

            // 结构保证：静态不开放的地表必须都有解锁条目，否则那块地表永远进不去、
            // 而且没有任何地方会说出来——这比配错更难发现。
            foreach (var region in catalog.Regions)
            {
                if (!region.Open && !seenRegion.Contains(region.Id))
                {
                    throw new ConfigException(
                        "region_unlock.json 漏了静态不开放的地表 " + region.Id + "，它永远进不去");
                }
            }

            return new RegionUnlockTable(entries);
        }
    }

    /// <summary>先逐类查缺、再查多：缺项比「多了一个键」更该被报出来，顺序反了会互相盖。</summary>
    static void RequireKeys(JsonElement obj, HashSet<string> allowed, string where)
    {
        foreach (var key in allowed)
        {
            if (!obj.TryGetProperty(key, out _) && key is not ("pending" or "note"))
            {
                throw new ConfigException(where + " 缺字段 " + key);
            }
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ConfigException(where + " 出现不认识的键 " + property.Name);
            }
        }
    }

    static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static int Num(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number))
        {
            throw new ConfigException("region_unlock.json 的 " + name + " 必须是整数");
        }

        return number;
    }
}
