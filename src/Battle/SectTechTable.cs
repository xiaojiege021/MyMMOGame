using System.Text.Json;

namespace Battle;

/// <summary>
/// P3-3a3：宗门科技加成折算表（config/sect_tech.json 是唯一正本）。
///
/// 方案 11.1 写「贡献 / 建设 / 科技三条线，科技只在城战生效」，12.3 写城战
/// 「装备全额生效 + 宗门科技加成」；开发架构 4.4 / 8 / 9 节写定「社交服不算伤害，
/// 战斗服开城战时来读科技」。所以这里只放「每级科技给多少加成」的折算，科技等级
/// 本身由社交服的 "社交".sect_tech 给，不在这份配置里编一份等级，也不读名册。
///
/// 加成是「装备全额（k 恒为 1）之上的第二层」，不是平衡系数：不做同队压缩、
/// 不套竞技场模板、不写回角色库。方案没给档位数与每级加成，配置里的数字都是
/// 正整数占位并标待定，代码里不写第二套数字。
/// </summary>
public sealed class SectTechTable
{
    SectTechTable(int version, int powerPctPerLevel, int maxLevel, bool pending, bool missing)
    {
        Version = version;
        PowerPctPerLevel = powerPctPerLevel;
        MaxLevel = maxLevel;
        Pending = pending;
        IsMissing = missing;
    }

    public int Version { get; }
    public int PowerPctPerLevel { get; }
    public int MaxLevel { get; }
    public bool Pending { get; }

    /// <summary>没配科技表：城战照常开场，科技恒 0，不把副本 / 练习场拖垮。</summary>
    public bool IsMissing { get; }

    public static SectTechTable Missing() => new(0, 0, 0, true, missing: true);

    /// <summary>只城战调用：等级 0 或没配表时恒 1，其他模式连这个方法都不会走到。</summary>
    public double Factor(int level)
    {
        if (IsMissing || PowerPctPerLevel <= 0 || MaxLevel <= 0)
        {
            return 1.0;
        }

        var capped = Math.Clamp(level, 0, MaxLevel);
        return 1.0 + (double)PowerPctPerLevel * capped / 100.0;
    }

    /// <summary>把加成折算成战力事实值，只打进日志给核对看，不拿去算平衡系数。</summary>
    public long Bonus(long rawPower, int level) =>
        (long)Math.Round(rawPower * (Factor(level) - 1.0));

    public static SectTechTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"宗门科技表不存在：{path}");
        }

        // 反序列化会静默忽略未知字段，先扫一遍顶层键：平衡系数 / 模板 / 商城字段
        // 混进这张表就直接起不来，让「城战不压缩、科技不售卖」成为结构保证。
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(File.ReadAllText(path), options);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var key = property.Name.ToLowerInvariant();
            if (key is "balance" or "ratio" or "templates" or "raise_low_only"
                or "adjusted_power" or "arena_match" or "shop" or "mall" or "price"
                or "ingot" or "delivered_kinds" or "reward" or "tax")
            {
                throw new ConfigException(
                    $"{path}：宗门科技表不许出现 {property.Name}（不做平衡系数 / 模板，也不卖科技）");
            }

            if (key == "bonus" && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in property.Value.EnumerateObject())
                {
                    var nestedKey = nested.Name.ToLowerInvariant();
                    if (nestedKey is "model" or "prefab" or "appearance" or "tier"
                        or "team_color" or "hp_bar" or "shop" or "price" or "ingot")
                    {
                        throw new ConfigException(
                            $"{path}：bonus 段不许出现 {nested.Name}（只放每级加成折算）");
                    }
                }
            }
        }

        var wire = ConfigJson.Read<Wire>(path);
        if (wire.Schema != "sect-tech-table/1")
        {
            throw new ConfigException($"{path}：schema 必须是 sect-tech-table/1，现在是 {wire.Schema}");
        }
        if (wire.Version <= 0)
        {
            throw new ConfigException($"{path}：version 必须是正整数");
        }

        var bonus = wire.Bonus ?? throw new ConfigException($"{path}：没有 bonus 段（每级加成折算）");
        if (bonus.PowerPctPerLevel < 1 || bonus.PowerPctPerLevel > 100)
        {
            throw new ConfigException($"{path}：bonus.power_pct_per_level 要在 1..100 之间");
        }
        if (bonus.MaxLevel < 1 || bonus.MaxLevel > 100)
        {
            throw new ConfigException($"{path}：bonus.max_level 要在 1..100 之间");
        }

        return new SectTechTable(
            wire.Version, bonus.PowerPctPerLevel, bonus.MaxLevel, bonus.Pending,
            missing: false);
    }

    sealed class Wire
    {
        public string Schema { get; init; } = "";
        public int Version { get; init; }
        public BonusWire? Bonus { get; init; }
    }

    sealed class BonusWire
    {
        public int PowerPctPerLevel { get; init; }
        public int MaxLevel { get; init; }
        public bool Pending { get; init; }
    }
}
