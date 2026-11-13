using System.Text.Json;
using System.Text.Json.Serialization;

namespace Battle;

/// <summary>配置有问题时抛这个，启动直接失败，不带着半截配置跑。</summary>
public sealed class ConfigException(string message) : Exception(message);

public sealed class PointConfig
{
    public int X { get; init; }
    public int Y { get; init; }
}

public sealed class CasterConfig
{
    public ulong Id { get; init; }

    /// <summary>出生点。生命上限不在这里：体力是所有人的生命，数值在职业表。</summary>
    public PointConfig SpawnMm { get; init; } = new();
}

public sealed class BossConfig
{
    public ulong Id { get; init; }
    public string Name { get; init; } = "";
    public long HpMax { get; init; }
    public PointConfig SpawnMm { get; init; } = new();
    public long ExpOnClear { get; init; }

    /// <summary>
    /// Boss 的攻击。P0 里 Boss 只做「按间隔打仇恨最高的那个人」这一种，
    /// 读招时间就是 foreswing_ms。不配这一段 Boss 就不出手——单人核对
    /// 还是老样子，不会平白多掉血。
    /// </summary>
    public BossAttackConfig? Attack { get; init; }

    /// <summary>
    /// 阶段表。每段一个血线区间和自己的攻击参数，到线就切。
    /// 不配就是单阶段（用 attack 那段）。不设霸体条和破防窗口。
    /// </summary>
    public List<PhaseConfig>? Phases { get; init; }
}

public sealed class BossAttackConfig
{
    public long Damage { get; init; }
    public int IntervalMs { get; init; }
    public int ForeswingMs { get; init; }
}

public sealed class PhaseConfig
{
    public string Name { get; init; } = "";
    public int HpPctFrom { get; init; }
    public int HpPctTo { get; init; }
    public BossAttackConfig? Attack { get; init; }
}

/// <summary>
/// 场地参数。半径管住玩家能走多远，速度管住一次走位最多跨多少。
/// 两个都是服务端权威：客户端报的位置要按这两个夹过才算数。
/// </summary>
public sealed class ArenaConfig
{
    public int RadiusMm { get; init; }
    public int MoveSpeedMmPerS { get; init; }
}

public enum SkillKind
{
    Basic,
    Active,
    Heal,
    Resurrect,
}

/// <summary>
/// 一个技能的全部数字。这里不写死任何数值，技术架构第 7 节要求技能走配置。
/// 伤害不是平的：这一下 = 施法者攻击 × damage_multiplier。
/// 攻击 = 输出主属性 × 职业系数 + 装备固定攻击，在职业表里。
/// </summary>
public sealed class SkillDef
{
    public uint Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>这个技能属于哪个职业。跨职业发技能按未知技能拒绝。</summary>
    public string ClassId { get; init; } = "";

    public SkillKind Kind { get; init; }

    public double DamageMultiplier { get; init; }

    /// <summary>蓝耗。普攻可以配 0，主动技能配了就要检查蓝量。</summary>
    public int ManaCost { get; init; }

    public uint RangeMm { get; init; }
    public int ForeswingMs { get; init; }
    public int RecoveryMs { get; init; }
    public int CooldownMs { get; init; }
    public int StaggerMs { get; init; }
}

public sealed class SkillTable
{
    readonly Dictionary<uint, SkillDef> _byId;

    SkillTable(int version, Dictionary<uint, SkillDef> byId)
    {
        Version = version;
        _byId = byId;
    }

    public int Version { get; }

    public IReadOnlyCollection<SkillDef> All => _byId.Values;

    public bool TryGet(uint id, out SkillDef def) => _byId.TryGetValue(id, out def!);

    public IReadOnlyList<SkillDef> ForClass(string classId) =>
        _byId.Values.Where(s => s.ClassId == classId).OrderBy(s => s.Id).ToList();

