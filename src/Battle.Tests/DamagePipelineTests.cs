namespace Battle.Tests;

/// <summary>
/// D6：四段伤害的纯计算单测。不连库、不起服，读的是仓库正本 config/effects.json。
/// 期望值全部从表里现取（k / 下限 / 暴伤 / 随机端点），不写死当时那一份数字。
/// </summary>
public sealed class DamagePipelineTests
{
    static EffectTable Table => EffectTable.Load(TestConfig.Real("effects.json"));

    /// <summary>效果表缺失时退回恒等：攻击 × 技能倍率，不减伤、不暴击、不随机。</summary>
    [Fact]
    public void Missing_table_falls_back_to_identity()
    {
        var stages = DamagePipeline.Compute(
            EffectTable.Missing, new DamageInput(120, 0.6, 100), new Random(7));

        Assert.Equal(120, stages.Base, 9);
        Assert.Equal(120, stages.Enhanced, 9);
        Assert.Equal(72, stages.Skill, 9);
        Assert.Equal(72, stages.Final, 9);
        Assert.Equal(72, stages.Rounded);
        Assert.False(stages.Crit);
        Assert.Equal(1.0, stages.DefenseMitigation, 9);

        // 恒等不看防御：防御再高也还是攻击 × 倍率（Q2 之前的老链路）。
        var armored = DamagePipeline.Compute(
            EffectTable.Missing, new DamageInput(120, 0.6, 999_999), new Random(7));
        Assert.Equal(72, armored.Rounded);
    }

    /// <summary>四段顺序：基础 → 强化 → 技能 → 最终；中间保留小数，只在最后一处取整。</summary>
    [Fact]
    public void Four_stages_follow_the_documented_order()
    {
        var table = Table;
        var damage = table.Damage;
        var input = new DamageInput(
            Attack: 200,
            SkillMultiplier: 0.6,
            TargetDefense: 100,
            RealmBonusPct: 1000,
            AttackPct: 20,
            EnhanceCoef: 0.1,
            CrystalFlatAttack: 50,
            ForcedRandomPct: 100);

        var stages = DamagePipeline.Compute(table, input, new Random(7));

        // ① 基础 = 攻击 ×(1+攻击百分比) ×(1+境界加成)；② 强化 = 基础 ×(1+强化系数+结晶固定/基础)。
        var attack = 200 * 1.2;
        var expectedBase = attack * 1.1;
        var expectedEnhanced = expectedBase * (1 + 0.1 + 50 / expectedBase);
        var expectedSkill = expectedEnhanced * 0.6;

        Assert.Equal(240, attack, 9);
        Assert.Equal(264, expectedBase, 9);
        Assert.Equal(340.4, expectedEnhanced, 9);

        Assert.Equal(expectedBase, stages.Base, 9);
        Assert.Equal(expectedEnhanced, stages.Enhanced, 9);
        Assert.Equal(expectedSkill, stages.Skill, 9);

        // ③ 中间不取整：技能段留着 0.24 的小数。
        Assert.True(Math.Abs(stages.Skill - Math.Round(stages.Skill)) > 1e-9);

        // ④ 最终 = 技能 × 防御减免 × 暴伤 × 随机 × 抗性；这里的随机被钉成 100%。
        var mitigation = Math.Clamp(1 - 100.0 / (100 + damage.DefenseK), damage.DefenseMinPct / 100.0, 1.0);
        var critFactor = stages.Crit ? 1 + damage.CritDamagePct / 100.0 : 1.0;
        var expectedFinal = expectedSkill * mitigation * critFactor;

        Assert.Equal(mitigation, stages.DefenseMitigation, 9);
        Assert.Equal(expectedFinal, stages.Final, 9);
        Assert.Equal((long)Math.Round(expectedFinal, MidpointRounding.AwayFromZero), stages.Rounded);
        // 只取整一次：Rounded 是 Final 的整数版，不是把中间值先取整再乘出来的。
        Assert.True(Math.Abs(stages.Final - stages.Rounded) > 1e-9);
    }

    /// <summary>B10 的守卫：没有强化 / 结晶输入时，第二段与第一段逐字相等（不双算）。</summary>
    [Fact]
    public void Second_stage_is_identity_without_enhance_inputs()
    {
        var stages = DamagePipeline.Compute(
            Table,
            new DamageInput(Attack: 200, SkillMultiplier: 0.6, TargetDefense: 100, ForcedRandomPct: 100),
            new Random(7));

        Assert.Equal(stages.Base, stages.Enhanced, 9);
    }

    /// <summary>基础 &lt;= 0 时第二段按 0 处理，不除零、不出 NaN。</summary>
    [Fact]
    public void Non_positive_base_does_not_divide_by_zero()
    {
        var stages = DamagePipeline.Compute(
            Table,
            new DamageInput(
                Attack: 0, SkillMultiplier: 0.6, TargetDefense: 100,
                EnhanceCoef: 0.1, CrystalFlatAttack: 50, ForcedRandomPct: 100),
            new Random(7));

        Assert.Equal(0, stages.Base, 9);
        Assert.Equal(0, stages.Enhanced, 9);
        Assert.Equal(0, stages.Rounded);
        Assert.False(double.IsNaN(stages.Final));
        Assert.False(double.IsInfinity(stages.Final));
    }

