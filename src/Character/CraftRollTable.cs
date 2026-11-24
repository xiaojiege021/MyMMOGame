using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>一个熟练度档：制作次数下限 + 高值段权重（低值段权重恒为 1）。</summary>
public sealed record SkillTierDef(int MinCrafts, int HighWeight);

/// <summary>一条附加属性的允许值：部位 × 品阶 × 属性 → 区间。</summary>
public sealed record ExtraDef(string Slot, string Grade, string Attr, int Min, int Max);

/// <summary>
/// config/craft_roll.json 是制作掷点的唯一正本（方案 9.1）。
///
/// 方案写定「主属性在配方下限与上限之间取值；再掷最多两条附加属性，种类只从这件装备的
/// 部位和品阶允许的表里来；制作技能只提高高值权重，不抬上限、不改品阶」。方案没有给
/// 区间、权重与属性种类，所以这三样全在这份文件里：代码只按表掷，不写第二份数字。
///
/// 加载期硬校验（坏配置 config_error，不兜底）：
///   - skill_tiers 从 0 起、min_crafts 严格递增、high_weight ≥ 1；
///   - extra_count_max 在 1..2（方案「最多两条」）；
///   - extras 覆盖 items.json 现取的全部装备「部位 × 品阶」，同一组不重复；
///   - 每条配方的 grade_roll 与该档基准对得上（见 CheckRecipes）。
/// </summary>
public sealed class CraftRollTable
{
    public const string WrittenSchema = "craft-roll/1";

    /// <summary>当前装备能承载的属性种类。方案没给第二种，加一种要同时改战斗服，走新卡。</summary>
    public static readonly string[] WrittenAttrs = ["attack"];

    readonly Dictionary<string, IReadOnlyList<ExtraDef>> extrasByKey = new(StringComparer.Ordinal);

    CraftRollTable(
        int version, bool pending, IReadOnlyList<SkillTierDef> tiers, int extraCountMax,
        IReadOnlyList<ExtraDef> extras)
    {
        Version = version;
        Pending = pending;
        SkillTiers = tiers;
        ExtraCountMax = extraCountMax;
        Extras = extras;
    }

    public int Version { get; }

    /// <summary>整表是不是还没定数（数值台账表 B）。</summary>
    public bool Pending { get; }

    public IReadOnlyList<SkillTierDef> SkillTiers { get; }

    public int ExtraCountMax { get; }

    public IReadOnlyList<ExtraDef> Extras { get; }

    /// <summary>按这个副职的累计制作次数选档；超过最高档就用最高档。</summary>
    public SkillTierDef TierOf(int craftCount)
    {
        var chosen = SkillTiers[0];
        for (var i = 0; i < SkillTiers.Count; i++)
        {
            if (craftCount >= SkillTiers[i].MinCrafts)
            {
                chosen = SkillTiers[i];
            }
        }

        return chosen;
    }

    /// <summary>这个部位 / 品阶允许的附加属性。加载期已校验非空。</summary>
    public IReadOnlyList<ExtraDef> ExtrasFor(string slot, string grade)
        => extrasByKey.GetValueOrDefault(slot + "/" + grade) ?? [];

