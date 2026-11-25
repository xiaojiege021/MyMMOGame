using System.Text.Json;
using System.Text.Json.Serialization;

namespace Battle;

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

    /// <summary>
    /// S1：这条技能是哪个门派的路数（天地玄黄 / 法则神通）。只做归属标注，
    /// 不参与下发与结算；门派与阶的名字只在那张配置表里，这里不留第二份。
    /// </summary>
    public string School { get; init; } = "";

    /// <summary>门派内的阶（黄 / 玄 / 地 / 天）。值域由门派自己在配置里列，代码只查命中。</summary>
    public string Tier { get; init; } = "";

    /// <summary>
    /// 挂到哪条分支。空表示门派通用（不看分支谁都能用）；
    /// 非空只是归属标注，战斗服仍按 class_id 下发三个技能，不按分支过滤。
    /// </summary>
    public string Branch { get; init; } = "";

    /// <summary>
    /// B6：觉醒技。true 表示这条不在「按职业下发的三条」里——ForClass 不下发它，
    /// 只有本分支觉醒之后由开场包按快照里的 awaken_skill_id 额外追加一条。
    /// 归属仍按门派与阶走通用校验（现在是法则神通的 tian 阶），这里不写死派名。
    /// </summary>
    public bool Awaken { get; init; }

    public SkillKind Kind { get; init; }

    public double DamageMultiplier { get; init; }

    /// <summary>蓝耗。普攻可以配 0，主动技能配了就要检查蓝量。</summary>
    public int ManaCost { get; init; }

    public uint RangeMm { get; init; }
    public int ForeswingMs { get; init; }
    public int RecoveryMs { get; init; }
    public int CooldownMs { get; init; }
    public int StaggerMs { get; init; }

    /// <summary>
    /// Q2：这一手结算时对目标施加的效果。空表示只有伤害 / 回复，不带效果。
    /// 只引用 config/effects.json 的编号，不在这里抄时长与数值；
    /// B1 / B15 铺技能内容时只填这一段，运行时不动。
    /// </summary>
    public List<EffectSpec> Effects { get; init; } = [];
}

/// <summary>
/// 一个门派（S1）：天地玄黄是承露门教的低阶技能，法则神通是圣地与焦原教的高阶技能。
/// 门派只在这张配置表里定义，代码不写死名字；<see cref="Tiers"/> 是这一派允许的阶，
/// 留空表示这一派暂时没有技能（法则神通要等二转与觉醒卡）。
/// </summary>
public sealed class SkillSchool
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>谁教这一派：承露门，或明霄圣地 / 焦原。</summary>
    public string Giver { get; init; } = "";

    public List<SkillTier> Tiers { get; init; } = [];
}

