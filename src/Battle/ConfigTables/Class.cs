using System.Text.Json;
using System.Text.Json.Serialization;

namespace Battle;

public sealed class ResurrectConfig
{
    public int HpRestorePct { get; init; }
    public int ExpCostPct { get; init; }

    /// <summary>
    /// P2-4a：生息二转之后扣的经验比例，比一转基础值小。方案只说「把这个比例减小、
    /// 少多少走配置」，数字在这里；卷轴仍按 ExpCostPct 扣，不吃这个减免。
    /// </summary>
    public int SecondTurnExpCostPct { get; init; }
}

/// <summary>
/// A9：角色手动加到四属性上的点数（来自角色服快照，0 = 没加点）。
/// 只与职业表的基础属性相加，战斗服不另存一份属性数值。
/// 竞技场模板不吃它；副本 / 练习场 / 世界 Boss / 战场 / 城战吃。
/// </summary>
public readonly record struct AttrBonus(long Strength, long Agility, long Spirit, long Vitality)
{
    public static readonly AttrBonus None = default;

    public bool IsZero => Strength == 0 && Agility == 0 && Spirit == 0 && Vitality == 0;
}

public sealed class AttributesConfig
{
    public long Strength { get; init; }
    public long Agility { get; init; }
    public long Spirit { get; init; }
    public long Vitality { get; init; }

    public long Get(string primary) => primary switch
    {
        "strength" => Strength,
        "agility" => Agility,
        "spirit" => Spirit,
        _ => 0,
    };

    /// <summary>A9：基础 + 加点。加四条，不加任何系数。</summary>
    public AttributesConfig Plus(AttrBonus bonus) => new()
    {
        Strength = Strength + bonus.Strength,
        Agility = Agility + bonus.Agility,
        Spirit = Spirit + bonus.Spirit,
        Vitality = Vitality + bonus.Vitality,
    };
}

/// <summary>
/// 一个职业。属性、蓝条系数、仇恨系数都在这里，不在代码里写死：
/// 生命 = 体力 × hp_per_vitality（表级）；
/// 蓝量上限 = mana_base + 精神 × mana_per_spirit；
/// 攻击 = 主属性 × attack_per_primary + 装备固定攻击（坦克主属性是 none，只吃装备）；
/// 仇恨稳定值 = 体力 × threat_per_vitality。
/// </summary>
public sealed class ClassDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Primary { get; init; } = "none";
    public AttributesConfig Attributes { get; init; } = new();
    public double AttackPerPrimary { get; init; }
    public long EquipmentAttack { get; init; }
    public double HealPerSpirit { get; init; }
    public long ManaBase { get; init; }
    public long ManaPerSpirit { get; init; }
    public long ThreatPerVitality { get; init; }
    public List<uint> Skills { get; init; } = [];

    // A9：四条公式都先加加点再算。不传 bonus 时与 A9 之前逐字相同（默认参数 = 四条都是 0）。
    public long HpMax(long hpPerVitality, AttrBonus bonus = default) =>
        Attributes.Plus(bonus).Vitality * hpPerVitality;

    public double Attack(AttrBonus bonus = default) =>
        Attributes.Plus(bonus).Get(Primary) * AttackPerPrimary + EquipmentAttack;

    public long ManaMax(AttrBonus bonus = default) =>
        ManaBase + Attributes.Plus(bonus).Spirit * ManaPerSpirit;

    public long ThreatStability(AttrBonus bonus = default) =>
        Attributes.Plus(bonus).Vitality * ThreatPerVitality;

    /// <summary>HealPower：精神 × 职业系数（法系那条技能从这里取值）。A9 起读加点后的精神。</summary>
    public double HealPower(AttrBonus bonus = default) =>
        Attributes.Plus(bonus).Spirit * HealPerSpirit;
}

public sealed class ClassTable
{
    readonly Dictionary<string, ClassDef> _byId;

    ClassTable(int version, long hpPerVitality, ResurrectConfig resurrect, Dictionary<string, ClassDef> byId)
    {
        Version = version;
        HpPerVitality = hpPerVitality;
        Resurrect = resurrect;
        _byId = byId;
    }