    public static CraftRollTable Load(string path, ItemTable items, GradeTable grades)
    {
        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), Options)
                ?? throw new ConfigException("craft_roll 解析为空");
        }
        catch (JsonException ex)
        {
            throw new ConfigException("craft_roll 不是合法 JSON：" + ex.Message);
        }

        if (!string.Equals(wire.Schema, WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException("craft_roll 的 schema 必须是 " + WrittenSchema);
        }

        var version = wire.Version ?? 0;
        if (version < 1)
        {
            throw new ConfigException("craft_roll 缺 version");
        }

        var tiers = new List<SkillTierDef>();
        var tierWire = wire.SkillTiers ?? [];
        if (tierWire.Count == 0)
        {
            throw new ConfigException("craft_roll 的 skill_tiers 一条都没有");
        }

        var lastMin = -1;
        foreach (var raw in tierWire)
        {
            var min = raw.MinCrafts ?? -1;
            var weight = raw.HighWeight ?? 0;
            if (min < 0)
            {
                throw new ConfigException("craft_roll 的 skill_tiers 缺 min_crafts");
            }

            if (min <= lastMin)
            {
                throw new ConfigException("craft_roll 的 skill_tiers 必须按 min_crafts 严格递增");
            }

            if (lastMin < 0 && min != 0)
            {
                throw new ConfigException("craft_roll 的第一档 min_crafts 必须是 0");
            }

            if (weight < 1 || weight > 100)
            {
                throw new ConfigException("craft_roll 的 high_weight 必须在 1 到 100 之间");
            }

            lastMin = min;
            tiers.Add(new SkillTierDef(min, weight));
        }

        var extraMax = wire.ExtraCountMax ?? 0;
        if (extraMax < 1 || extraMax > 2)
        {
            throw new ConfigException("craft_roll 的 extra_count_max 必须在 1 到 2 之间（方案最多两条）");
        }

        var extras = new List<ExtraDef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in wire.Extras ?? [])
        {
            var slot = (raw.Slot ?? "").Trim();
            var grade = (raw.Grade ?? "").Trim();
            var attr = (raw.Attr ?? "").Trim();
            var min = raw.Min ?? 0;
            var max = raw.Max ?? 0;

            if (slot.Length == 0 || grade.Length == 0 || attr.Length == 0)
            {
                throw new ConfigException("craft_roll 的 extras 有一条缺 slot / grade / attr");
            }

            if (grades.Find(grade) is null)
            {
                throw new ConfigException("craft_roll 的 extras 引用了不存在的品阶：" + grade);
            }

            if (Array.IndexOf(WrittenAttrs, attr) < 0)
            {
                throw new ConfigException("craft_roll 的 extras 里有当前装备承载不了的属性：" + attr);
            }

            if (min < 1 || max < min)
            {
                throw new ConfigException(
                    "craft_roll 的 extras 区间非法（" + slot + "/" + grade + "/" + attr + "）");
            }

            if (!seen.Add(slot + "/" + grade + "/" + attr))
            {
                throw new ConfigException(
                    "craft_roll 的 extras 重复： " + slot + "/" + grade + "/" + attr);
            }

            extras.Add(new ExtraDef(slot, grade, attr, min, max));
        }

        var table = new CraftRollTable(version, wire.Pending ?? false, tiers, extraMax, extras);
        table.BuildIndex();
        table.CheckCoverage(items);
        return table;
    }

    /// <summary>
    /// items.json 里现取全部装备的「部位 × 品阶」，少一格就起不来：否则那种装备做出来
    /// 永远掷不到附加属性，而配置看起来是好的。
    /// </summary>
    void CheckCoverage(ItemTable items)
    {
        var need = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.All)
        {
            if (!item.IsEquipment || item.Slot.Length == 0 || item.Grade.Length == 0)
            {
                continue;
            }

            need.Add(item.Slot + "/" + item.Grade);
        }

        foreach (var key in need)
        {
            if (!extrasByKey.ContainsKey(key))
            {
                throw new ConfigException("craft_roll 的 extras 缺这一组装备：" + key);
            }
        }
    }

    /// <summary>
    /// 每条装备配方的 grade_roll 与品阶基准对账（方案 9.1）：
    /// 下限不低于该品阶基准、上限不超过下一品阶基准（红装没有下一档，停在自身上限）。
    /// </summary>
    public void CheckRecipes(RecipeTable recipes, GradeTable grades)
    {
        var ordered = grades.Ordered;
        foreach (var recipe in recipes.Recipes)
        {
            var roll = recipe.Roll;
            if (roll is null)
            {
                continue;
            }

            var grade = recipe.Grade;
            var def = grades.Find(grade)
                ?? throw new ConfigException("配方 " + recipe.Id + " 的 grade 不存在：" + grade);

            if (roll.AttrMin < def.BaseAttack)
            {
                throw new ConfigException(
                    "配方 " + recipe.Id + " 的 attr_min 低于 " + grade + " 档基准 " + def.BaseAttack);
            }

            long ceiling = def.BaseAttack;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Order == def.Order + 1)
                {
                    ceiling = ordered[i].BaseAttack;
                    break;
                }
            }

            if (roll.AttrMax > ceiling)
            {
                throw new ConfigException(
                    "配方 " + recipe.Id + " 的 attr_max 超过下一档基准 " + ceiling);
            }

            if (roll.AttrMin > roll.AttrMax)
            {
                throw new ConfigException("配方 " + recipe.Id + " 的 attr_min 大于 attr_max");
            }

            if (roll.ExtrasMax < 0 || roll.ExtrasMax > ExtraCountMax)
            {
                throw new ConfigException(
                    "配方 " + recipe.Id + " 的 extras_max 必须在 0 到 " + ExtraCountMax + " 之间");
            }
        }
    }

    /// <summary>把 extras 按「部位/品阶」分好组。加载末尾调用一次。</summary>
    internal void BuildIndex()
    {
        var grouped = new Dictionary<string, List<ExtraDef>>(StringComparer.Ordinal);
        foreach (var extra in Extras)
        {
            var key = extra.Slot + "/" + extra.Grade;
            if (!grouped.TryGetValue(key, out var list))
            {
                list = [];
                grouped[key] = list;
            }

            list.Add(extra);
        }

        foreach (var pair in grouped)
        {
            extrasByKey[pair.Key] = pair.Value;
        }
    }

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    sealed record Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; init; }
        [JsonPropertyName("version")] public int? Version { get; init; }
        [JsonPropertyName("pending")] public bool? Pending { get; init; }
        [JsonPropertyName("skill_tiers")] public List<WireTier>? SkillTiers { get; init; }
        [JsonPropertyName("extra_count_max")] public int? ExtraCountMax { get; init; }
        [JsonPropertyName("extras")] public List<WireExtra>? Extras { get; init; }
    }

    sealed record WireTier
    {
        [JsonPropertyName("min_crafts")] public int? MinCrafts { get; init; }
        [JsonPropertyName("high_weight")] public int? HighWeight { get; init; }
    }

    sealed record WireExtra
    {
        [JsonPropertyName("slot")] public string? Slot { get; init; }
        [JsonPropertyName("grade")] public string? Grade { get; init; }
        [JsonPropertyName("attr")] public string? Attr { get; init; }
        [JsonPropertyName("min")] public int? Min { get; init; }
        [JsonPropertyName("max")] public int? Max { get; init; }
    }
}
