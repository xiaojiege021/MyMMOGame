using System.Text.Json;
using System.Text.Json.Serialization;

namespace Battle;

public sealed partial class BattleConfig
{

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
        var effectPath = Resolve(dir, raw.EffectTable, "effects.json");
        var monsterPath = Resolve(dir, raw.MonsterTable, "monsters.json");
        var classPath = Resolve(dir, raw.ClassTable, "classes.json");
        var dungeonPath = Resolve(dir, raw.DungeonTable, "dungeons.json");
        var dungeonDropPath = Resolve(dir, raw.DungeonDropTable, "dungeon_drops.json");
        var arenaMatchPath = Resolve(dir, raw.ArenaMatchTable, "arena_match.json");
        var battlefieldPath = Resolve(dir, raw.BattlefieldTable, "battlefield.json");
        var siegePath = Resolve(dir, raw.SiegeTable, "siege.json");
        var sectTechPath = Resolve(dir, raw.SectTechTable, "sect_tech.json");
        var siegeTerritoryPath = Resolve(dir, raw.SiegeTerritoryTable, "siege_territory.json");
        var worldBossPath = Resolve(dir, raw.WorldBossTable, "world_boss.json");
        // Q11：日历表是窗口时刻的解释口径。临时配置目录只指了 world_boss_table
        // 却没带 calendar.json 时，退回仓库根那份正本（各进程都从仓库根启动，
        // 与网关的 DefaultCalendarTable 同一条约定）；显式写了路径却缺文件照样起不来。
        var calendarPath = Resolve(dir, raw.CalendarTable, "calendar.json");
        if (!File.Exists(calendarPath) && string.IsNullOrWhiteSpace(raw.CalendarTable))
        {
            calendarPath = Path.Combine("config", "calendar.json");
        }
        // Q9：断线重连表同样可选。老卡的临时配置目录没有 reconnect.json 时按
        // 「断线即离场、不重连、不惩罚」跑；显式写了路径却缺文件照样起不来。
        var reconnectPath = Resolve(dir, raw.ReconnectTable, "reconnect.json");
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
        // 城战表同理：config/battle.json 里不显式登记 siege_table，靠默认文件名 +
        // 文件存在性判断。老卡的临时配置目录没有 siege.json，缺表只让城战一律拒，
        // 副本 / 练习场 / 竞技场 / 战场照跑；显式写了路径却缺文件则照样起不来。
        var siege = File.Exists(siegePath)
            ? SiegeTable.Load(siegePath)
            : string.IsNullOrWhiteSpace(raw.SiegeTable)
                ? SiegeTable.Missing()
                : throw new ConfigException($"{path}：siege_table 指的 {siegePath} 不存在");
        // 宗门科技表同理：config/battle.json 不显式登记 sect_tech_table，靠默认文件名 +
        // 文件存在性判断。老卡临时配置目录里没有这份表，缺表只让科技恒 0，
        // 副本 / 练习场 / 竞技场 / 战场 / 城战照常开场；显式写了路径却缺文件照样起不来。
        var sectTech = File.Exists(sectTechPath)
            ? SectTechTable.Load(sectTechPath)
            : string.IsNullOrWhiteSpace(raw.SectTechTable)
                ? SectTechTable.Missing()
                : throw new ConfigException($"{path}：sect_tech_table 指的 {sectTechPath} 不存在");
        // 领地表同理：config/battle.json 不显式登记 siege_territory_table，靠默认文件名 +
        // 文件存在性判断。老卡的临时配置目录没有这份表，缺表只让城战「不结束、不写回」，
        // 副本 / 练习场 / 竞技场 / 战场 / 老城战断言照跑；显式写了路径却缺文件照样起不来。
        var siegeTerritory = File.Exists(siegeTerritoryPath)
            ? SiegeTerritoryTable.Load(siegeTerritoryPath)
            : string.IsNullOrWhiteSpace(raw.SiegeTerritoryTable)
                ? SiegeTerritoryTable.Missing()
                : throw new ConfigException(
                    $"{path}：siege_territory_table 指的 {siegeTerritoryPath} 不存在");

        // 世界 Boss 表同理：config/battle.json 不显式登记 world_boss_table，靠默认文件名 +
        // 文件存在性判断。老卡的临时配置目录没有这份表，缺表只让带 worldboss 的连接一律拒，
        // 别的模式照跑；显式写了路径却缺文件照样起不来。
        var calendar = CalendarTable.Load(calendarPath);

