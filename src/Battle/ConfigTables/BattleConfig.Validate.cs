using System.Text.Json;
using System.Text.Json.Serialization;

namespace Battle;

public sealed partial class BattleConfig
{

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

        if (Boss.Defense < 0)
        {
            throw new ConfigException($"{SourcePath}：boss.defense 不能是负数");
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
        ValidateDungeonBossBindings();

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

    /// <summary>
    /// B15：副本与首领的绑定校验。dungeons.json 的 boss_id 指向 monsters.json 的
    /// tier=boss 条目；不搬数值，也不许两条副本共用同一只 Boss（否则「五个副本各一个
    /// Boss」在内容层对不上）。没写 boss_id 的副本（等境界开关的元婴深渊）不校验。
    /// </summary>
    void ValidateDungeonBossBindings()
    {
        if (Dungeons is null)
        {
            return;
        }

        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dungeon in Dungeons.All)
        {
            var bossId = dungeon.BossId;
            if (string.IsNullOrWhiteSpace(bossId))
            {
                continue;
            }

            var monster = Monsters?.Find(bossId);
            if (monster is null)
            {
                throw new ConfigException(
                    $"{SourcePath}：副本 {dungeon.Id} 的 boss_id「{bossId}」不在怪物表里"
                    + "（config/monsters.json）");
            }
            if (!monster.IsBoss)
            {
                throw new ConfigException(
                    $"{SourcePath}：副本 {dungeon.Id} 的 boss_id「{bossId}」档位是 {monster.Tier}，"
                    + "必须是 boss");
            }
            if (claimed.TryGetValue(bossId, out var other))
            {
                throw new ConfigException(
                    $"{SourcePath}：副本 {other} 与 {dungeon.Id} 绑了同一只 Boss「{bossId}」");
            }
            claimed[bossId] = dungeon.Id;
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
}
