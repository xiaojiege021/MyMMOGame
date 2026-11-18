namespace Battle;

/// <summary>
/// 四段伤害的输入。属性都来自战斗快照（职业表 / 竞技场模板 / 战场压缩后 / 城战全额），
/// 这里不另读一份配置；境界加成来自 battle_profile.passive_pct_bps。
/// 强化系数与结晶固定值现在恒为 0（B10 才接），乘区结构已经在。
/// </summary>
public sealed record DamageInput(
    double Attack,
    double SkillMultiplier,
    double TargetDefense,
    double RealmBonusPct = 0,
    double AttackPct = 0,
    double DefensePct = 0,
    double PenetrationPct = 0,
    double ResistancePct = 0,
    double EnhanceCoef = 0,
    double CrystalFlatAttack = 0,
    double CounterCoef = 1.0,
    int? ForcedRandomPct = null);

/// <summary>四段中间值与最后一次取整后的结果。中间全程 double，只在 Rounded 取整一次。</summary>
public sealed record DamageStages(
    double Base,
    double Enhanced,
    double Skill,
    double Final,
    long Rounded,
    bool Crit,
    double DefenseMitigation);

/// <summary>
/// 方案 7.2 的四段：基础 → 强化 → 技能 → 最终。全部乘区，顺序按方案写死；
/// 先取整再乘会差出可见的数，所以只有最后一处四舍五入（final_round_half_up）。
/// 穿透 / 抗性是第 4 段的两个子因子，默认 0 时与方案逐字等价。
/// </summary>
public static class DamagePipeline
{
    /// <summary>
    /// 效果表缺失时（老卡临时配置目录没有 effects.json）的恒等算法：
    /// 攻击 × 技能倍率，不暴击、不随机、不减伤。与 Q2 之前逐字一致。
    /// </summary>
    public static DamageStages Identity(DamageInput input)
    {
        var final = input.Attack * input.SkillMultiplier;
        return new DamageStages(
            input.Attack, input.Attack, final, final,
            RoundHalfUp(final), false, 1.0);
    }

    public static DamageStages Compute(EffectTable table, DamageInput input, Random rng)
    {
        if (table.IsMissing)
        {
            return Identity(input);
        }

        var damage = table.Damage;

        // ① 基础攻击 = (主属性 × 系数 + 装备固定) × (1 + 境界加成)。
        // Attack 已经是括号里那一截；攻击力百分比来自 buff / debuff，加在括号之后。
        var attack = input.Attack * (1 + input.AttackPct / 100.0);
        var baseAttack = attack * (1 + input.RealmBonusPct / 10000.0);

        // ② 强化后 = 基础 × (1 + 强化系数 + 结晶固定 / 基础)。
        // 基础 <= 0 时整项按 0 处理，不除零（Q1 定的守卫）。
        var enhanced = baseAttack <= 0
            ? 0
            : baseAttack * (1 + input.EnhanceCoef + input.CrystalFlatAttack / baseAttack);

        // ③ 技能伤害 = 强化后 × 技能倍率 × 克制。克制表等 B15，现在恒 1。
        var skillDamage = enhanced * input.SkillMultiplier * input.CounterCoef;

        // ④ 最终 = 技能 × 防御减免 × 暴伤判定 × 随机 × (1 - 抗性)。
        var defense = Math.Max(0, input.TargetDefense * (1 + input.DefensePct / 100.0));
        var penetration = damage.PenetrationEnabled
            ? Math.Clamp(input.PenetrationPct, 0, damage.PenetrationCapPct)
            : 0;
        var effectiveDefense = Math.Max(0, defense * (1 - penetration / 100.0));
        var mitigation = Mitigation(effectiveDefense, damage.DefenseK, damage.DefenseMinPct);

        var crit = damage.CritRatePct > 0 && rng.NextDouble() * 100 < damage.CritRatePct;
        var critFactor = crit ? 1 + damage.CritDamagePct / 100.0 : 1.0;

        var randomPct = input.ForcedRandomPct
                        ?? damage.RandomMinPct + rng.NextDouble() * (damage.RandomMaxPct - damage.RandomMinPct);
        var randomFactor = Math.Max(0, randomPct) / 100.0;

        var resistance = damage.ResistanceEnabled
            ? Math.Clamp(input.ResistancePct, 0, damage.ResistanceCapPct)
            : 0;
        var resistanceFactor = 1 - resistance / 100.0;

        var final = skillDamage * mitigation * critFactor * randomFactor * resistanceFactor;
        if (final < 0 || double.IsNaN(final))
        {
            final = 0;
        }

        return new DamageStages(
            baseAttack, enhanced, skillDamage, final, RoundHalfUp(final), crit, mitigation);
    }

    /// <summary>防御减免 = clamp(1 - 有效防御 / (有效防御 + K), 下限, 1)。K 为 0 时不除零。</summary>
    static double Mitigation(double effectiveDefense, double k, double minPct)
    {
        var floor = Math.Clamp(minPct, 0, 100) / 100.0;
        if (effectiveDefense <= 0)
        {
            return 1.0;
        }
        var denominator = effectiveDefense + k;
        if (denominator <= 0)
        {
            return floor;
        }
        return Math.Clamp(1 - effectiveDefense / denominator, floor, 1.0);
    }

    /// <summary>final_round_half_up：.5 一律进，不用银行家舍入。</summary>
    static long RoundHalfUp(double value) =>
        (long)Math.Round(value, MidpointRounding.AwayFromZero);
}
