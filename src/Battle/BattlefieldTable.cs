using System.Text.Json;

namespace Battle;

/// <summary>
/// 战场这一层的配置：10 对 10、凑不齐的开场时限、同队平衡比例、每级强化折算、
/// 以及战力权重。config/battlefield.json 是唯一正本，代码里不写第二套数字。
///
/// 战场和竞技场是两条独立的线：竞技场按职业与分支取统一模板（config/arena_match.json），
/// 战场先让装备生效、再把同一队的战力差压进 ratio 倍以内。
/// </summary>
public sealed class BattlefieldTable
{
    BattlefieldTable(
        int version, string modeId, int teamSize, int teams, int fillTimeoutMs, double ratio,
        long attackPerLevel, long hpPerLevel, long manaPerLevel, long healPerLevel,
        long attackWeight, long hpWeight, long manaWeight, long healWeight, bool missing)
    {
        Version = version;
        ModeId = modeId;
        TeamSize = teamSize;
        Teams = teams;
        FillTimeoutMs = fillTimeoutMs;
        Ratio = ratio;
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
    public int TeamSize { get; }
    public int Teams { get; }
    public int Players => TeamSize * Teams;
    public int FillTimeoutMs { get; }
    public double Ratio { get; }
    public long AttackPerLevel { get; }
    public long HpPerLevel { get; }
    public long ManaPerLevel { get; }
    public long HealPerLevel { get; }
    public long AttackWeight { get; }
    public long HpWeight { get; }
    public long ManaWeight { get; }
    public long HealWeight { get; }

    /// <summary>没配战场表。战场连接一律拒，副本、练习场与竞技场不受影响。</summary>
    public bool IsMissing { get; }

    public static BattlefieldTable Missing() =>
        new(0, "", 0, 0, 0, 1.0, 0, 0, 0, 0, 0, 0, 0, 0, missing: true);

    /// <summary>有效战力 = 四项属性各乘权重。权重方案没给，来配置。</summary>
    public long Power(long attack, long hp, long mana, long heal) =>
        attack * AttackWeight + hp * HpWeight + mana * ManaWeight + heal * HealWeight;

    /// <summary>
    /// 同队压缩：低于「本队最高战力 / ratio」的人抬到这条线，最高的人不动。
    /// 这样队里任何两个人的有效战力之比都不超过 ratio，且只抬低不削高
    /// （方案 12.3 只写「压缩至 1.3 倍内」，没写削高还是抬低）。
    /// </summary>
    public long AdjustedPower(long power, long teamMaxPower)
    {
        if (power <= 0 || teamMaxPower <= 0 || Ratio <= 1.0)
        {
            return power;
        }

        var floor = (long)Math.Floor(teamMaxPower / Ratio);
        return power < floor ? floor : power;
    }

    public static BattlefieldTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"战场表不存在：{path}");
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
            throw new ConfigException($"{path}：战场开打人数是 {players}，超出 2..100");
        }

        if (wire.FillTimeoutMs < 0 || wire.FillTimeoutMs > 60000)
        {
            throw new ConfigException($"{path}：fill_timeout_ms 要在 0..60000 之间");
        }

        var balance = wire.Balance ?? throw new ConfigException($"{path}：没有 balance 段");
        if (balance.Ratio <= 1.0 || balance.Ratio > 100.0)
        {
            throw new ConfigException($"{path}：balance.ratio 必须大于 1，现在是 {balance.Ratio}");
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

        return new BattlefieldTable(
            wire.Version, mode.Id.Trim(), mode.TeamSize, mode.Teams, wire.FillTimeoutMs,
            balance.Ratio,
            equipment.AttackPerLevel, equipment.HpPerLevel,
            equipment.ManaPerLevel, equipment.HealPerLevel,
            power.AttackWeight, power.HpWeight, power.ManaWeight, power.HealWeight,
            missing: false);
    }

    sealed class Wire
    {
        public int Version { get; init; }
        public ModeWire? Mode { get; init; }
        public int FillTimeoutMs { get; init; }
        public BalanceWire? Balance { get; init; }
        public EquipmentWire? Equipment { get; init; }
        public PowerWire? Power { get; init; }
    }

    sealed class ModeWire
    {
        public string Id { get; init; } = "";
        public int TeamSize { get; init; }
        public int Teams { get; init; }
    }

    sealed class BalanceWire
    {
        public double Ratio { get; init; }
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
