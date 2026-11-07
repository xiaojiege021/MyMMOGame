using Mmo.Proto.Battle;

namespace Mmo.Tools;

/// <summary>
/// P0-1b 的核对客户端。一条连接一场，按 --case 跑一个场景，最后打一行 verdict。
/// 每个场景都独立开新连接，互不影响。
/// </summary>
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        var url = Arg(args, "--url");
        var name = Arg(args, "--case");
        if (url is null || name is null)
        {
            Console.WriteLine("verdict=fail reason=usage");
            return 2;
        }

        var boss = ulong.TryParse(Arg(args, "--boss"), out var b) ? b : 0UL;
        var ranged = uint.TryParse(Arg(args, "--ranged"), out var r) ? r : 0U;
        var melee = uint.TryParse(Arg(args, "--melee"), out var m) ? m : 0U;
        var unknown = uint.TryParse(Arg(args, "--unknown"), out var u) ? u : 9999U;

        try
        {
            return name switch
            {
                "open" => await CaseOpenAsync(url),
                "damage" => await CaseDamageAsync(url, boss, ranged),
                "cooldown" => await CaseCooldownAsync(url, boss, ranged),
                "casting" => await CaseCastingAsync(url, boss, ranged),
                "recovery" => await CaseRecoveryAsync(url, boss, ranged),
                "range" => await CaseRangeAsync(url, boss, melee),
                "cancel" => await CaseCancelAsync(url, boss, ranged),
                "reject" => await CaseRejectAsync(url, boss, ranged, unknown),
                "clear" => await CaseClearAsync(url, boss, ranged),
                _ => Fail($"没有这个场景：{name}"),
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.GetType().Name + "：" + ex.Message.Replace('\n', ' '));
        }
    }

    /// <summary>开场。血量、距离、双方编号对得上就算过。</summary>
    static async Task<int> CaseOpenAsync(string url)
    {
        await using var session = await Session.ConnectAsync(url);
        var opened = session.Opened;
        var ok = opened.BossHp == opened.BossHpMax
                 && opened.BossHpMax > 0
                 && opened.CasterHp == opened.CasterHpMax
                 && opened.DistanceMm > 0
                 && opened.SessionId > 0;

        return Report(ok,
            $"case=open session_id={opened.SessionId} caster={opened.CasterId} boss={opened.BossId} " +
            $"boss_hp={opened.BossHp} boss_hp_max={opened.BossHpMax} distance_mm={opened.DistanceMm}");
    }

    /// <summary>打一下。回包里的伤害、剩余血量、硬直、冷却都要对得上。</summary>
    static async Task<int> CaseDamageAsync(string url, ulong boss, uint ranged)
    {
        await using var session = await Session.ConnectAsync(url);
        var hpBefore = session.Opened.BossHp;

        await session.SubmitAsync(ranged, boss, Session.NowMs());
        var result = await session.ReadResultAsync(5000);

        var ok = result is not null
                 && result.Damage > 0
                 && result.Hit
                 && result.CasterId == session.Opened.CasterId
                 && result.TargetId == boss
                 && result.TargetHpAfter == hpBefore - result.Damage
                 && result.CooldownMs > 0;

        return Report(ok,
            $"case=damage hp_before={hpBefore} skill={result?.SkillId} damage={result?.Damage} " +
            $"hp_after={result?.TargetHpAfter} stagger_ms={result?.StaggerMs} cooldown_ms={result?.CooldownMs}");
    }

    /// <summary>完成标准一：还在冷却里再发一次，只有第一次生效。</summary>
    static async Task<int> CaseCooldownAsync(string url, ulong boss, uint ranged)
    {
        await using var session = await Session.ConnectAsync(url);

        await session.SubmitAsync(ranged, boss, Session.NowMs());
        var first = await session.ReadResultAsync(5000);

        // 等过后摇再发，这样拒绝的原因一定是冷却，不是后摇。
        await Task.Delay(700);

        await session.SubmitAsync(ranged, boss, Session.NowMs());
        var second = await session.ReadRejectedAsync(3000);

        var ok = first is { Damage: > 0 }
                 && second?.Reason == SkillRejectReason.SkillRejectCooldown
                 && second.RetryAfterMs > 0;

        return Report(ok,
            $"case=cooldown first_damage={first?.Damage} second_reason={second?.Reason} " +
            $"retry_after_ms={second?.RetryAfterMs}");
    }

    /// <summary>一次只结算一个：前摇里再发，后一次要被挡住，第一次照常结算。</summary>
    static async Task<int> CaseCastingAsync(string url, ulong boss, uint ranged)
    {
        await using var session = await Session.ConnectAsync(url);

        await session.SubmitAsync(ranged, boss, Session.NowMs());
        await Task.Delay(150);
        await session.SubmitAsync(ranged, boss, Session.NowMs());

        var second = await session.ReadRejectedAsync(3000);
        var first = await session.ReadResultAsync(5000);

        var ok = second?.Reason == SkillRejectReason.SkillRejectCasting
                 && first is { Damage: > 0 };

        return Report(ok,
            $"case=casting second_reason={second?.Reason} first_damage={first?.Damage}");
    }

    /// <summary>后摇没走完不能接下一个。</summary>
    static async Task<int> CaseRecoveryAsync(string url, ulong boss, uint ranged)
    {
        await using var session = await Session.ConnectAsync(url);

        await session.SubmitAsync(ranged, boss, Session.NowMs());
        var first = await session.ReadResultAsync(5000);

        // 回包刚到，后摇从现在开始。
        await session.SubmitAsync(ranged, boss, Session.NowMs());
        var second = await session.ReadRejectedAsync(3000);

        var ok = first is { Damage: > 0 } && second?.Reason == SkillRejectReason.SkillRejectRecovery;

        return Report(ok,
            $"case=recovery first_damage={first?.Damage} second_reason={second?.Reason}");
    }

    /// <summary>距离不够就拒绝，而且不改目标生命。</summary>
    static async Task<int> CaseRangeAsync(string url, ulong boss, uint melee)
    {
        await using var session = await Session.ConnectAsync(url);
        var hpBefore = session.Opened.BossHp;

        await session.SubmitAsync(melee, boss, Session.NowMs());
        var rejected = await session.ReadRejectedAsync(3000);

        // 再等一会儿，确认没有一个迟到的结算把血扣掉。
        var late = await session.ReadAsync(1500);

        var ok = rejected?.Reason == SkillRejectReason.SkillRejectOutOfRange
                 && late is null
                 && session.Opened.BossHp == hpBefore;

        return Report(ok,
            $"case=range distance_mm={session.Opened.DistanceMm} reason={rejected?.Reason} " +
            $"hp_before={hpBefore} late_message={(late is null ? "none" : late.BodyCase.ToString())}");
    }

    /// <summary>
    /// 完成标准二：前摇里取消，这次施法没有伤害；取消以后冷却退回去，马上还能再放。
    /// </summary>
    static async Task<int> CaseCancelAsync(string url, ulong boss, uint ranged)
    {
        await using var session = await Session.ConnectAsync(url);
        var hpBefore = session.Opened.BossHp;

        var castTime = Session.NowMs();
        await session.SubmitAsync(ranged, boss, castTime);
        await Task.Delay(150);
        await session.CancelAsync(ranged, castTime);

        var cancelled = (await session.ReadAsync(3000))?.Cancelled;

        // 原本该结算的时刻早就过了，这里确认没有结算。
        var late = await session.ReadAsync(1500);

        // 冷却如果没有退回去，这一下会被判 COOLDOWN。
        await session.SubmitAsync(ranged, boss, Session.NowMs());
        var again = await session.ReadResultAsync(5000);

        var ok = cancelled is { Accepted: true }
                 && late is null
                 && again is { Damage: > 0 }
                 && again.TargetHpAfter == hpBefore - again.Damage;

        return Report(ok,
            $"case=cancel accepted={cancelled?.Accepted} late_message={(late is null ? "none" : late.BodyCase.ToString())} " +
            $"hp_before={hpBefore} again_damage={again?.Damage} again_hp_after={again?.TargetHpAfter}");
    }

    /// <summary>两种不该放出去的东西：编的技能号、对不上的目标号。</summary>
    static async Task<int> CaseRejectAsync(string url, ulong boss, uint ranged, uint unknown)
    {
        await using var session = await Session.ConnectAsync(url);
        var hpBefore = session.Opened.BossHp;

        await session.SubmitAsync(unknown, boss, Session.NowMs());
        var unknownRejected = await session.ReadRejectedAsync(3000);

        await session.SubmitAsync(ranged, boss + 12345, Session.NowMs());
        var targetRejected = await session.ReadRejectedAsync(3000);

        var late = await session.ReadAsync(1200);

        var ok = unknownRejected?.Reason == SkillRejectReason.SkillRejectUnknownSkill
                 && targetRejected?.Reason == SkillRejectReason.SkillRejectTargetGone
                 && late is null
                 && session.Opened.BossHp == hpBefore;

        return Report(ok,
            $"case=reject unknown_reason={unknownRejected?.Reason} target_reason={targetRejected?.Reason} " +
            $"hp_before={hpBefore} late_message={(late is null ? "none" : late.BodyCase.ToString())}");
    }

    /// <summary>打到通关，结算要给出事务号。进程没写成就没有号，客户端拿不到。</summary>
    static async Task<int> CaseClearAsync(string url, ulong boss, uint ranged)
    {
        await using var session = await Session.ConnectAsync(url);
        var hp = session.Opened.BossHp;
        var casts = 0;
        BattleEnded? ended = null;

        for (var attempt = 0; attempt < 40 && ended is null; attempt++)
        {
            await session.SubmitAsync(ranged, boss, Session.NowMs());
            var message = await session.ReadAsync(6000);
            if (message is null)
            {
                break;
            }

            if (message.Ended is not null)
            {
                ended = message.Ended;
                break;
            }

            if (message.Result is not null)
            {
                casts++;
                hp = message.Result.TargetHpAfter;

                // 打死那一下后面紧跟一条结束消息，得当场读，不能等到下一轮再提交。
                if (hp <= 0)
                {
                    ended = (await session.ReadAsync(3000))?.Ended;
                    break;
                }

                continue;
            }

            if (message.Rejected is not { } rejected)
            {
                break;
            }

            if (rejected.Reason == SkillRejectReason.SkillRejectCooldown)
            {
                await Task.Delay((int)rejected.RetryAfterMs + 40);
                continue;
            }

            if (rejected.Reason == SkillRejectReason.SkillRejectRecovery)
            {
                await Task.Delay(500);
                continue;
            }

            break;
        }

        // 这场已经结算完，再发一个不该再结算。
        SkillRejected? afterEnd = null;
        if (ended is not null)
        {
            await session.SubmitAsync(ranged, boss, Session.NowMs());
            afterEnd = await session.ReadRejectedAsync(3000);
        }

        var ok = ended is { Cleared: true }
                 && hp <= 0
                 && !string.IsNullOrEmpty(ended.SettlementId)
                 && ended.ExpGain > 0
                 && afterEnd?.Reason == SkillRejectReason.SkillRejectNotInBattle;

        return Report(ok,
            $"case=clear casts={casts} hp_left={hp} cleared={ended?.Cleared} " +
            $"settlement_id={ended?.SettlementId} exp_gain={ended?.ExpGain} " +
            $"after_end_reason={afterEnd?.Reason}");
    }

    static int Report(bool ok, string line)
    {
        Console.WriteLine(line);
        Console.WriteLine(ok ? "verdict=ok" : "verdict=fail");
        return ok ? 0 : 1;
    }

    static int Fail(string detail)
    {
        Console.WriteLine("detail=" + detail);
        Console.WriteLine("verdict=fail");
        return 1;
    }

    static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