    public static SkillTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"技能表不存在：{path}");
        }

        var file = ConfigJson.Read<SkillFile>(path);
        var byId = new Dictionary<uint, SkillDef>();
        foreach (var skill in file.Skills)
        {
            if (skill.Id == 0)
            {
                throw new ConfigException($"{path}：技能编号不能是 0");
            }
            if (string.IsNullOrWhiteSpace(skill.Name))
            {
                throw new ConfigException($"{path}：技能 {skill.Id} 没有名字");
            }
            if (string.IsNullOrWhiteSpace(skill.ClassId))
            {
                throw new ConfigException($"{path}：技能 {skill.Id} 没写属于哪个职业");
            }
            if (skill.DamageMultiplier < 0 || skill.ManaCost < 0 || skill.ForeswingMs < 0 ||
                skill.RecoveryMs < 0 || skill.CooldownMs < 0 || skill.StaggerMs < 0)
            {
                throw new ConfigException($"{path}：技能 {skill.Id} 有负数");
            }
            if (skill.ForeswingMs == 0)
            {
                // 前摇为零就等于「按下即结算」，那取消就没有窗口，完成标准里的
                // 「打断后这次施法没有伤害」也就无从谈起。
                throw new ConfigException($"{path}：技能 {skill.Id} 的前摇必须大于 0");
            }
            if (skill.Kind != SkillKind.Resurrect && skill.DamageMultiplier <= 0)
            {
                throw new ConfigException($"{path}：技能 {skill.Id} 的倍率必须大于 0");
            }
            if (!byId.TryAdd(skill.Id, skill))
            {
                throw new ConfigException($"{path}：技能编号 {skill.Id} 重复");
            }
        }

        if (byId.Count == 0)
        {
            throw new ConfigException($"{path}：技能表是空的");
        }

        if (file.Version <= 0)
        {
            throw new ConfigException($"{path}：version 必须大于 0");
        }

        return new SkillTable(file.Version, byId);
    }

    sealed class SkillFile
    {
        public int Version { get; init; }
        public List<SkillDef> Skills { get; init; } = [];
    }
}

/// <summary>牧师的复活：拉起倒地队友，回一部分血；被拉的人只扣当前等级已有经验的一部分。</summary>
public sealed class ResurrectConfig
{
    public int HpRestorePct { get; init; }
    public int ExpCostPct { get; init; }
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

    public long HpMax(long hpPerVitality) => Attributes.Vitality * hpPerVitality;

    public double Attack() => Attributes.Get(Primary) * AttackPerPrimary + EquipmentAttack;

    public long ManaMax() => ManaBase + Attributes.Spirit * ManaPerSpirit;

    public long ThreatStability() => Attributes.Vitality * ThreatPerVitality;
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
public sealed class BattleConfig
{
    public string Listen { get; init; } = "";
    public string SettlementDir { get; init; } = "";
    public string CharacterDir { get; init; } = "";
    public string SkillTablePath { get; init; } = "";
    public string ClassTablePath { get; init; } = "";
    public string ArenaMatchTablePath { get; init; } = "";
    public string BattlefieldTablePath { get; init; } = "";
    public string DefaultClass { get; init; } = "";

    /// <summary>
    /// 等级经验结算要交给角色服（持久数据的唯一写入方）。留空就是 P0 的
    /// 本机结算口径：只写本地记录，不改角色库；老核对与练习场都走这一条。
    /// </summary>
    public string ExpUpstream { get; init; } = "";

    /// <summary>
    /// 角色服的地址。红符要从背包里扣一张，背包只有角色服能写（技术架构第 3 节）。
    /// 留空时按 exp_upstream 走——两者本来就是同一个进程；都没有就是本机口径，
    /// 这一场用不了红符（回 upstream_off），不从别的地方变出一张来。
    /// </summary>
    public string CharacterUpstream { get; init; } = "";

    public CasterConfig Caster { get; init; } = new();
    public BossConfig Boss { get; init; } = new();
    public ArenaConfig Arena { get; init; } = new();
    public SkillTable Skills { get; private set; } = null!;
    public ClassTable Classes { get; private set; } = null!;

    /// <summary>副本表：人数上限与进本时的扣减规则。与角色服读同一份配置。</summary>
    public DungeonTable Dungeons { get; private set; } = null!;

