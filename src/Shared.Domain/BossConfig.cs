namespace Mmo.Shared.Domain;

/// <summary>
/// 场地坐标（毫米）。世界 Boss 的出生点与战斗配置的出生点共用这一个类型。
/// 原在战斗服 Config.cs，C6/D12 随世界 Boss 表一起搬进共享域。
/// </summary>
public sealed class PointConfig
{
    public int X { get; init; }
    public int Y { get; init; }
}

/// <summary>一个 Boss 的数值：编号、名字、生命、出生点、通关经验与出手 / 阶段。</summary>
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
