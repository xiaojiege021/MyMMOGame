using System.Text.Json.Nodes;
using Mmo.Shared.Domain;

namespace Battle.Tests;

/// <summary>
/// D6：配置校验单测。正本起得来；把一处改坏就必定报 ConfigException，
/// 证明「坏配置起不来」是加载器本体的硬校验，不是核对脚本的另一套规则。
/// 临时副本都落在测试输出的 config 目录里，表之间的相对引用照旧解析得到。
/// </summary>
public sealed class EffectTableValidationTests
{
    static JsonNode Real => TestConfig.ReadReal("effects.json");

    static string Mutate(Action<JsonNode> change) => TestConfig.Mutate("effects.json", change);

    static void AssertRejected(string path, string because)
    {
        try
        {
            var ex = Assert.Throws<ConfigException>(() => EffectTable.Load(path));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message), because + "：错误消息不能是空的");
        }
        finally
        {
            TestConfig.Cleanup(path);
        }
    }

    [Fact]
    public void Real_config_loads_and_matches_the_file()
    {
        var table = EffectTable.Load(TestConfig.Real("effects.json"));
        var damage = Real["damage"]!;

        Assert.False(table.IsMissing);
        Assert.True(table.Version >= 3);
        Assert.NotEmpty(table.All);

        // 表里的 damage 段与 JSON 现值一致：加载不做二次换算，也不另给默认值。
        Assert.Equal(damage["defense"]!["k"]!.GetValue<double>(), table.Damage.DefenseK, 9);
        Assert.Equal(damage["defense"]!["min_pct"]!.GetValue<double>(), table.Damage.DefenseMinPct, 9);
        Assert.Equal(damage["crit"]!["rate_pct"]!.GetValue<double>(), table.Damage.CritRatePct, 9);
        Assert.Equal(damage["random"]!["min_pct"]!.GetValue<double>(), table.Damage.RandomMinPct, 9);
        Assert.Equal(damage["random"]!["max_pct"]!.GetValue<double>(), table.Damage.RandomMaxPct, 9);
        Assert.Equal(damage["penetration"]!["enabled"]!.GetValue<bool>(), table.Damage.PenetrationEnabled);
        Assert.Equal(damage["resistance"]!["cap_pct"]!.GetValue<double>(), table.Damage.ResistanceCapPct, 9);
    }

    [Fact]
    public void Missing_file_is_rejected()
    {
        var ex = Assert.Throws<ConfigException>(
            () => EffectTable.Load(TestConfig.Real("no-such-effects.json")));
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public void Wrong_schema_is_rejected()
    {
        AssertRejected(Mutate(root => root["schema"] = "effect-table/1"), "schema");
    }

    [Fact]
    public void Old_version_is_rejected()
    {
        AssertRejected(Mutate(root => root["version"] = 2), "version");
    }

    [Fact]
    public void Empty_effects_is_rejected()
    {
        AssertRejected(Mutate(root => root["effects"] = new JsonArray()), "effects 空表");
    }

    [Fact]
    public void Duplicate_effect_id_is_rejected()
    {
        AssertRejected(
            Mutate(root =>
            {
                var effects = root["effects"]!.AsArray();
                effects[1]!["id"] = effects[0]!["id"]!.GetValue<string>();
            }),
            "重复 id");
    }

    [Fact]
    public void Unknown_stack_rule_is_rejected()
    {
        AssertRejected(
            Mutate(root => root["effects"]!.AsArray()[0]!["stack_rule"] = "forever"),
            "stack_rule");
    }

    [Fact]
    public void Zero_duration_is_rejected()
    {
        AssertRejected(
            Mutate(root => root["effects"]!.AsArray()[0]!["duration_ms"] = 0),
            "duration_ms");
    }

    [Fact]
    public void Damage_stages_out_of_order_is_rejected()
    {
        AssertRejected(
            Mutate(root => root["damage"]!["stages"]!.AsArray()[0]!["id"] = "enhance"),
            "四段顺序");
    }

    [Fact]
    public void Unknown_rounding_is_rejected()
    {
        AssertRejected(
            Mutate(root => root["damage"]!["rounding"] = "bankers"),
            "rounding");
    }

    [Fact]
    public void Missing_damage_section_is_rejected()
    {
        AssertRejected(Mutate(root => root.AsObject().Remove("damage")), "缺 damage 段");
    }

    [Fact]
    public void Negative_defense_k_is_rejected()
    {
        AssertRejected(
            Mutate(root => root["damage"]!["defense"]!["k"] = -1),
            "defense.k");
    }

    [Fact]
    public void Crit_rate_over_100_is_rejected()
    {
        AssertRejected(
            Mutate(root => root["damage"]!["crit"]!["rate_pct"] = 101),
            "crit.rate_pct");
    }
}

/// <summary>战斗配置（战斗服启动那一份）的加载与校验。</summary>
public sealed class BattleConfigValidationTests
{
    static string Mutate(Action<JsonNode> change) => TestConfig.Mutate("battle.json", change);

    static void AssertRejected(string path, string because)
    {
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BattleConfig.Load(path));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message), because + "：错误消息不能是空的");
        }
        finally
        {
            TestConfig.Cleanup(path);
        }
    }

    [Fact]
    public void Real_battle_config_loads()
    {
        var config = BattleConfig.Load(TestConfig.Real("battle.json"));

        Assert.True(config.Boss.HpMax > 0);
        Assert.True(config.Boss.Id != 0);
        Assert.NotEmpty(config.Skills.All);
        Assert.NotEmpty(config.Classes.All);
        Assert.True(config.Classes.TryGet(config.DefaultClass, out _));
    }

    [Fact]
    public void Zero_boss_hp_is_rejected()
    {
        AssertRejected(Mutate(root => root["boss"]!["hp_max"] = 0), "boss.hp_max");
    }

    [Fact]
    public void Negative_boss_defense_is_rejected()
    {
        AssertRejected(Mutate(root => root["boss"]!["defense"] = -1), "boss.defense");
    }

    [Fact]
    public void Listen_without_port_is_rejected()
    {
        AssertRejected(Mutate(root => root["listen"] = "127.0.0.1"), "listen");
    }

    [Fact]
    public void Unknown_default_class_is_rejected()
    {
        AssertRejected(
            Mutate(root => root["default_class"] = "no_such_class"), "default_class");
    }

    [Fact]
    public void Zero_arena_radius_is_rejected()
    {
        AssertRejected(Mutate(root => root["arena"]!["radius_mm"] = 0), "arena.radius_mm");
    }

    [Fact]
    public void Boss_and_caster_sharing_one_id_is_rejected()
    {
        AssertRejected(
            Mutate(root => root["boss"]!["id"] = root["caster"]!["id"]!.GetValue<ulong>()),
            "boss.id 撞 caster.id");
    }
}
