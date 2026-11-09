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
        var party = Arg(args, "--party");
        var warriorChar = ulong.TryParse(Arg(args, "--warrior-char"), out var wc) ? wc : 2UL;
        var priestChar = ulong.TryParse(Arg(args, "--priest-char"), out var pc) ? pc : 3UL;
        var hit = uint.TryParse(Arg(args, "--hit"), out var h) ? h : 1002U;
        var resurrect = uint.TryParse(Arg(args, "--resurrect"), out var rs) ? rs : 1303U;
        var second = Arg(args, "--second") ?? "skip";
        var waitMs = int.TryParse(Arg(args, "--wait-ms"), out var wm) ? wm : 0;

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
                "p03a_classes" => await CaseP03aClassesAsync(url),
                "p03a_tank" => await CaseP03aTankAsync(url, boss, hit),
                "p03a_mana" => await CaseP03aManaAsync(url, boss, hit),
                "p03a_resurrect" => await CaseP03aResurrectAsync(url, boss, party, warriorChar, priestChar, hit, resurrect, second),
                "p03b_phase" => await CaseP03bPhaseAsync(url, boss, ranged),
                "p11a2_connect" => await CaseP11a2ConnectAsync(url, boss, hit, waitMs),
                "p11a2_reject" => await CaseP11a2RejectAsync(url),
                "p11a2_expire" => await CaseP11a2ExpireAsync(url, boss, hit, waitMs),
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

    // ---- P0-3a / P0-3b 的场景 ----

    static string WithQuery(string url, string query) => url + (url.Contains('?') ? "&" : "?") + query;

    /// <summary>
    /// 五个职业挨个连一场：蓝量上限排序（法牧最高、射手次之、战士再次、坦克最低）、
    /// 生命来自体力、技能数对得上。职业配置里精神全配成同一个值，
    /// 「同一精神下法师高于坦克」就在这一轮里直接比出来。
    /// </summary>
    static async Task<int> CaseP03aClassesAsync(string url)
    {
        var order = new[] { "mage", "priest", "archer", "warrior", "tank" };
        var mana = new Dictionary<string, long>();
        var ok = true;

        foreach (var id in order)
        {
            await using var session = await Session.ConnectAsync(WithQuery(url, "class=" + id));
            var opened = session.Opened;
            mana[id] = opened.ManaMax;

            var classOk = opened.ClassId == id
                          && opened.Mana == opened.ManaMax
                          && opened.CasterHp == opened.CasterHpMax
                          && opened.CasterHpMax > 0
                          && opened.Skills.Count == 3
                          && opened.Classes.Count == 5;
            ok &= classOk;

            Console.WriteLine($"p03a_class={id} name={opened.ClassName}"
                + $" mana_max={opened.ManaMax} hp_max={opened.CasterHpMax}"
                + $" skills={opened.Skills.Count} classes={opened.Classes.Count}");
        }

        var orderOk = mana["mage"] == mana["priest"]
                      && mana["mage"] > mana["archer"]
                      && mana["archer"] > mana["warrior"]
                      && mana["warrior"] > mana["tank"]
                      && mana["mage"] > mana["tank"];
        ok &= orderOk;

        Console.WriteLine(
            $"p03a_mana_order mage={mana["mage"]} priest={mana["priest"]} archer={mana["archer"]}"
            + $" warrior={mana["warrior"]} tank={mana["tank"]}");

        return Report(ok, $"p03a_classes same_spirit_mage_vs_tank={mana["mage"]}_vs_{mana["tank"]}");
    }

    /// <summary>
    /// 坦克打一下：伤害只来自装备固定攻击和技能倍率（体力不进入攻击），
    /// 回包里带累计有效仇恨（伤害 + 体力 × 系数）。两次不同体力配置的跑法
    /// 由脚本比对：伤害要一样，仇恨要涨。
    /// </summary>
    static async Task<int> CaseP03aTankAsync(string url, ulong boss, uint skill)
    {
        await using var session = await Session.ConnectAsync(WithQuery(url, "class=tank"));

        await session.SubmitAsync(skill, boss, Session.NowMs());
        var result = await session.ReadResultAsync(5000);

        var ok = result is { Hit: true, Damage: > 0 }
                 && result.ThreatAfter > result.Damage;

        return Report(ok,
            $"p03a_tank damage={result?.Damage} threat={result?.ThreatAfter} mana={result?.ManaAfter}");
    }

    /// <summary>法师连放两个耗蓝技能：第一个结算，第二个蓝不够被拒。用小蓝量的配置跑。</summary>
    static async Task<int> CaseP03aManaAsync(string url, ulong boss, uint skill)
    {
        await using var session = await Session.ConnectAsync(WithQuery(url, "class=mage"));

        await session.SubmitAsync(skill, boss, Session.NowMs());
        var first = await session.ReadResultAsync(5000);

        // 过后摇再发，这样拒绝的原因一定是蓝，不是后摇。
        await Task.Delay(700);
        await session.SubmitAsync(skill, boss, Session.NowMs());
        var second = await session.ReadRejectedAsync(3000);

        var ok = first is { Hit: true, ManaAfter: >= 0 }
                 && second?.Reason == SkillRejectReason.SkillRejectNoMana;

        return Report(ok,
            $"p03a_mana first_mana_after={first?.ManaAfter} second_reason={second?.Reason}");
    }

    /// <summary>
    /// 牧师复活：战士挨打倒地，牧师拉起来；对没倒地的人发复活和对自已发都要被拒。
    /// 第二段按 --second 的期望走：ok（蓝还够，再拉一次）或 no_mana（蓝不够，拉不起来）。
    /// 经验扣没扣、扣多少，由脚本读角色那张表来对。
    /// </summary>
    static async Task<int> CaseP03aResurrectAsync(
        string url, ulong boss, string? party, ulong warriorChar, ulong priestChar,
        uint hit, uint resurrect, string second)
    {
        var key = string.IsNullOrEmpty(party) ? "p03a-" + Guid.NewGuid().ToString("N")[..8] : party;

        await using var warrior = await Session.ConnectAsync(
            WithQuery(url, $"party={key}&class=warrior&char={warriorChar}"));
        await using var priest = await Session.ConnectAsync(
            WithQuery(url, $"party={key}&class=priest&char={priestChar}"));

        // 战士先打一下，把仇恨拉过去，Boss 会追着他打。
        // 他的队列里还压着牧师进场的广播，按条件挑自己那一下。
        await warrior.SubmitAsync(hit, boss, Session.NowMs());
        var hitResult = (await warrior.ReadUntilAsync(
            m => m.Result is not null && m.Result.CasterId == warriorChar && m.Result.SkillId == hit, 5000))?.Result;

        // 等 Boss 把战士打倒。
        var down = await priest.ReadUntilAsync(
            m => m.MemberUpdated?.Member?.MemberId == warriorChar && m.MemberUpdated.Member.Down, 10000);

        // 牧师拉他。
        await priest.SubmitAsync(resurrect, warriorChar, Session.NowMs());
        var raised = (await priest.ReadUntilAsync(
            m => m.Result is not null && m.Result.CasterId == priestChar && m.Result.SkillId == resurrect, 5000))?.Result;
        var up = await priest.ReadUntilAsync(
            m => m.MemberUpdated?.Member?.MemberId == warriorChar && !m.MemberUpdated.Member.Down, 5000);

        // 过牧师的后摇，再试两个不该成的目标：没倒地的队友、自己。
        await Task.Delay(700);
        await priest.SubmitAsync(resurrect, warriorChar, Session.NowMs());
        var badTarget = await priest.ReadRejectedAsync(3000);
        await priest.SubmitAsync(resurrect, priestChar, Session.NowMs());
        var selfTarget = await priest.ReadRejectedAsync(3000);

        var ok = hitResult is { Hit: true }
                 && down is not null
                 && raised is { Hit: true, Healing: > 0, TargetHpAfter: > 0 }
                 && up is not null
                 && badTarget?.Reason == SkillRejectReason.SkillRejectBadTarget
                 && selfTarget?.Reason == SkillRejectReason.SkillRejectBadTarget;

        Console.WriteLine($"p03a_resurrect hit={hitResult?.Damage}"
            + $" down={down?.MemberUpdated?.Member?.Down}"
            + $" restore={raised?.Healing} hp_after={raised?.TargetHpAfter}"
            + $" bad_target={badTarget?.Reason} self_target={selfTarget?.Reason}");

        if (second == "skip")
        {
            return Report(ok, "p03a_resurrect first_only");
        }

        // 等 Boss 再次把战士打倒，再发一次复活。
        var downAgain = await priest.ReadUntilAsync(
            m => m.MemberUpdated?.Member?.MemberId == warriorChar && m.MemberUpdated.Member.Down, 10000);

        await Task.Delay(700);
        await priest.SubmitAsync(resurrect, warriorChar, Session.NowMs());

        if (second == "no_mana")
        {
            var noMana = await priest.ReadRejectedAsync(3000);
            ok &= downAgain is not null
                  && noMana?.Reason == SkillRejectReason.SkillRejectNoMana;
            Console.WriteLine($"p03a_resurrect_second expect=no_mana reason={noMana?.Reason}");
        }
        else
        {
            var raisedAgain = (await priest.ReadUntilAsync(
                m => m.Result is not null && m.Result.CasterId == priestChar && m.Result.SkillId == resurrect, 5000))?.Result;
            ok &= downAgain is not null
                  && raisedAgain is { Hit: true, TargetHpAfter: > 0 };
            Console.WriteLine($"p03a_resurrect_second expect=ok restore={raisedAgain?.Healing} hp_after={raisedAgain?.TargetHpAfter}");
        }

        return Report(ok, "p03a_resurrect " + second);
    }

    /// <summary>
    /// Boss 两阶段：打到血线触发阶段广播，切了阶段以后 Boss 这一下的伤害换成新阶段配置的。
    /// 血线切在哪、两阶段各打多痛，全由配置说了算，脚本改配置再跑一遍来对。
    /// 自己的血全程跟着 MemberUpdated 走：第一阶段的先手也会打人，不能拿开场值当基准。
    /// </summary>
    static async Task<int> CaseP03bPhaseAsync(string url, ulong boss, uint skill)
    {
        await using var session = await Session.ConnectAsync(url);
        var hp = session.Opened.BossHp;
        var myHp = session.Opened.CasterHp;
        var me = session.Opened.CasterId;

        BossPhase? phase = null;
        long attackDamage = -1;
        var casts = 0;
        var nextCastAt = 0L;
        var deadline = Environment.TickCount64 + 40000;
        var idleSpins = 0;

        while (Environment.TickCount64 < deadline)
        {
            if (phase is not null && attackDamage >= 0)
            {
                break;
            }

            // 半分钟没动静就放弃，别把核对挂死在这。
            if (idleSpins > 60)
            {
                break;
            }

            if (phase is null && nextCastAt >= 0 && Environment.TickCount64 >= nextCastAt)
            {
                await session.SubmitAsync(skill, boss, Session.NowMs());
                casts++;
                nextCastAt = -1; // 等这一手有结果再排下一手
                idleSpins = 0;
            }

            // 短读：等冷却的时候也要回来排下一手，不能在 ReadAsync 里睡过头。
            var message = await session.ReadAsync(500);
            if (message is null)
            {
                idleSpins++;
                continue;
            }
            idleSpins = 0;

            if (message.MemberUpdated?.Member is { } member && member.MemberId == me)
            {
                if (phase is not null && attackDamage < 0 && myHp > member.Hp)
                {
                    attackDamage = myHp - member.Hp;
                }
                myHp = member.Hp;
                continue;
            }

            if (message.Result is { } result)
            {
                hp = result.TargetHpAfter;
                nextCastAt = Environment.TickCount64 + result.CooldownMs + 150;
                continue;
            }

            if (message.BossPhase is { } p)
            {
                phase = p;
                continue;
            }

            if (message.Rejected is { } rejected)
            {
                nextCastAt = Environment.TickCount64 + rejected.RetryAfterMs + 150;
                continue;
            }
        }

        if (phase is null)
        {
            return Report(false, $"p03b_phase no_phase_message casts={casts} hp={hp}");
        }

        Console.WriteLine($"p03b_phase index={phase.PhaseIndex} name={phase.Name}"
            + $" hp_after_switch={hp} casts={casts}"
            + $" attack_damage={attackDamage} hp_left={myHp}");

        var ok = phase.PhaseIndex == 1
                 && hp >= 0
                 && attackDamage > 0;

        return Report(ok, $"p03b_phase index={phase.PhaseIndex} attack={attackDamage}");
    }

    /// <summary>
    /// P1-1a2：带令牌从网关连进去，转发链路要通——出一手，能收到战斗服的回包。
    /// 伤害或拒绝都算通，这一条验的是「网关把字节流转过去了」，不是战斗规则。
    /// </summary>
    static async Task<int> CaseP11a2ConnectAsync(string url, ulong boss, uint skill, int waitMs)
    {
        await using var session = await Session.ConnectAsync(url);
        if (waitMs > 0)
        {
            await Task.Delay(waitMs);
        }

        await session.SubmitAsync(skill, boss, Session.NowMs());
        var message = await session.ReadUntilAsync(
            m => m.Result is not null || m.Rejected is not null,
            5000);

        var ok = message is not null;
        return Report(ok,
            "case=p11a2_connect caster=" + session.Opened.CasterId
            + " wait_ms=" + waitMs
            + " got=" + (message?.BodyCase.ToString() ?? "none")
            + (message?.Result is { } r ? " damage=" + r.Damage : "")
            + (message?.Rejected is { } j ? " reason=" + j.Reason : ""));
    }

    /// <summary>P1-1a2：没令牌、伪造令牌、过期令牌，连接就该建不起来。</summary>
    static async Task<int> CaseP11a2RejectAsync(string url)
    {
        try
        {
            await using var session = await Session.ConnectAsync(url);
            return Report(false, "case=p11a2_reject connected session=" + session.Opened.SessionId);
        }
        catch (Exception ex)
        {
            return Report(true, "case=p11a2_reject refused=" + ex.GetType().Name);
        }
    }

    /// <summary>
    /// P1-1a2：连上的时候令牌还有效，等它过期再发技能，必须收不到任何回包——
    /// 网关会在转发这一帧之前把连接按策略违规关掉。
    /// </summary>
    static async Task<int> CaseP11a2ExpireAsync(string url, ulong boss, uint skill, int waitMs)
    {
        await using var session = await Session.ConnectAsync(url);
        await Task.Delay(waitMs);

        await session.SubmitAsync(skill, boss, Session.NowMs());
        var message = await session.ReadUntilAsync(
            m => m.Result is not null || m.Rejected is not null,
            3000);

        var ok = message is null;
        return Report(ok,
            "case=p11a2_expire wait_ms=" + waitMs
            + " got=" + (message?.BodyCase.ToString() ?? "none"));
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
