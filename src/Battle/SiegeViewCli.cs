using System.Globalization;
using Mmo.Proto.Battle;

namespace Battle;

/// <summary>
/// P3-3a2 的只读核对入口：不连库、不起服、不读配置，直接喂一批单位给
/// SiegeViewTable.Build，把服务端算出来的档位逐行打出来。
/// 用法（核对脚本从 config/siege.json 现取上限传进来）：
///   --siege-view &lt;limit&gt; &lt;viewerId&gt; &lt;id:team:x:y:hp:hpMax&gt; ...
/// 与真实启动共用同一个 Build，不另写一套排序或上限。
/// </summary>
public static class SiegeViewCli
{
    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count < 2
            || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
            || !ulong.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var viewer))
        {
            Console.WriteLine("view_error=usage --siege-view <limit> <viewerId> <id:team:x:y:hp:hpMax> ...");
            return 2;
        }

        var units = new List<SiegeViewUnit>();
        for (var i = 2; i < args.Count; i++)
        {
            var parts = args[i].Split(':');
            if (parts.Length != 6
                || !ulong.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var team)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hp)
                || !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hpMax))
            {
                Console.WriteLine("view_error=bad_unit " + args[i]);
                return 2;
            }
            units.Add(new SiegeViewUnit(id, team, x, y, hp, hpMax));
        }

        if (limit < 1)
        {
            Console.WriteLine("view_error=limit");
            return 2;
        }

        var view = SiegeViewTable.Build(0, viewer, limit, units);
        Console.WriteLine($"siege_view=session={view.SessionId} limit={view.FullModelLimit}"
            + $" full={view.FullModelCount} units={view.UnitCount}"
            + $" markers={view.UnitCount - view.FullModelCount}");
        foreach (var unit in view.Units)
        {
            var tier = unit.Tier == SiegeVisualTier.SiegeVisualFull ? "full" : "marker";
            Console.WriteLine($"unit=id={unit.MemberId} team={unit.Team} tier={tier}"
                + $" hp={unit.Hp} hp_max={unit.HpMax}");
        }
        return 0;
    }
}