    public int Version { get; }

    public long HpPerVitality { get; }

    public ResurrectConfig Resurrect { get; }

    public IReadOnlyCollection<ClassDef> All => _byId.Values;

    public bool TryGet(string id, out ClassDef def) => _byId.TryGetValue(id, out def!);

    public static ClassTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"职业表不存在：{path}");
        }

        var file = ConfigJson.Read<ClassFile>(path);
        var byId = new Dictionary<string, ClassDef>(StringComparer.Ordinal);
        foreach (var cls in file.Classes)
        {
            if (string.IsNullOrWhiteSpace(cls.Id))
            {
                throw new ConfigException($"{path}：有职业没写 id");
            }
            if (string.IsNullOrWhiteSpace(cls.Name))
            {
                throw new ConfigException($"{path}：职业 {cls.Id} 没有名字");
            }
            if (cls.Primary is not ("none" or "strength" or "agility" or "spirit"))
            {
                throw new ConfigException($"{path}：职业 {cls.Id} 的 primary 只能是 none/strength/agility/spirit");
            }
            if (cls.Attributes.Strength < 0 || cls.Attributes.Agility < 0 ||
                cls.Attributes.Spirit < 0 || cls.Attributes.Vitality <= 0)
            {
                throw new ConfigException($"{path}：职业 {cls.Id} 的属性不能是负数，体力必须大于 0");
            }
            if (cls.AttackPerPrimary < 0 || cls.EquipmentAttack < 0 || cls.HealPerSpirit < 0 ||
                cls.ManaBase < 0 || cls.ManaPerSpirit < 0 || cls.ThreatPerVitality < 0)
            {
                throw new ConfigException($"{path}：职业 {cls.Id} 的系数不能是负数");
            }
            if (cls.Skills.Count == 0)
            {
                throw new ConfigException($"{path}：职业 {cls.Id} 一个技能都没有");
            }
            if (!byId.TryAdd(cls.Id, cls))
            {
                throw new ConfigException($"{path}：职业 id {cls.Id} 重复");
            }
        }

        if (byId.Count == 0)
        {
            throw new ConfigException($"{path}：职业表是空的");
        }

        if (file.Resurrect.HpRestorePct is < 0 or > 100)
        {
            throw new ConfigException($"{path}：resurrect.hp_restore_pct 要在 0..100 之间");
        }
        if (file.Resurrect.ExpCostPct is < 0 or > 100)
        {
            throw new ConfigException($"{path}：resurrect.exp_cost_pct 要在 0..100 之间");
        }
        // 二转只把比例减小：写大了就不是「减少」了，配置直接不认。
        if (file.Resurrect.SecondTurnExpCostPct is < 0 or > 100
            || file.Resurrect.SecondTurnExpCostPct > file.Resurrect.ExpCostPct)
        {
            throw new ConfigException(
                $"{path}：resurrect.second_turn_exp_cost_pct 要在 0..exp_cost_pct 之间");
        }

        if (file.HpPerVitality <= 0)
        {
            throw new ConfigException($"{path}：hp_per_vitality 必须大于 0");
        }

        var resurrect = file.Resurrect;
        if (resurrect.HpRestorePct <= 0 || resurrect.HpRestorePct > 100 ||
            resurrect.ExpCostPct < 0 || resurrect.ExpCostPct > 100)
        {
            throw new ConfigException($"{path}：resurrect 的 hp_restore_pct 要在 (0,100]，exp_cost_pct 要在 [0,100]");
        }

        if (file.Version <= 0)
        {
            throw new ConfigException($"{path}：version 必须大于 0");
        }

        return new ClassTable(file.Version, file.HpPerVitality, resurrect, byId);
    }

    sealed class ClassFile
    {
        public int Version { get; init; }
        public long HpPerVitality { get; init; }
        public ResurrectConfig Resurrect { get; init; } = new();
        public List<ClassDef> Classes { get; init; } = [];
    }
}

/// <summary>一场战斗的启动参数。全部来自 config/battle.json。</summary>
