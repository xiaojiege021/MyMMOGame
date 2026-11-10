using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Mmo.Net;
using Mmo.Proto.Battle;
using UnityEditor;
using UnityEngine;
// System.Diagnostics 里也有 Debug，这里只要 Stopwatch，日志一律走 Unity 的。
using Debug = UnityEngine.Debug;

// P0-2b 核对（编辑器这一侧）：真的连一场战斗，按「远处打不到 → 走近 → 打中 →
// 前摇里撤掉 → 超界被拉回」走一遍，看服务端认不认走位、认不认打断。
// 界面怎么摆不在这一层验，真机段看 logcat。
// 打印格式：p02b_check_ 开头的 kv 行，由 tools/check/check-p0-2b.sh 逐项断言。
public static class P02bCheck
{
    static int _pass;

    static int _fail;

    public static void Run()
    {
        var url = Environment.GetEnvironmentVariable("MMO_BATTLE_URL") ?? string.Empty;

        int code;
        try
        {
            code = Task.Run(() => FlowAsync(url)).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Debug.Log("p02b_check_fail=" + e.GetType().Name + " " + e.Message);
            code = 1;
        }

        Debug.Log("p02b_check_summary=" + (code == 0 ? "ok" : "fail")
            + " pass=" + _pass + " fail=" + _fail);

        EditorApplication.Exit(code);
    }

    static async Task<int> FlowAsync(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            Debug.Log("p02b_check_fail=没有给 MMO_BATTLE_URL");
            return 1;
        }

        var link = await BattleLink.ConnectAsync(url, TimeSpan.FromSeconds(5));
        try
        {
            var opened = await ExpectOpened(link);
            if (opened == null)
            {
                return 1;
            }

            // 1. 站位 6000mm，近战 1001 的 3000mm 够不着，必须先被拒。
            var hpBefore = opened.BossHp;
            var far = await CastAndWait(link, 1001, TimeSpan.FromSeconds(3));
            Check("far_rejected", far.BodyCase == ServerMessage.BodyOneofCase.Rejected
                && far.Rejected.Reason == SkillRejectReason.SkillRejectOutOfRange,
                "reason=" + (far.BodyCase == ServerMessage.BodyOneofCase.Rejected
                    ? far.Rejected.Reason.ToString()
                    : far.BodyCase.ToString()));
            Check("far_hp_unchanged", opened.BossHp == hpBefore, "hp=" + opened.BossHp);

            // 2. 一次跨 4000mm 的走位会被速度上限夹住，服务端不采信客户端坐标。
            var jump = await MoveAndWait(link, 2000, 0);
            Check("move_clamped", jump.Clamped, "distance=" + jump.DistanceMm);
            Check("move_step_limited", jump.DistanceMm >= 5350,
                "distance=" + jump.DistanceMm);

            // 3. 一步一步走近，每步最多一个上报间隔的量。
            var near = jump;
            for (var i = 0; i < 24 && near.DistanceMm > 2100; i++)
            {
                await Task.Delay(100);
                near = await MoveAndWait(link, 2000, 0);
            }

            Check("move_near", near.DistanceMm <= 2100 && near.DistanceMm >= 1900,
                "distance=" + near.DistanceMm);

            // 4. 走近之后同样的 1001 应该打得中，伤害由服务端给。
            var hit = await CastAndWait(link, 1001, TimeSpan.FromSeconds(4));
            Check("near_hit", hit.BodyCase == ServerMessage.BodyOneofCase.Result && hit.Result.Hit,
                "case=" + hit.BodyCase);
            var hpAfterHit = hit.BodyCase == ServerMessage.BodyOneofCase.Result
                ? hit.Result.TargetHpAfter
                : hpBefore;
            Check("hit_damage", hit.BodyCase == ServerMessage.BodyOneofCase.Result
                && hit.Result.Damage == 120,
                "damage=" + (hit.BodyCase == ServerMessage.BodyOneofCase.Result ? hit.Result.Damage : 0));

            // 5. 前摇里撤掉：900ms 的横扫，150ms 时撤，之后不能有迟到的结算。
            // 上一个技能的后摇 300ms 还没走完时发，会被 RECOVERY 挡掉，先等过去。
            await Task.Delay(400);
            var castTime = await Cast(link, 1003);
            await Task.Delay(150);
            await link.SendCancelAsync(1003, castTime);

            var cancelled = await WaitFor(link, m => m.BodyCase == ServerMessage.BodyOneofCase.Cancelled,
                TimeSpan.FromSeconds(2));
            Check("cancel_accepted", cancelled != null && cancelled.Cancelled.Accepted,
                "accepted=" + (cancelled != null && cancelled.Cancelled.Accepted));

            var late = await WaitFor(link,
                m => m.BodyCase == ServerMessage.BodyOneofCase.Result
                     || m.BodyCase == ServerMessage.BodyOneofCase.Rejected,
                TimeSpan.FromSeconds(1.5));
            Check("cancel_no_damage", late == null, "late=" + (late == null ? "none" : late.BodyCase.ToString()));

            // 6. 一直往场外走，最后会被场地半径挡在 8000mm，再远也出不去。
            var outside = near;
            for (var i = 0; i < 30 && outside.DistanceMm < 7900; i++)
            {
                await Task.Delay(100);
                outside = await MoveAndWait(link, 20000, 0);
            }

            Check("out_of_arena_clamped", outside.Clamped && outside.DistanceMm <= 8100,
                "distance=" + outside.DistanceMm + " clamped=" + outside.Clamped);

            Debug.Log("p02b_check_chain=ok hp_after_hit=" + hpAfterHit
                + " skills=" + opened.Skills.Count
                + " arena=" + opened.ArenaRadiusMm
                + " speed=" + opened.MoveSpeedMmPerS);
        }
        finally
        {
            await link.CloseAsync();
            link.Dispose();
        }

