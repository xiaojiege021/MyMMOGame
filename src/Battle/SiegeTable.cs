using System.Text.Json;

namespace Battle;

/// <summary>
/// 城战这一层的配置：40 对 40、凑不齐的开场时限、每级强化的全额折算与战力权重。
/// config/siege.json 是唯一正本，代码里不写第二套人数、也不写第二套概率/折算。
///
/// 与战场的分别：战场是「装备生效 + 同队 1.3 倍压缩」，城战是「装备全额生效」——
/// 算出真实属性后直接进场，不做任何平衡系数，也不套竞技场统一模板。
/// 这张表里出现 balance / ratio / templates 之类的键直接起不来，
/// 让「不压缩」成为结构保证，而不是靠运行时忘了调用某段代码。
///
/// P3-3a2：view 段是城战同屏完整模型的数量上限（方案 15.1 没给数字，标待定）。
/// 只放一个上限，表现细节（队伍颜色、血条）由战斗服按档位下发，不进这张配置表。
/// </summary>
public sealed class SiegeTable
{
    SiegeTable(
        int version, string modeId, string name, int teamSize, int teams, int fillTimeoutMs,
        int viewFullModelLimit,
        long attackPerLevel, long hpPerLevel, long manaPerLevel, long healPerLevel,
        long attackWeight, long hpWeight, long manaWeight, long healWeight, bool missing)
    {
        Version = version;
        ModeId = modeId;
        Name = name;
        TeamSize = teamSize;
        Teams = teams;
        FillTimeoutMs = fillTimeoutMs;
        ViewFullModelLimit = viewFullModelLimit;
        AttackPerLevel = attackPerLevel;
        HpPerLevel = hpPerLevel;
        ManaPerLevel = manaPerLevel;
        HealPerLevel = healPerLevel;
        AttackWeight = attackWeight;
        HpWeight = hpWeight;
        ManaWeight = manaWeight;
        HealWeight = healWeight;
        IsMissing = missing;
    }

    public int Version { get; }
    public string ModeId { get; }
    public string Name { get; }
    public int TeamSize { get; }
    public int Teams { get; }
    public int Players => TeamSize * Teams;
    public int FillTimeoutMs { get; }

    /// <summary>城战里同时画完整模型的人数上限。只从这里读，代码里不写第二套数字。</summary>
    public int ViewFullModelLimit { get; }
    public long AttackPerLevel { get; }
    public long HpPerLevel { get; }
    public long ManaPerLevel { get; }
    public long HealPerLevel { get; }
    public long AttackWeight { get; }
    public long HpWeight { get; }
    public long ManaWeight { get; }
    public long HealWeight { get; }

    /// <summary>没配城战表。城战连接一律拒，副本、练习场、竞技场与战场不受影响。</summary>
    public bool IsMissing { get; }

