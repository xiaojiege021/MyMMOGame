using Mmo.Shared.Domain;

namespace World;

/// <summary>
/// Q11 的第一层容量：人是不是站在入口所在地表的一条分线上，这条线现在几个人。
/// 结论只从分线注册表取，不另存一份在线状态，也不读客户端带来的任何东西。
/// </summary>
public sealed class LineStandingProbe(LineRegistry registry, WorldBossTable? table) : IEntranceLine
{
    public LineStanding Where(ulong characterId)
    {
        var standing = registry.StandingOf(characterId);
        if (standing is null)
        {
            return new LineStanding(false, "", 0, 0);
        }

        var onEntrance = table is not null
            && string.Equals(standing.RegionId, table.RegionId, StringComparison.Ordinal);
        return new LineStanding(onEntrance, standing.LineId, standing.LineSize, standing.Capacity);
    }
}