        var worldBoss = File.Exists(worldBossPath)
            ? WorldBossTable.Load(worldBossPath)
            : string.IsNullOrWhiteSpace(raw.WorldBossTable)
                ? WorldBossTable.Missing()
                : throw new ConfigException($"{path}：world_boss_table 指的 {worldBossPath} 不存在");

        // K6：怪物表的技能编号要落在这份技能表里。缺表按「没有怪物」处理，
        // 老卡的临时配置目录不受影响；显式写了路径却缺文件仍然起不来。
        var skills = SkillTable.Load(skillPath);

        var reconnect = File.Exists(reconnectPath)
            ? ReconnectTable.Load(reconnectPath)
            : string.IsNullOrWhiteSpace(raw.ReconnectTable)
                ? ReconnectTable.Missing
                : throw new ConfigException($"{path}：reconnect_table 指的 {reconnectPath} 不存在");

        // Q2：效果表是可选表。配置目录里没有它就按恒等伤害跑（老卡的临时配置目录
        // 一个文件都不用改）；显式写了路径却缺文件照样起不来。技能里写了 effect 块
        // 却没有效果表，属于内容接错了，起不来，不静默丢效果。
        var effects = File.Exists(effectPath)
            ? EffectTable.Load(effectPath)
            : string.IsNullOrWhiteSpace(raw.EffectTable)
                ? EffectTable.Missing
                : throw new ConfigException($"{path}：effect_table 指的 {effectPath} 不存在");
        foreach (var skill in skills.All)
        {
            foreach (var spec in skill.Effects)
            {
                if (spec.Target is not ("self" or "target"))
                {
                    throw new ConfigException(
                        $"{path}：技能 {skill.Id} 的效果目标「{spec.Target}」只认 self / target");
                }
                if (spec.Stacks < 1)
                {
                    throw new ConfigException($"{path}：技能 {skill.Id} 的效果层数必须 >= 1");
                }
                if (effects.IsMissing)
                {
                    throw new ConfigException(
                        $"{path}：技能 {skill.Id} 引用了效果 {spec.EffectId}，但没有 effect_table");
                }
                if (!effects.TryGet(spec.EffectId, out _))
                {
                    throw new ConfigException(
                        $"{path}：技能 {skill.Id} 引用的效果 {spec.EffectId} 不在效果表里");
                }
            }
        }

        var monsters = File.Exists(monsterPath)
            ? MonsterTable.Load(monsterPath, skills.All.Select(x => x.Id.ToString()).ToList())
            : string.IsNullOrWhiteSpace(raw.MonsterTable)
                ? null
                : throw new ConfigException($"{path}：monster_table 指的 {monsterPath} 不存在");

        var config = new BattleConfig
        {
            Listen = raw.Listen,
            // 结算与角色目录按当前工作目录解析，约定从仓库根启动。
            // 启动日志会打绝对路径，跑错目录一眼能看出来。
            SettlementDir = Path.GetFullPath(raw.SettlementDir),
            CharacterDir = Path.GetFullPath(raw.CharacterDir),
            SkillTablePath = skillPath,
            ClassTablePath = classPath,
            MonsterTablePath = monsterPath,
            ArenaMatchTablePath = arenaMatchPath,
            SiegeTablePath = siegePath,
            SectTechTablePath = sectTechPath,
            SiegeTerritoryTablePath = siegeTerritoryPath,
            WorldBossTablePath = worldBossPath,
            CalendarPath = calendarPath,
            Calendar = calendar,
            WorldUpstream = (raw.WorldUpstream ?? "").Trim(),
            SocialUpstream = (raw.SocialUpstream ?? "").Trim(),
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
            Skills = skills,
            Effects = effects,
            EffectTablePath = effectPath,
            Classes = ClassTable.Load(classPath),
            Monsters = monsters,
            Dungeons = DungeonTable.Load(dungeonPath),
            DungeonDrops = DungeonDropTable.LoadIfPresent(dungeonDropPath),
            ArenaMatch = arenaMatch,
            Battlefield = battlefield,
            Siege = siege,
            SectTech = sectTech,
            SiegeTerritory = siegeTerritory,
            WorldBoss = worldBoss,
            Reconnect = reconnect,
            ReconnectTablePath = reconnectPath,
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
}
