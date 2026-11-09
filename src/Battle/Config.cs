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
    public long HpMax { get; init; }
    public PointConfig SpawnMm { get; init; } = new();
}

public sealed class BossConfig
{
    public ulong Id { get; init; }
    public string Name { get; init; } = "";
    public long HpMax { get; init; }
    public PointConfig SpawnMm { get; init; } = new();
    public long ExpOnClear { get; init; }
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

/// <summary>
/// 一个技能的全部数字。这里不写死任何数值，技术架构第 7 节要求技能走配置。
/// 前摇是施法到结算的时长，后摇是结算后不能再出手的时长，两段分开算。
/// </summary>
public sealed class SkillDef
{
    public uint Id { get; init; }
    public string Name { get; init; } = "";
    public long Damage { get; init; }
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
            if (skill.Damage < 0 || skill.ForeswingMs < 0 || skill.RecoveryMs < 0 ||
                skill.CooldownMs < 0 || skill.StaggerMs < 0)
            {
                throw new ConfigException($"{path}：技能 {skill.Id} 有负数");
            }
            if (skill.ForeswingMs == 0)
            {
                // 前摇为零就等于「按下即结算」，那取消就没有窗口，完成标准里的
                // 「打断后这次施法没有伤害」也就无从谈起。
                throw new ConfigException($"{path}：技能 {skill.Id} 的前摇必须大于 0");
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

/// <summary>一场单人战斗的启动参数。全部来自 config/battle.json。</summary>
public sealed class BattleConfig
{
    public string Listen { get; init; } = "";
    public string SettlementDir { get; init; } = "";
    public string SkillTablePath { get; init; } = "";
    public CasterConfig Caster { get; init; } = new();
    public BossConfig Boss { get; init; } = new();
    public ArenaConfig Arena { get; init; } = new();
    public SkillTable Skills { get; private set; } = null!;

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

        var skillPath = Path.IsPathRooted(raw.SkillTable)
            ? raw.SkillTable
            : Path.Combine(dir, raw.SkillTable);

        var config = new BattleConfig
        {
            Listen = raw.Listen,
            // 结算目录按当前工作目录解析，约定从仓库根启动，写 var/battle/settlements 就是那个。
            // 启动日志会打绝对路径，跑错目录一眼能看出来。
            SettlementDir = Path.GetFullPath(raw.SettlementDir),
            SkillTablePath = skillPath,
            Caster = raw.Caster,
            Boss = raw.Boss,
            Arena = raw.Arena,
            SourcePath = full,
            Skills = SkillTable.Load(skillPath),
        };

        config.Validate();
        return config;
    }

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

        if (Caster.HpMax <= 0)
        {
            throw new ConfigException($"{SourcePath}：caster.hp_max 必须大于 0");
        }

        if (Boss.ExpOnClear < 0)
        {
            throw new ConfigException($"{SourcePath}：boss.exp_on_clear 不能是负数");
        }

        if (Boss.Id == Caster.Id)
        {
            throw new ConfigException($"{SourcePath}：boss.id 和 caster.id 撞了");
        }

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

        // 技能表加载时已经校验过每一项，这里只看至少有一个技能打得到这个站位。
        // 全都打不到的话这场永远打不完，属于配置写错，早报早改。
        var distance = DistanceMm(Caster.SpawnMm, Boss.SpawnMm);
        if (!Skills.All.Any(s => s.RangeMm >= distance))
        {
            throw new ConfigException(
                $"{SourcePath}：站位距离 {distance}mm，但技能表里没有一个技能够得着，这场打不完");
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
        public string SkillTable { get; init; } = "skills.json";
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
