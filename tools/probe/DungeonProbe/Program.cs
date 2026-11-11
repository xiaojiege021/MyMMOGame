using System;
using System.Globalization;
using Mmo.Net;

namespace Mmo.Tools;

/// <summary>
/// 进本这一件事的纯 .NET 探针：直接编客户端那个 `DungeonEntry.cs`，
/// 所以验到的就是随包发出去的那份拆装。
///
/// 只吃命令行给的状态码与回包，不连网关——真正的 HTTP 由 check-u3.sh 用 curl 打，
/// 打回来的状态码与 body 原样喂进来，两端拼起来才算一条证据。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  --enter-body <角色号> <副本编号> <人数>");
            Console.WriteLine("  --enter <状态码> <回包>");
            Console.WriteLine("  --fatigue-line <状态码> <回包>");
            return 2;
        }

        switch (args[0])
        {
            case "--enter-body":
                Expect(args, 4);
                var id = ulong.Parse(args[1], CultureInfo.InvariantCulture);
                var size = int.Parse(args[3], CultureInfo.InvariantCulture);
                Console.WriteLine("body=" + DungeonEntry.EnterBody(id, args[2], size));
                return 0;

            case "--enter":
                Expect(args, 3);
                return Print(int.Parse(args[1], CultureInfo.InvariantCulture), args[2], false);

            case "--fatigue-line":
                Expect(args, 3);
                return Print(int.Parse(args[1], CultureInfo.InvariantCulture), args[2], true);

            default:
                Console.WriteLine("unknown=" + args[0]);
                return 2;
        }
    }

    static int Print(int status, string body, bool asLine)
    {
        var outcome = DungeonEntry.ParseEnter(status, body);

        Console.WriteLine("kind=" + DungeonEntry.KindName(outcome.Kind));
        Console.WriteLine("allowed=" + (outcome.Allowed ? "true" : "false"));
        Console.WriteLine("reason=" + outcome.Reason);
        Console.WriteLine("max_players=" + outcome.MaxPlayers.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("fatigue=" + outcome.Fatigue.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("fatigue_cost=" + outcome.FatigueCost.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("fatigue_daily_cap=" + outcome.FatigueDailyCap.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("weekly_share_cost=" + outcome.WeeklyShareCost.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("weekly_share_remaining=" + outcome.WeeklyShareRemaining.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("weekly_share_cap=" + outcome.WeeklyShareCap.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("reward_shares=" + outcome.RewardShares.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("entry_id=" + outcome.EntryId);

        if (asLine)
        {
            // 与屏上那行同一个形状：核对直接拿它跟 UiDungeonFlow.ResultLine 比。
            Console.WriteLine("line=疲劳 " + outcome.Fatigue + " / " + outcome.FatigueDailyCap
                + " · 本次扣 " + outcome.FatigueCost
                + " · 本周剩 " + outcome.WeeklyShareRemaining + " / " + outcome.WeeklyShareCap
                + " · 每人 " + outcome.RewardShares + " 份");
        }

        // 拆装本身不该失败：不管允许、被拒还是连不上，退出码都是 0，
        // 判定由调用方按 kind 做。只有参数不对才返回 2。
        return 0;
    }

    static void Expect(string[] args, int count)
    {
        if (args.Length < count)
        {
            throw new ArgumentException("参数不够：" + string.Join(" ", args));
        }
    }
}