        return _fail == 0 ? 0 : 1;
    }

    static async Task<BattleOpened> ExpectOpened(BattleLink link)
    {
        var message = await link.ReceiveAsync(TimeSpan.FromSeconds(5));
        if (message.BodyCase != ServerMessage.BodyOneofCase.Opened)
        {
            Debug.Log("p02b_check_fail=第一条不是开场包，是 " + message.BodyCase);
            return null;
        }

        var opened = message.Opened;
        Check("opened", opened.BossHp == opened.BossHpMax, "boss_hp=" + opened.BossHp + "/" + opened.BossHpMax);
        Check("opened_skills", opened.Skills.Count == 3, "skills=" + opened.Skills.Count);
        Check("opened_arena", opened.ArenaRadiusMm == 8000 && opened.MoveSpeedMmPerS == 3000,
            "arena=" + opened.ArenaRadiusMm + " speed=" + opened.MoveSpeedMmPerS);
        Check("opened_distance", opened.DistanceMm == 6000, "distance=" + opened.DistanceMm);

        Debug.Log("p02b_check_open distance=" + opened.DistanceMm
            + " skills=" + opened.Skills.Count
            + " arena=" + opened.ArenaRadiusMm
            + " speed=" + opened.MoveSpeedMmPerS);
        return opened;
    }

    /// <summary>发一次技能，返回第一条跟它有关的回包（结果或拒绝）。</summary>
    static async Task<ServerMessage> CastAndWait(BattleLink link, uint skillId, TimeSpan timeout)
    {
        await Cast(link, skillId);
        return await WaitFor(link,
            m => m.BodyCase == ServerMessage.BodyOneofCase.Result
                 || m.BodyCase == ServerMessage.BodyOneofCase.Rejected,
            timeout) ?? new ServerMessage();
    }

    /// <summary>发技能并返回这次用的施放时刻，取消时要拿它对上。</summary>
    static async Task<long> Cast(BattleLink link, uint skillId)
    {
        var castTime = (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        await link.SendSkillAsync(skillId, 9001, castTime);
        return castTime;
    }

    static async Task<MoveState> MoveAndWait(BattleLink link, int x, int y)
    {
        var sent = (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        await link.SendMoveAsync(x, y, sent);

        var message = await WaitFor(link, m => m.BodyCase == ServerMessage.BodyOneofCase.Moved,
            TimeSpan.FromSeconds(3));
        if (message == null)
        {
            Debug.Log("p02b_check_fail=走位没有回包");
            return new MoveState();
        }

        return message.Moved;
    }

    static async Task<ServerMessage> WaitFor(BattleLink link, Func<ServerMessage, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                var message = await link.ReceiveAsync(remaining);
                if (match(message))
                {
                    return message;
                }
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        return null;
    }

    static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            _pass++;
            Debug.Log("p02b_check_ok " + name + " " + detail);
        }
        else
        {
            _fail++;
            Debug.Log("p02b_check_bad " + name + " " + detail);
        }
    }
}
