using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Mmo.Net;
using Mmo.Proto.Battle;
using UnityEditor;
using UnityEngine;
// System.Diagnostics 里也有 Debug，这里只要 Stopwatch，日志一律走 Unity 的。
using Debug = UnityEngine.Debug;

// P0-1c 核对用：Unity 这一侧真的连上战斗进程，把请求发出去，把结果收回来。
// 地址从环境变量给，脚本起进程后把端口塞进来，客户端不写死端口。
// 打印格式：p01c_ 开头的 kv 行，由 tools/check/check-p0-1c.sh 逐项断言。
public static class P01cCheck
{
    public static void Run()
    {
        var url = Environment.GetEnvironmentVariable("MMO_BATTLE_URL") ?? string.Empty;
        var deadUrl = Environment.GetEnvironmentVariable("MMO_BATTLE_DEAD_URL") ?? string.Empty;
        var skillText = Environment.GetEnvironmentVariable("MMO_BATTLE_SKILL") ?? string.Empty;
        var skillId = uint.TryParse(skillText, out var parsed) ? parsed : 1002u;

        int code;
        try
        {
            // 整个流程放到线程池上跑：主线程要一直等到它结束，
            // 如果在主线程 await，续体排回主线程就自己等自己了。
            code = Task.Run(() => FlowAsync(url, deadUrl, skillId)).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Debug.Log("p01c_fail=" + e.GetType().Name + " " + e.Message);
            code = 1;
        }

        Debug.Log("p01c_exit=" + code);
        EditorApplication.Exit(code);
    }

    static async Task<int> FlowAsync(string url, string deadUrl, uint skillId)
    {
        if (!await CheckUnreachable(deadUrl))
        {
            return 1;
        }

        if (string.IsNullOrEmpty(url))
        {
            Debug.Log("p01c_fail=没有给 MMO_BATTLE_URL");
            return 1;
        }

        var link = await BattleLink.ConnectAsync(url, TimeSpan.FromSeconds(5));
        try
        {
            var openedMessage = await link.ReceiveAsync(TimeSpan.FromSeconds(5));
            if (openedMessage.BodyCase != ServerMessage.BodyOneofCase.Opened)
            {
                Debug.Log("p01c_fail=第一条不是开场包，是 " + openedMessage.BodyCase);
                return 1;
            }

            var opened = openedMessage.Opened;
            Debug.Log(
                "p01c_opened=session=" + opened.SessionId
                + " caster=" + opened.CasterId
                + " boss=" + opened.BossId
                + " boss_hp=" + opened.BossHp
                + " boss_hp_max=" + opened.BossHpMax
                + " caster_hp=" + opened.CasterHp
                + " caster_hp_max=" + opened.CasterHpMax
                + " distance_mm=" + opened.DistanceMm);

            // 第一下：够得着，应当命中。
            var watch = Stopwatch.StartNew();
            await link.SendSkillAsync(skillId, opened.BossId, NowMs());
            var first = await link.ReceiveAsync(TimeSpan.FromSeconds(5));
            watch.Stop();

            if (first.BodyCase != ServerMessage.BodyOneofCase.Result)
            {
                Debug.Log("p01c_fail=第一下没拿到结果，是 " + first.BodyCase + " " + first);
                return 1;
            }

            var result = first.Result;
            Debug.Log(
                "p01c_result=skill=" + result.SkillId
                + " caster=" + result.CasterId
                + " target=" + result.TargetId
                + " hit=" + result.Hit
                + " damage=" + result.Damage
                + " healing=" + result.Healing
                + " hp_after=" + result.TargetHpAfter
                + " stagger_ms=" + result.StaggerMs
                + " cooldown_ms=" + result.CooldownMs
                + " elapsed_ms=" + watch.ElapsedMilliseconds);

            // 第二下：等后摇走完、冷却还没好，这时候发必然被拒。
            await Task.Delay(RecoveryWaitMs);
            await link.SendSkillAsync(skillId, opened.BossId, NowMs());
            var second = await link.ReceiveAsync(TimeSpan.FromSeconds(3));
            if (second.BodyCase != ServerMessage.BodyOneofCase.Rejected)
            {
                Debug.Log("p01c_fail=第二下没被拒，是 " + second.BodyCase);
                return 1;
            }

            var rejected = second.Rejected;
            Debug.Log(
                "p01c_rejected=skill=" + rejected.SkillId
                + " target=" + rejected.TargetId
                + " reason=" + rejected.Reason
                + " retry_after_ms=" + rejected.RetryAfterMs);

            // 被拒之后不该有迟到的结算，血量也不该再动。
            await Task.Delay(300);
            var leak = 0;
            while (link.TryReceive(out var late))
            {
                if (late.BodyCase == ServerMessage.BodyOneofCase.Result)
                {
                    leak++;
                }
            }

            Debug.Log("p01c_reject_leak=" + leak);

            // 服务端那边还活着就说明连接没提前断。
            Debug.Log("p01c_link_open=" + link.IsOpen);

            await link.CloseAsync();
            Debug.Log("p01c_closed=ok");
            return 0;
        }
        finally
        {
            link.Dispose();
        }
    }

    // 后摇 400ms，留点余量。冷却 2000ms，这会儿还在冷却里。
    const int RecoveryWaitMs = 700;

    static async Task<bool> CheckUnreachable(string deadUrl)
    {
        if (string.IsNullOrEmpty(deadUrl))
        {
            Debug.Log("p01c_connect_fail=skipped");
            return true;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            using (var dead = await BattleLink.ConnectAsync(deadUrl, TimeSpan.FromSeconds(2)))
            {
                watch.Stop();
                Debug.Log("p01c_connect_fail=connected_unexpectedly elapsed_ms=" + watch.ElapsedMilliseconds);
                return false;
            }
        }
        catch (Exception e)
        {
            watch.Stop();
            Debug.Log("p01c_connect_fail=" + e.GetType().Name + " elapsed_ms=" + watch.ElapsedMilliseconds);
            return watch.ElapsedMilliseconds < 3000;
        }
    }

    static long NowMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