    /// <summary>防御减免 = clamp(1 - 有效防御/(有效防御+k), min_pct, 100)；防御越高伤害越低。</summary>
    [Fact]
    public void Defense_mitigation_clamps_to_the_configured_floor()
    {
        var table = Table;
        var k = table.Damage.DefenseK;

        var none = DamagePipeline.Compute(table, new DamageInput(200, 0.6, 0, ForcedRandomPct: 100), new Random(7));
        var equal = DamagePipeline.Compute(table, new DamageInput(200, 0.6, k, ForcedRandomPct: 100), new Random(7));
        var huge = DamagePipeline.Compute(
            table, new DamageInput(200, 0.6, (k + 1) * 1_000_000, ForcedRandomPct: 100), new Random(7));

        Assert.Equal(1.0, none.DefenseMitigation, 9);
        Assert.Equal(0.5, equal.DefenseMitigation, 9);
        Assert.Equal(table.Damage.DefenseMinPct / 100.0, huge.DefenseMitigation, 9);
        Assert.True(none.Final > equal.Final);
        Assert.True(equal.Final > huge.Final);
    }

    /// <summary>穿透按 cap_pct 夹取：超过上限的值不再多穿，防御被穿光时减免为 1。</summary>
    [Fact]
    public void Penetration_clamps_to_the_configured_cap()
    {
        var table = Table;
        var damage = table.Damage;

        var atCap = DamagePipeline.Compute(
            table, new DamageInput(200, 0.6, 100, PenetrationPct: damage.PenetrationCapPct, ForcedRandomPct: 100),
            new Random(7));
        var beyond = DamagePipeline.Compute(
            table,
            new DamageInput(200, 0.6, 100, PenetrationPct: damage.PenetrationCapPct + 500, ForcedRandomPct: 100),
            new Random(7));

        Assert.Equal(atCap.DefenseMitigation, beyond.DefenseMitigation, 9);

        var cap = damage.PenetrationEnabled ? Math.Clamp(damage.PenetrationCapPct, 0, damage.PenetrationCapPct) : 0;
        var effective = Math.Max(0, 100 * (1 - cap / 100.0));
        var expected = Math.Clamp(
            1 - effective / (effective + damage.DefenseK), damage.DefenseMinPct / 100.0, 1.0);
        Assert.Equal(expected, atCap.DefenseMitigation, 9);
    }

    /// <summary>抗性按 cap_pct 夹取，并且只减不多。</summary>
    [Fact]
    public void Resistance_clamps_to_the_configured_cap()
    {
        var table = Table;
        var damage = table.Damage;
        var withResist = new DamageInput(200, 0.6, 100, ResistancePct: damage.ResistanceCapPct + 500, ForcedRandomPct: 100);

        var capped = DamagePipeline.Compute(table, withResist, new Random(7));
        var none = DamagePipeline.Compute(table, withResist with { ResistancePct = 0 }, new Random(7));

        var cap = damage.ResistanceEnabled ? Math.Clamp(withResist.ResistancePct, 0, damage.ResistanceCapPct) : 0;
        var critFactor = capped.Crit ? 1 + damage.CritDamagePct / 100.0 : 1.0;
        var expected = capped.Skill * capped.DefenseMitigation * critFactor * (1 - cap / 100.0);

        Assert.Equal(expected, capped.Final, 9);
        Assert.True(capped.Final <= none.Final + 1e-9);
    }

    /// <summary>强制随机百分比：负值按 0（这一刀打空），端点按比例缩放到最终伤害。</summary>
    [Fact]
    public void Forced_random_is_floored_at_zero()
    {
        var table = Table;

        var whiff = DamagePipeline.Compute(
            table, new DamageInput(200, 0.6, 0, ForcedRandomPct: -50), new Random(7));
        Assert.Equal(0, whiff.Final, 9);
        Assert.Equal(0, whiff.Rounded);

        var half = DamagePipeline.Compute(
            table, new DamageInput(200, 0.6, 0, ForcedRandomPct: 50), new Random(7));
        var full = DamagePipeline.Compute(
            table, new DamageInput(200, 0.6, 0, ForcedRandomPct: 100), new Random(7));
        Assert.Equal(full.Final / 2, half.Final, 9);
    }

    /// <summary>final_round_half_up：.5 一律进，不用银行家舍入（0.5→1、1.5→2、2.5→3）。</summary>
    [Fact]
    public void Final_rounds_half_up_not_bankers()
    {
        Assert.Equal(1, DamagePipeline.Identity(new DamageInput(1, 0.5, 0)).Rounded);
        Assert.Equal(2, DamagePipeline.Identity(new DamageInput(3, 0.5, 0)).Rounded);
        Assert.Equal(3, DamagePipeline.Identity(new DamageInput(5, 0.5, 0)).Rounded);
    }

    /// <summary>整条链上 Rounded 都得等于 Final 的半点进位，不能有第二套取整。</summary>
    [Fact]
    public void Rounded_matches_final_rounding_across_inputs()
    {
        var table = Table;
        for (var attack = 1; attack <= 40; attack++)
        {
            var stages = DamagePipeline.Compute(
                table, new DamageInput(attack, 0.5, 0, ForcedRandomPct: 100), new Random(attack));
            Assert.Equal((long)Math.Round(stages.Final, MidpointRounding.AwayFromZero), stages.Rounded);
        }
    }

    /// <summary>暴击只在 rate_pct &gt; 0 时可能发生；配置是 0 时 50 次采样一次都不暴。</summary>
    [Fact]
    public void Crit_only_happens_when_the_config_rate_is_positive()
    {
        var table = Table;
        if (table.Damage.CritRatePct > 0)
        {
            return; // 有了暴击来源之后由那条卡补正例，这里只挡「0 也暴击」。
        }

        for (var seed = 0; seed < 50; seed++)
        {
            var stages = DamagePipeline.Compute(
                table, new DamageInput(200, 0.6, 0, ForcedRandomPct: 100), new Random(seed));
            Assert.False(stages.Crit);
        }
    }
}