    /// <summary>
    /// 竞技场模式与统一属性模板（config/arena_match.json）。注意与上面那个
    /// Arena 区分：Arena 是场地半径和移速，这里是 1v1 / 3v3 与模板属性。
    /// </summary>
    public ArenaMatchTable ArenaMatch { get; private set; } = null!;

    /// <summary>
    /// 战场：10 对 10、同队 1.3 倍平衡、每级强化折算与战力权重（config/battlefield.json）。
    /// 与上面的 Arena 与 ArenaMatch 都不同——战场是「装备生效 + 同队压缩」那一层。
    /// </summary>
    public BattlefieldTable Battlefield { get; private set; } = null!;

    /// <summary>配置文件的绝对路径，日志里要打出来才知道跑的是哪一份。</summary>
    public string SourcePath { get; private set; } = "";

    public static BattleConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new ConfigException($"战斗配置不存在：{full}");
        }

        var raw = ConfigJson.Read<RawConfig>(full);
        var dir = Path.GetDirectoryName(full)!;

        var skillPath = Resolve(dir, raw.SkillTable, "skills.json");
        var classPath = Resolve(dir, raw.ClassTable, "classes.json");
        var dungeonPath = Resolve(dir, raw.DungeonTable, "dungeons.json");
        var arenaMatchPath = Resolve(dir, raw.ArenaMatchTable, "arena_match.json");
        var battlefieldPath = Resolve(dir, raw.BattlefieldTable, "battlefield.json");
        // 竞技场表是可选项：没写这一项、文件也不在，就起一张空表（竞技场一律拒），
        // 副本与练习场不受影响。但显式写了路径就必须兑现，缺文件照样起不来。
        var arenaMatch = File.Exists(arenaMatchPath)
            ? ArenaMatchTable.Load(arenaMatchPath)
            : string.IsNullOrWhiteSpace(raw.ArenaMatchTable)
                ? ArenaMatchTable.Missing()
                : throw new ConfigException($"{path}：arena_match_table 指的 {arenaMatchPath} 不存在");
        // 战场表同理：老卡临时配置里没有这一项，缺失不能让副本起不来。
        var battlefield = File.Exists(battlefieldPath)
            ? BattlefieldTable.Load(battlefieldPath)
            : string.IsNullOrWhiteSpace(raw.BattlefieldTable)
                ? BattlefieldTable.Missing()
                : throw new ConfigException($"{path}：battlefield_table 指的 {battlefieldPath} 不存在");

        var config = new BattleConfig
        {
            Listen = raw.Listen,
            // 结算与角色目录按当前工作目录解析，约定从仓库根启动。
            // 启动日志会打绝对路径，跑错目录一眼能看出来。
            SettlementDir = Path.GetFullPath(raw.SettlementDir),
            CharacterDir = Path.GetFullPath(raw.CharacterDir),
            SkillTablePath = skillPath,
            ClassTablePath = classPath,
            ArenaMatchTablePath = arenaMatchPath,
            DefaultClass = string.IsNullOrWhiteSpace(raw.DefaultClass) ? "warrior" : raw.DefaultClass,
            ExpUpstream = (raw.ExpUpstream ?? "").Trim(),
            // 没单独配就跟经验结算走同一个角色服；两个都空才是本机口径。
            CharacterUpstream = string.IsNullOrWhiteSpace(raw.CharacterUpstream)
                ? (raw.ExpUpstream ?? "").Trim()
                : raw.CharacterUpstream.Trim(),
            Caster = raw.Caster,
            Boss = raw.Boss,
            Arena = raw.Arena,
            SourcePath = full,
            Skills = SkillTable.Load(skillPath),
            Classes = ClassTable.Load(classPath),
            Dungeons = DungeonTable.Load(dungeonPath),
            ArenaMatch = arenaMatch,
            Battlefield = battlefield,
        };