    public static SiegeTable Missing() =>
        new(0, "", "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, missing: true);

    /// <summary>有效战力 = 四项属性各乘权重。只作事实值，不拿来算平衡系数。</summary>
    public long Power(long attack, long hp, long mana, long heal) =>
        attack * AttackWeight + hp * HpWeight + mana * ManaWeight + heal * HealWeight;

    public static SiegeTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"城战表不存在：{path}");
        }

        // 反序列化会静默忽略未知字段，所以先扫一遍顶层键：
        // 拿战场表 / 竞技场表当城战表用，或后来有人在城战里加平衡系数，都要当场拒掉。
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var document = JsonDocument.Parse(File.ReadAllText(path), options);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var key = property.Name.ToLowerInvariant();
            if (key is "balance" or "ratio" or "templates" or "raise_low_only"
                or "adjusted_power" or "arena_match")
            {
                throw new ConfigException(
                    $"{path}：城战表不许出现 {property.Name}（装备全额生效，不做平衡系数 / 模板）");
            }
            if (key == "view" && property.Value.ValueKind == JsonValueKind.Object)
            {
                // 方案 15.1：超限单位只留队伍颜色和血条。配置里只放一个上限，
                // 不许把模型 / 外观 / 颜色 / prefab 写进来，也不许在这里偷偷定档位。
                foreach (var nested in property.Value.EnumerateObject())
                {
                    var nestedKey = nested.Name.ToLowerInvariant();
                    if (nestedKey is "model" or "models" or "prefab" or "appearance"
                        or "appearance_tier" or "mesh" or "team_color" or "hp_bar" or "tier")
                    {
                        throw new ConfigException(
                            $"{path}：view 段不许出现 {nested.Name}（只放上限，表现档位由战斗服下发）");
                    }
                }
            }
        }

        Wire wire;
        try
        {
            wire = ConfigJson.Read<Wire>(path);
        }
        catch (ConfigException)
        {
            throw;
        }

        if (wire.Schema != "siege-table/1")
        {
            throw new ConfigException($"{path}：schema 必须是 siege-table/1，现在是 {wire.Schema}");
        }
        if (wire.Version <= 0)
        {
            throw new ConfigException($"{path}：version 必须是正整数");
        }

        var mode = wire.Mode ?? throw new ConfigException($"{path}：没有 mode 段");
        if (string.IsNullOrWhiteSpace(mode.Id))
        {
            throw new ConfigException($"{path}：mode.id 不能为空");
        }
        if (mode.TeamSize < 1)
        {
            throw new ConfigException($"{path}：mode.team_size 必须至少 1");
        }
        if (mode.Teams < 2)
        {
            throw new ConfigException($"{path}：mode.teams 必须至少 2");
        }

        var players = mode.TeamSize * mode.Teams;
        if (players < 2 || players > 100)
        {
            throw new ConfigException($"{path}：城战开打人数是 {players}，超出 2..100");
        }

        if (wire.FillTimeoutMs < 0 || wire.FillTimeoutMs > 60000)
        {
            throw new ConfigException($"{path}：fill_timeout_ms 要在 0..60000 之间");
        }

        // 同屏上限：方案没给数字，只从 view 段读，不给兜底值，也不按人数反推一份。
        // 上限允许大于本场实际人数（人少时自然一个都不超限），所以不与 players 绑死。
        var view = wire.View ?? throw new ConfigException($"{path}：没有 view 段（同屏完整模型上限）");
        if (view.FullModelLimit < 1 || view.FullModelLimit > 100)
        {
            throw new ConfigException($"{path}：view.full_model_limit 要在 1..100 之间");
        }

        var equipment = wire.Equipment ?? throw new ConfigException($"{path}：没有 equipment 段");
        if (equipment.AttackPerLevel < 0 || equipment.HpPerLevel < 0
            || equipment.ManaPerLevel < 0 || equipment.HealPerLevel < 0)
        {
            throw new ConfigException($"{path}：每级强化的折算不能是负数");
        }

        var power = wire.Power ?? throw new ConfigException($"{path}：没有 power 段");
        if (power.AttackWeight < 0 || power.HpWeight < 0
            || power.ManaWeight < 0 || power.HealWeight < 0)
        {
            throw new ConfigException($"{path}：战力权重不能是负数");
        }
        if (power.AttackWeight + power.HpWeight + power.ManaWeight + power.HealWeight <= 0)
        {
            throw new ConfigException($"{path}：战力权重全是 0，算不出战力");
        }

        return new SiegeTable(
            wire.Version, mode.Id.Trim(), mode.Name.Trim(), mode.TeamSize, mode.Teams,
            wire.FillTimeoutMs, view.FullModelLimit,
            equipment.AttackPerLevel, equipment.HpPerLevel,
            equipment.ManaPerLevel, equipment.HealPerLevel,
            power.AttackWeight, power.HpWeight, power.ManaWeight, power.HealWeight,
            missing: false);
    }

    sealed class Wire
    {
        public string Schema { get; init; } = "";
        public int Version { get; init; }
        public ModeWire? Mode { get; init; }
        public int FillTimeoutMs { get; init; }
        public ViewWire? View { get; init; }
        public EquipmentWire? Equipment { get; init; }
        public PowerWire? Power { get; init; }
    }

    sealed class ViewWire
    {
        public int FullModelLimit { get; init; }
    }

    sealed class ModeWire
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public int TeamSize { get; init; }
        public int Teams { get; init; }
    }

    sealed class EquipmentWire
    {
        public long AttackPerLevel { get; init; }
        public long HpPerLevel { get; init; }
        public long ManaPerLevel { get; init; }
        public long HealPerLevel { get; init; }
    }

    sealed class PowerWire
    {
        public long AttackWeight { get; init; }
        public long HpWeight { get; init; }
        public long ManaWeight { get; init; }
        public long HealWeight { get; init; }
    }
}
