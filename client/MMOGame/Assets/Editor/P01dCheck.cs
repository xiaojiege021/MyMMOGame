using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Mmo.Net;
using Mmo.Proto.Battle;
using UnityEditor;
using UnityEngine;
// System.Diagnostics 里也有 Debug，这里只要 Stopwatch，日志一律走 Unity 的。
using Debug = UnityEngine.Debug;

// P0-1d 核对用：从前摇里打断这次施法，以及进程重启后新开一场是不是干净。
// 三个模式由 MMO_P01D_MODE 给：cancel / clear / reopen，各自跑一段就退出。
// 打印格式：p01d_ 开头的 kv 行，由 tools/check/check-p0-1d.sh 逐项断言。
public static class P01dCheck
{
    public static void Run()
    {
        var url = Environment.GetEnvironmentVariable("MMO_BATTLE_URL") ?? string.Empty;
        var mode = Environment.GetEnvironmentVariable("MMO_P01D_MODE") ?? "cancel";
        var skillId = uint.TryParse(Environment.GetEnvironmentVariable("MMO_BATTLE_SKILL"), out var s) ? s : 1002u;
        var bossId = ulong.TryParse(Environment.GetEnvironmentVariable("MMO_BATTLE_BOSS"), out var b) ? b : 0ul;

        int code;
        try
        {
            // 主线程要一直等这段跑完。在主线程里 await，续体会排回主线程，就成了自己等自己。
            code = Task.Run(() => FlowAsync(url, mode, skillId, bossId)).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Debug.Log("p01d_fail=" + e.GetType().Name + " " + e.Message);
            code = 1;
        }

        Debug.Log("p01d_exit=" + code);
        EditorApplication.Exit(code);
    }

    static async Task<int> FlowAsync(string url, string mode, uint skillId, ulong bossId)
    {
        if (string.IsNullOrEmpty(url))
        {
            Debug.Log("p01d_fail=没有给 MMO_BATTLE_URL");
            return 1;
        }

        var link = await BattleLink.ConnectAsync(url, TimeSpan.FromSeconds(5));
        try
        {
            var openedMessage = await link.ReceiveAsync(TimeSpan.FromSeconds(5));
            if (openedMessage.BodyCase != ServerMessage.BodyOneofCase.Opened)
            {
                Debug.Log("p01d_fail=第一条不是开场包，是 " + openedMessage.BodyCase);
                return 1;
            }

            var opened = openedMessage.Opened;
            if (bossId == 0)
            {
                bossId = opened.BossId;
            }

            Debug.Log(
                "p01d_opened=session=" + opened.SessionId
                + " boss=" + opened.BossId
                + " hp=" + opened.BossHp
                + " hp_max=" + opened.BossHpMax
                + " distance_mm=" + opened.DistanceMm);

            switch (mode)
            {
                case "cancel":
                    return await RunCancelAsync(link, opened.BossId, skillId);
                case "clear":
                    return await RunClearAsync(link, opened.BossId, skillId);
                case "reopen":
                    return await RunReopenAsync(link);
                default:
                    Debug.Log("p01d_fail=不认识的模式 " + mode);
                    return 1;
            }
        }
        finally
        {
            link.Dispose();
        }
    }

