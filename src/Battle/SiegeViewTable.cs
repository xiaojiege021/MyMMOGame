using Mmo.Proto.Battle;

namespace Battle;

/// <summary>
/// 城战里一个单位的表现事实值（P3-3a2）。坐标只给战斗服算「谁算周围」，
/// 不下发给客户端；队伍颜色只用队伍编号，血条只用当前血量与上限。
/// </summary>
public readonly record struct SiegeViewUnit(
    ulong MemberId, uint Team, double X, double Y, long Hp, long HpMax);

/// <summary>
/// 城战同屏档位的唯一计算点（方案 15.1、开发架构 3 节「城战表现」、9 节、14 节第 64 行）。
///
/// 方案写定「周围的人显示完整，超出同屏上限的人只留队伍颜色和血条」，没写定
/// 完整模型的数量、也没写定谁算周围：数量只从 config/siege.json 的 view 段读，
/// 顺序按「看的人」到各人的距离、再按成员编号稳定排序，自己恒在最前。
/// 城战是战斗服里的一场实例，名单、坐标与血量都在本进程内存，档位在这里算完
/// 才下发；世界服不管实例里的人流，客户端也不按距离自行决定谁该画全。
///
/// 这里只做纯计算：不连库、不结算技能、不读配置、不写第二套上限。
/// </summary>
public static class SiegeViewTable
{
    public static SiegeView Build(
        ulong sessionId, ulong viewerId, int fullLimit, IEnumerable<SiegeViewUnit> units)
    {
        if (fullLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fullLimit), "同屏上限必须至少 1");
        }

        var all = units.ToList();
        double vx = 0, vy = 0;
        var hasViewer = false;
        foreach (var unit in all)
        {
            if (unit.MemberId != viewerId)
            {
                continue;
            }
            vx = unit.X;
            vy = unit.Y;
            hasViewer = true;
            break;
        }

        // 自己恒完整；其余按距离→编号排序，距离相同也保证两份快照一致。
        // 不是 AOI：名单一个都不删，只给每个单位标表现档位。
        var ordered = all
            .OrderBy(unit => unit.MemberId == viewerId ? 0 : 1)
            .ThenBy(unit => hasViewer
                ? (unit.X - vx) * (unit.X - vx) + (unit.Y - vy) * (unit.Y - vy)
                : 0.0)
            .ThenBy(unit => unit.MemberId)
            .ToList();

        var view = new SiegeView
        {
            SessionId = sessionId,
            FullModelLimit = (uint)fullLimit,
            UnitCount = (uint)ordered.Count,
        };

        for (var i = 0; i < ordered.Count; i++)
        {
            var unit = ordered[i];
            var tier = i < fullLimit
                ? SiegeVisualTier.SiegeVisualFull
                : SiegeVisualTier.SiegeVisualMarker;
            if (tier == SiegeVisualTier.SiegeVisualFull)
            {
                view.FullModelCount++;
            }
            view.Units.Add(new SiegeUnitVisual
            {
                MemberId = unit.MemberId,
                Team = unit.Team,
                Hp = unit.Hp,
                HpMax = unit.HpMax,
                Tier = tier,
            });
        }

        return view;
    }
}