public sealed class SkillTier
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class SkillTable
{
    readonly Dictionary<uint, SkillDef> _byId;

    SkillTable(int version, Dictionary<uint, SkillDef> byId, List<SkillSchool> schools)
    {
        Version = version;
        _byId = byId;
        Schools = schools;
    }

    public int Version { get; }

    /// <summary>门派定义。老临时配置没有这一段时是空的，此时不校验技能的归属。</summary>
    public IReadOnlyList<SkillSchool> Schools { get; }

    public IReadOnlyCollection<SkillDef> All => _byId.Values;

    public bool TryGet(uint id, out SkillDef def) => _byId.TryGetValue(id, out def!);

    /// <summary>
    /// 按职业下发的技能：觉醒技不在其中（B6）。没有 awaken=true 技能时结果与
    /// v3 逐字相同，老卡的行为不变。
    /// </summary>
    public IReadOnlyList<SkillDef> ForClass(string classId) =>
        _byId.Values.Where(s => s.ClassId == classId && !s.Awaken).OrderBy(s => s.Id).ToList();

    /// <summary>B6：这条分支的觉醒技；没有觉醒技的分支回 null。</summary>
    public SkillDef? AwakenFor(string branchId) =>
        _byId.Values.FirstOrDefault(s => s.Awaken && s.Branch == branchId);

    public IReadOnlyList<SkillDef> ForSchool(string schoolId) =>
        _byId.Values.Where(s => s.School == schoolId).OrderBy(s => s.Id).ToList();

    public IReadOnlyList<SkillDef> ForBranch(string branchId) =>
        _byId.Values.Where(s => s.Branch == branchId).OrderBy(s => s.Id).ToList();

    public static SkillTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"技能表不存在：{path}");
        }

        var file = ConfigJson.Read<SkillFile>(path);

        // 门派段：可选的。写了就逐派查 id / 名字 / 阶，并且下面每条技能都必须命中自己那一派。
        var schools = new List<SkillSchool>();
        var tiersOf = new Dictionary<string, HashSet<string>>();
        foreach (var school in file.Schools)
        {
            if (string.IsNullOrWhiteSpace(school.Id))
            {
                throw new ConfigException($"{path}：门派缺 id");
            }
            if (!IsConfigId(school.Id))
            {
                throw new ConfigException($"{path}：门派 id「{school.Id}」只能是小写字母、数字与下划线，且以字母开头");
            }
            if (string.IsNullOrWhiteSpace(school.Name))
            {
                throw new ConfigException($"{path}：门派 {school.Id} 没有名字");
            }
            if (!tiersOf.TryAdd(school.Id, []))
            {
                throw new ConfigException($"{path}：门派 {school.Id} 重复");
            }
            foreach (var tier in school.Tiers)
            {
                if (string.IsNullOrWhiteSpace(tier.Id) || string.IsNullOrWhiteSpace(tier.Name))
                {
                    throw new ConfigException($"{path}：门派 {school.Id} 的阶缺 id 或名字");
                }
                if (!tiersOf[school.Id].Add(tier.Id))
                {
                    throw new ConfigException($"{path}：门派 {school.Id} 的阶 {tier.Id} 重复");
                }
            }
            schools.Add(school);
        }

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
            // 门派与分支是归属，不是玩法：填了就查得到，没填就按「没写」放过。
            // 老卡（P0-3a 等）自带的临时技能表没有这两格，照样要能起。
            if (schools.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(skill.School))
                {
                    throw new ConfigException($"{path}：技能 {skill.Id} 没写属于哪个门派");
                }
                if (!tiersOf.ContainsKey(skill.School))
                {
                    throw new ConfigException($"{path}：技能 {skill.Id} 的门派「{skill.School}」不在门派表里");
                }
                if (string.IsNullOrWhiteSpace(skill.Tier))
                {
                    throw new ConfigException($"{path}：技能 {skill.Id} 没写是哪一阶");
                }
                if (!tiersOf[skill.School].Contains(skill.Tier))
                {
                    throw new ConfigException(
                        $"{path}：技能 {skill.Id} 的阶「{skill.Tier}」不在门派 {skill.School} 的阶表里");
                }
            }
            if (skill.Branch.Length > 0 && !IsConfigId(skill.Branch))
            {
                throw new ConfigException(
                    $"{path}：技能 {skill.Id} 的分支「{skill.Branch}」只能是小写字母、数字与下划线，且以字母开头");
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

        // B6：觉醒技的归属。同一分支最多一条（否则「觉醒解锁哪一条」没有唯一答案），
        // 并且必须挂分支（不挂分支就不知道该发给谁）。
        var awakenByBranch = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var def in byId.Values.Where(d => d.Awaken))
        {
            if (def.Branch.Length == 0)
            {
                throw new ConfigException($"{path}：觉醒技 {def.Id} 必须挂一条分支");
            }
            if (!awakenByBranch.TryAdd(def.Branch, def.Id))
            {
                throw new ConfigException(
                    $"{path}：分支 {def.Branch} 有两条觉醒技（{awakenByBranch[def.Branch]} / {def.Id}）");
            }
        }

        if (file.Version <= 0)
        {
            throw new ConfigException($"{path}：version 必须大于 0");
        }

        return new SkillTable(file.Version, byId, schools);
    }

    /// <summary>
    /// 配置里的编号形状：小写字母开头，只含小写字母、数字与下划线。
    /// 只查形状，不列白名单——分支 / 门派的名字只在配置里，代码不留第二份。
    /// </summary>
    internal static bool IsConfigId(string text)
    {
        if (text.Length == 0 || text[0] < 'a' || text[0] > 'z')
        {
            return false;
        }
        foreach (var c in text)
        {
            if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_')
            {
                return false;
            }
        }
        return true;
    }

    sealed class SkillFile
    {
        public int Version { get; init; }

        /// <summary>门派段是可选的：老临时配置没有这一格。</summary>
        public List<SkillSchool> Schools { get; init; } = [];

        public List<SkillDef> Skills { get; init; } = [];
    }
}

/// <summary>牧师的复活：拉起倒地队友，回一部分血；被拉的人只扣当前等级已有经验的一部分。</summary>