        // 模板与职业表对齐：每个职业都要有默认模板，模板里也不许出现职业表没有的职业。
        config.ArenaMatch.ValidateAgainst(config.Classes);
        config.Validate();
        return config;
    }

    static string Resolve(string dir, string value, string fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? Path.Combine(dir, fallback)
            : Path.IsPathRooted(value) ? value : Path.Combine(dir, value);

    void Validate()
    {
        if (string.IsNullOrWhiteSpace(Listen))
        {
            throw new ConfigException($"{SourcePath}：listen 不能为空");
        }

        var parts = Listen.Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[1], out var port) || port < 0 || port > 65535)
        {
            throw new ConfigException($"{SourcePath}：listen 要写成 主机:端口，例如 127.0.0.1:7100，现在是「{Listen}」");
        }

        if (string.IsNullOrWhiteSpace(SettlementDir))
        {
            throw new ConfigException($"{SourcePath}：settlement_dir 不能为空");
        }

        if (string.IsNullOrWhiteSpace(CharacterDir))
        {
            throw new ConfigException($"{SourcePath}：character_dir 不能为空");
        }

        if (Boss.Id == 0)
        {
            throw new ConfigException($"{SourcePath}：boss.id 不能是 0");
        }

        if (Boss.HpMax <= 0)
        {
            throw new ConfigException($"{SourcePath}：boss.hp_max 必须大于 0");
        }

        if (Caster.Id == 0)
        {
            throw new ConfigException($"{SourcePath}：caster.id 不能是 0");
        }

        if (Boss.ExpOnClear < 0)
        {
            throw new ConfigException($"{SourcePath}：boss.exp_on_clear 不能是负数");
        }

        if (Boss.Id == Caster.Id)
        {
            throw new ConfigException($"{SourcePath}：boss.id 和 caster.id 撞了");
        }

        if (!Classes.TryGet(DefaultClass, out _))
        {
            throw new ConfigException($"{SourcePath}：default_class「{DefaultClass}」不在职业表里");
        }

        ValidateBossAttacks();

        if (Arena.RadiusMm <= 0)
        {
            throw new ConfigException($"{SourcePath}：arena.radius_mm 必须大于 0");
        }

        if (Arena.MoveSpeedMmPerS <= 0)
        {
            // 速度为零等于走不动，走位这条功能就没法验，属于配置写错。
            throw new ConfigException($"{SourcePath}：arena.move_speed_mm_per_s 必须大于 0");
        }

        if (DistanceMm(Caster.SpawnMm, Boss.SpawnMm) > Arena.RadiusMm)
        {
            throw new ConfigException(
                $"{SourcePath}：出生点 {DistanceMm(Caster.SpawnMm, Boss.SpawnMm)}mm 已经出了场地半径 {Arena.RadiusMm}mm");
        }

        ValidateClassSkills();

        // 技能表加载时已经校验过每一项，这里只看至少有一个技能打得到这个站位。
        // 全都打不到的话这场永远打不完，属于配置写错，早报早改。
        var distance = DistanceMm(Caster.SpawnMm, Boss.SpawnMm);
        if (!Skills.All.Any(s => s.RangeMm >= distance))
        {
            throw new ConfigException(
                $"{SourcePath}：站位距离 {distance}mm，但技能表里没有一个技能够得着，这场打不完");
        }
    }

    void ValidateBossAttacks()
    {
        void CheckAttack(string where, BossAttackConfig? attack)
        {
            if (attack is null)
            {
                return;
            }
            if (attack.Damage <= 0 || attack.IntervalMs <= 0 || attack.ForeswingMs < 0)
            {
                throw new ConfigException($"{SourcePath}：{where} 的 attack 要 damage>0、interval_ms>0、foreswing_ms>=0");
            }
        }

        CheckAttack("boss", Boss.Attack);

        var phases = Boss.Phases;
        if (phases is null || phases.Count == 0)
        {
            return;
        }

        if (phases.Count < 2)
        {
            throw new ConfigException($"{SourcePath}：配了 phases 就至少两段");
        }

        var expectedFrom = 100;
        foreach (var phase in phases)
        {
            if (string.IsNullOrWhiteSpace(phase.Name))
            {
                throw new ConfigException($"{SourcePath}：阶段没写名字");
            }
            if (phase.HpPctFrom != expectedFrom || phase.HpPctTo >= phase.HpPctFrom)
            {
                throw new ConfigException(
                    $"{SourcePath}：阶段 {phase.Name} 要从 {expectedFrom} 起、到更低的血线，一段接一段盖满 100 到 0");
            }
            CheckAttack($"阶段 {phase.Name}", phase.Attack);
            expectedFrom = phase.HpPctTo;
        }

        if (expectedFrom != 0)
        {
            throw new ConfigException($"{SourcePath}：阶段最后要落到 0，现在落在 {expectedFrom}");
        }
    }

    /// <summary>技能表和职业表要对得上：技能属于的职业要存在，职业列的技能要真有且归属正确。</summary>
    void ValidateClassSkills()
    {
        var resurrectPerClass = new Dictionary<string, int>();
        foreach (var skill in Skills.All)
        {
            if (!Classes.TryGet(skill.ClassId, out _))
            {
                throw new ConfigException($"{SkillTablePath}：技能 {skill.Id} 属于的职业「{skill.ClassId}」不在职业表里");
            }
            if (skill.Kind == SkillKind.Resurrect)
            {
                resurrectPerClass.TryGetValue(skill.ClassId, out var n);
                resurrectPerClass[skill.ClassId] = n + 1;
            }
        }

        foreach (var pair in resurrectPerClass)
        {
            if (pair.Value > 1)
            {
                throw new ConfigException($"{SkillTablePath}：职业 {pair.Key} 的复活技能只能有一个");
            }
        }

        foreach (var cls in Classes.All)
        {
            foreach (var id in cls.Skills)
            {
                if (!Skills.TryGet(id, out var skill))
                {
                    throw new ConfigException($"{ClassTablePath}：职业 {cls.Id} 列的技能 {id} 不在技能表里");
                }
                if (skill.ClassId != cls.Id)
                {
                    throw new ConfigException(
                        $"{ClassTablePath}：技能 {id} 属于 {skill.ClassId}，不在职业 {cls.Id} 名下");
                }
            }

            if (cls.Skills.Distinct().Count() != cls.Skills.Count)
            {
                throw new ConfigException($"{ClassTablePath}：职业 {cls.Id} 的技能列表有重复");
            }
        }
    }

    /// <summary>直线距离，取整到毫米。坐标本来就是以毫米为单位的整数。</summary>
    public static uint DistanceMm(PointConfig a, PointConfig b)
    {
        var dx = (long)a.X - b.X;
        var dy = (long)a.Y - b.Y;
        var squared = (double)dx * dx + (double)dy * dy;
        return (uint)Math.Round(Math.Sqrt(squared));
    }

    sealed class RawConfig
    {
        public string Listen { get; init; } = "";
        public string SettlementDir { get; init; } = "";
        public string CharacterDir { get; init; } = "var/battle/characters";
        public string SkillTable { get; init; } = "skills.json";
        public string ClassTable { get; init; } = "classes.json";
        public string DungeonTable { get; init; } = "dungeons.json";
        public string ArenaMatchTable { get; init; } = "";
        public string BattlefieldTable { get; init; } = "";
        public string DefaultClass { get; init; } = "warrior";
        public string ExpUpstream { get; init; } = "";

        /// <summary>角色服地址；留空按 exp_upstream 走。</summary>
        public string CharacterUpstream { get; init; } = "";
        public CasterConfig Caster { get; init; } = new();
        public BossConfig Boss { get; init; } = new();
        public ArenaConfig Arena { get; init; } = new();
    }
}

/// <summary>两个配置文件共用一套读法：snake_case 对齐 JSON，允许注释和尾逗号。</summary>
static class ConfigJson
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static T Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
                   ?? throw new ConfigException($"{path}：内容为空");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{path}：JSON 读不了，{ex.Message}");
        }
        catch (IOException ex)
        {
            throw new ConfigException($"{path}：文件读不了，{ex.Message}");
        }
    }
}