    /// <summary>
    /// 打断：前摇里发取消，撤掉就该没有伤害。撤干净之后冷却也退回去了，
    /// 所以马上再发同一个技能应当被接受，这一下会真的打出去。
    /// </summary>
    static async Task<int> RunCancelAsync(BattleLink link, ulong bossId, uint skillId)
    {
        // 前摇 600ms，160ms 时打断，离结算还早。
        var castTimeMs = NowMs();
        await link.SendSkillAsync(skillId, bossId, castTimeMs);
        await Task.Delay(CancelDelayMs);

        var watch = Stopwatch.StartNew();
        await link.SendCancelAsync(skillId, castTimeMs);
        var cancelled = await link.ReceiveAsync(TimeSpan.FromSeconds(3));
        watch.Stop();

        if (cancelled.BodyCase != ServerMessage.BodyOneofCase.Cancelled)
        {
            Debug.Log("p01d_fail=取消没拿到回执，是 " + cancelled.BodyCase);
            return 1;
        }

        Debug.Log(
            "p01d_cancel=accepted=" + cancelled.Cancelled.Accepted
            + " skill=" + cancelled.Cancelled.SkillId
            + " cast_time_ms=" + cancelled.Cancelled.CastTimeMs
            + " elapsed_ms=" + watch.ElapsedMilliseconds);

        // 前摇早该走完了。这段时间里如果冒出结算，就是打断没生效。
        await Task.Delay(ForeswingObserveMs);
        var leak = 0;
        long lateDamage = 0;
        while (link.TryReceive(out var stray))
        {
            if (stray.BodyCase == ServerMessage.BodyOneofCase.Result)
            {
                leak++;
                lateDamage += stray.Result.Damage;
            }
        }

        Debug.Log("p01d_cancel_leak=" + leak + " late_damage=" + lateDamage);

        // 撤掉之后冷却退回出手前，同一技能应当立刻能再发。
        var reCastTimeMs = NowMs();
        var reWatch = Stopwatch.StartNew();
        await link.SendSkillAsync(skillId, bossId, reCastTimeMs);
        var again = await link.ReceiveAsync(TimeSpan.FromSeconds(5));
        reWatch.Stop();

        if (again.BodyCase == ServerMessage.BodyOneofCase.Rejected)
        {
            Debug.Log(
                "p01d_after_cancel=rejected reason=" + again.Rejected.Reason
                + " retry_after_ms=" + again.Rejected.RetryAfterMs);
            return 1;
        }

        if (again.BodyCase != ServerMessage.BodyOneofCase.Result)
        {
            Debug.Log("p01d_fail=取消后重发没拿到结果，是 " + again.BodyCase);
            return 1;
        }

        var first = again.Result;
        Debug.Log(
            "p01d_after_cancel=result skill=" + first.SkillId
            + " damage=" + first.Damage
            + " hp_after=" + first.TargetHpAfter
            + " elapsed_ms=" + reWatch.ElapsedMilliseconds);

        // 已经结算完，这时候再发取消撤不回来，服务端应当回 accepted=false。
        await link.SendCancelAsync(skillId, reCastTimeMs);
        var late = await link.ReceiveAsync(TimeSpan.FromSeconds(3));
        if (late.BodyCase != ServerMessage.BodyOneofCase.Cancelled)
        {
            Debug.Log("p01d_fail=结算后取消没拿到回执，是 " + late.BodyCase);
            return 1;
        }

        Debug.Log("p01d_late_cancel=accepted=" + late.Cancelled.Accepted);

        // 再打一下，让血量明显不足满值。崩掉之后新一场要是满血，就说明进度没被带回来。
        await Task.Delay(CooldownWaitMs);
        await link.SendSkillAsync(skillId, bossId, NowMs());
        var second = await link.ReceiveAsync(TimeSpan.FromSeconds(5));
        if (second.BodyCase != ServerMessage.BodyOneofCase.Result)
        {
            Debug.Log("p01d_fail=第二下没拿到结果，是 " + second.BodyCase);
            return 1;
        }

        Debug.Log("p01d_partial_hp=" + second.Result.TargetHpAfter);

        await link.CloseAsync();
        Debug.Log("p01d_closed=ok");
        return 0;
    }

    /// <summary>打到通关，拿事务号。没这个号客户端领不了奖，重启后也不该补出来。</summary>
    static async Task<int> RunClearAsync(BattleLink link, ulong bossId, uint skillId)
    {
        var casts = 0;
        long hp = -1;
        BattleEnded ended = null;

        while (casts < MaxCasts)
        {
            await link.SendSkillAsync(skillId, bossId, NowMs());
            while (true)
            {
                var message = await link.ReceiveAsync(TimeSpan.FromSeconds(5));
                if (message.BodyCase == ServerMessage.BodyOneofCase.Result)
                {
                    casts++;
                    hp = message.Result.TargetHpAfter;
                    break;
                }

                if (message.BodyCase == ServerMessage.BodyOneofCase.Ended)
                {
                    // 结算结果和收尾是两条消息，先来的那条不能把循环带走。
                    ended = message.Ended;
                    hp = 0;
                    break;
                }

                if (message.BodyCase == ServerMessage.BodyOneofCase.Rejected)
                {
                    Debug.Log("p01d_fail=通关途中被拒：" + message.Rejected.Reason);
                    return 1;
                }
            }

            if (ended != null || hp <= 0)
            {
                break;
            }

            await Task.Delay(CooldownWaitMs);
        }

        // 收尾包可能紧跟在最后一条结果后面，还没取出来。
        if (ended == null)
        {
            ended = await ReadEndedAsync(link);
        }

        if (ended == null)
        {
            Debug.Log("p01d_fail=打完了没收到收尾包");
            return 1;
        }

        Debug.Log(
            "p01d_cleared=cleared=" + ended.Cleared
            + " session=" + ended.SessionId
            + " settlement_id=" + ended.SettlementId
            + " exp_gain=" + ended.ExpGain
            + " casts=" + casts);

        await link.CloseAsync();
        Debug.Log("p01d_closed=ok");
        return 0;
    }

    /// <summary>重启之后只是连上看一眼：血量该是满的，场次该是新的。</summary>
    static async Task<int> RunReopenAsync(BattleLink link)
    {
        // 开场包在 FlowAsync 里已经打印成 p01d_opened 了，这里把结论写清再走。
        Debug.Log("p01d_reopened=ok");
        await link.CloseAsync();
        Debug.Log("p01d_closed=ok");
        return 0;
    }

    // 异步方法不能带 out 参数，取不到就回 null。
    static async Task<BattleEnded> ReadEndedAsync(BattleLink link)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (link.TryReceive(out var message))
            {
                if (message.BodyCase == ServerMessage.BodyOneofCase.Ended)
                {
                    return message.Ended;
                }

                continue;
            }

            await Task.Delay(20);
        }

        return null;
    }

    // 前摇 600ms：160ms 打断，离结算还有一截；再等 1000ms 看有没有迟到的结算。
    const int CancelDelayMs = 160;
    const int ForeswingObserveMs = 1000;

    // 冷却 2000ms，等 2200ms 连后摇一起过去。
    const int CooldownWaitMs = 2200;

    const int MaxCasts = 12;

    static long NowMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
