using System.Text.Json;
using System.Text.Json.Serialization;

namespace Battle;

public sealed class CasterConfig
{
    public ulong Id { get; init; }

    /// <summary>出生点。生命上限不在这里：体力是所有人的生命，数值在职业表。</summary>
    public PointConfig SpawnMm { get; init; } = new();
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

