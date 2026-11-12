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
        var holdMs = int.TryParse(Arg(args, "--hold-ms"), out var hm) ? hm : 0;
        var aChar = ulong.TryParse(Arg(args, "--a-char"), out var ac) ? ac : 1UL;
        var bChar = ulong.TryParse(Arg(args, "--b-char"), out var bc) ? bc : 2UL;
        var partySize = int.TryParse(Arg(args, "--party-size"), out var ps) ? ps : 2;
        var item = Arg(args, "--item") ?? "talisman_red";
        var otherItem = Arg(args, "--other") ?? "ticket_protect";
        var healSkill = uint.TryParse(Arg(args, "--heal"), out var hs) ? hs : 1302U;
        var blue = Arg(args, "--blue") ?? "talisman_blue";
        var manaSkill = uint.TryParse(Arg(args, "--skill"), out var ms) ? ms : 1002U;

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
                "p12a_join" => await CaseP12aJoinAsync(url, Arg(args, "--expect") ?? "open", holdMs),
                "p12c_boss_hit" => await CaseP12cBossHitAsync(url),
                "p12c_player_damage" => await CaseP12cPlayerDamageAsync(url, boss, hit),
                "p12d_clear" => await CaseP12dClearAsync(url, boss, hit, aChar, bChar, partySize),
                "p15c3_talisman" => await CaseP15c3TalismanAsync(url, item),
                "p15c3_talisman_full" => await CaseP15c3FullAsync(url, item),
                "p15c3_talisman_other" => await CaseP15c3OtherAsync(url, otherItem),
                "p15c3_talisman_down" => await CaseP15c3DownAsync(url, item),
                "p15c3_talisman_off" => await CaseP15c3OffAsync(url, item),
                "p15c3_talisman_heal_share" => await CaseP15c3HealShareAsync(url, item, healSkill),
                "p15c3_talisman_none" => await CaseP15c3NoneAsync(url, item),
                "p15c5_blue" => await CaseP15c5BlueAsync(url, blue, manaSkill),
                "p15c5_blue_full" => await CaseP15c5FullAsync(url, blue),
                "p15c5_blue_twice" => await CaseP15c5TwiceAsync(url, blue, manaSkill),
                "p15c5_red_blue_both" => await CaseP15c5BothAsync(url, item, blue, manaSkill),
                "p15c5_blue_damage" => await CaseP15c5DamageAsync(url, blue, manaSkill),
                "p15c5_blue_other" => await CaseP15c5OtherAsync(url, otherItem),
                "p15c5_blue_down" => await CaseP15c5DownAsync(url, blue),
                "p15c5_blue_none" => await CaseP15c5NoneAsync(url, blue, manaSkill),
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

    /// <summary>
    /// P1-2a：进本。带同一个 party 的第一个人应拿到 Opened，第二个人应拿到 EnterRejected。
    /// --expect open|reject 说明这次期望哪一种；不符就是不过。
    /// 这里不用 Session：它要求第一条必须是 Opened，被拒的连接根本不是那种形状。
    /// </summary>
    static async Task<int> CaseP12aJoinAsync(string url, string expect, int holdMs)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new System.Net.WebSockets.ClientWebSocket();

        try
        {
            await socket.ConnectAsync(new Uri(url), cts.Token);
        }
        catch (Exception ex)
        {
            // 连接都建不起来：只有期望「被拒」时才算数（握手阶段就关）。
            return expect == "reject"
                ? Report(true, "case=p12a_join refused_at_handshake=" + ex.GetType().Name)
                : Report(false, "case=p12a_join connect_failed=" + ex.GetType().Name);
        }

        var buffer = new byte[8192];
        using var assembled = new MemoryStream();
        ServerMessage? first = null;
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (received.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    break;
                }

                assembled.Write(buffer, 0, received.Count);
                if (received.EndOfMessage)
                {
                    first = ServerMessage.Parser.ParseFrom(assembled.ToArray());
                    break;
                }
            }
        }
        catch (Exception)
        {
            // 对面直接关了，first 仍然为空。
        }

        if (first is null)
        {
            return expect == "reject"
                ? Report(true, "case=p12a_join closed_without_message")
                : Report(false, "case=p12a_join closed_without_opened");
        }

        if (first.EnterRejected is { } rejected)
        {
            var ok = expect == "reject";
            return Report(ok,
                "case=p12a_join result=rejected reason=" + rejected.Reason
                + " limit=" + rejected.Limit
                + " current=" + rejected.Current
                + " dungeon=" + rejected.DungeonId);
        }

        if (first.Opened is { } opened)
        {
            // 先打一行「进来了」，再挂住连接：核对靠这一行知道第一个人已经占上场了。
            Console.WriteLine("joined=ok session=" + opened.SessionId + " caster=" + opened.CasterId);

            // 保持连接：核对要在这条连接还活着的时候让第二个人来撞上限。
            if (holdMs > 0)
            {
                try
                {
                    await Task.Delay(holdMs, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            var ok = expect == "open";
            return Report(ok,
                "case=p12a_join result=opened session=" + opened.SessionId
                + " caster=" + opened.CasterId
                + " hold_ms=" + holdMs
                + " party_expected=" + expect);
        }

        return Report(false, "case=p12a_join first=" + first.BodyCase);
    }

    /// <summary>
    /// P1-2c：读开场里的怪物生命上限，再等 Boss 打一下量出它的伤害。
    /// 核对脚本带不同 party_size 连两场，比对这两个数与玩家技能伤害。
    /// 这里只读，不主动出手，玩家技能机制保持原样。
    /// </summary>
    static async Task<int> CaseP12cBossHitAsync(string url)
    {
        await using var session = await Session.ConnectAsync(url);
        var opened = session.Opened;
        var me = opened.CasterId;
        var hp = opened.CasterHp;
        long bossDamage = -1;
        var deadline = Environment.TickCount64 + 12000;

        while (Environment.TickCount64 < deadline && bossDamage < 0)
        {
            var message = await session.ReadAsync(500);
            if (message?.MemberUpdated?.Member is not { } member || member.MemberId != me)
            {
                continue;
            }

            if (member.Hp < hp)
            {
                bossDamage = hp - member.Hp;
            }
            hp = member.Hp;
        }

        return Report(bossDamage > 0,
            $"case=p12c_boss_hit boss_hp={opened.BossHp} boss_hp_max={opened.BossHpMax} "
            + $"caster_hp={opened.CasterHp} boss_damage={bossDamage}");
    }

    /// <summary>
    /// P1-2c：玩家技能伤害不随进本人数变化（缩放只作用于怪物生命与伤害）。
    /// Boss 同时也在出手，所以要跳过广播挑自己那一发结算。
    /// </summary>
    static async Task<int> CaseP12cPlayerDamageAsync(string url, ulong boss, uint skill)
    {
        await using var session = await Session.ConnectAsync(url);
        await session.SubmitAsync(skill, boss, Session.NowMs());
        var message = await session.ReadUntilAsync(
            m => m.Result is not null || m.Rejected is not null, 6000);
        var result = message?.Result;

        return Report(result is { Hit: true, Damage: > 0 },
            $"case=p12c_player_damage boss_hp_max={session.Opened.BossHpMax} "
            + $"skill={result?.SkillId} damage={result?.Damage} "
            + $"hp_after={result?.TargetHpAfter} reject={message?.Rejected?.Reason.ToString() ?? "none"}");
    }

    /// <summary>
    /// P1-2d：两个人进同一场打完 Boss。两个连接都保持在场，战斗服通关结算时
    /// 会给每人各记一笔经验；这里只拿结算号与伤害，入账结果由核对脚本查库。
    /// 跳过队友进场的广播，挑自己那一发的 Result 与最后的 Ended。
    /// </summary>
    static async Task<int> CaseP12dClearAsync(
        string url, ulong boss, uint skill, ulong aChar, ulong bChar, int partySize)
    {
        var key = "p12d-" + Guid.NewGuid().ToString("N")[..8];
        var common = $"party={key}&party_size={partySize}&class=warrior";
        await using var a = await Session.ConnectAsync(WithQuery(url, common + "&char=" + aChar));
        await using var b = await Session.ConnectAsync(WithQuery(url, common + "&char=" + bChar));

        var hp = a.Opened.BossHp;
        long firstDamage = 0;
        var casts = 0;
        BattleEnded? ended = null;

        for (var attempt = 0; attempt < 80 && ended is null; attempt++)
        {
            await a.SubmitAsync(skill, boss, Session.NowMs());
            var message = await a.ReadUntilAsync(
                m => m.Result is not null || m.Ended is not null || m.Rejected is not null, 6000);
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
                if (firstDamage == 0)
                {
                    firstDamage = message.Result.Damage;
                }

                hp = message.Result.TargetHpAfter;
                if (hp <= 0)
                {
                    // 结算要同步等角色服写完才回 Ended，留足一次建连的余量。
                    ended = (await a.ReadUntilAsync(m => m.Ended is not null, 8000))?.Ended;
                    break;
                }

                continue;
            }

            var rejected = message.Rejected!;
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

        var ok = ended is { Cleared: true }
                 && !string.IsNullOrEmpty(ended.SettlementId)
                 && firstDamage > 0
                 && hp <= 0;

        return Report(ok,
            $"case=p12d_clear party_size={partySize} a={aChar} b={bChar} "
            + $"casts={casts} damage={firstDamage} hp_left={hp} "
            + $"settlement_id={ended?.SettlementId} exp_gain={ended?.ExpGain} cleared={ended?.Cleared}");
    }

    // ---- P1-5c3 红符 ----

    /// <summary>
    /// 等到自己被 Boss 打掉血（wantDown 为真时等到倒地）。
    /// 回的是这一条更新里的血量，场景代码拿它对齐用符之后的血量。
    /// </summary>
    static async Task<PartyMember?> WaitDamagedAsync(Session session, int timeoutMs, bool wantDown)
    {
        var me = session.Opened.CasterId;
        var message = await session.ReadUntilAsync(
            m => m.MemberUpdated is not null
                 && m.MemberUpdated.Member.MemberId == me
                 && (wantDown
                     ? m.MemberUpdated.Member.Hp <= 0
                     : m.MemberUpdated.Member.Hp < m.MemberUpdated.Member.HpMax),
            timeoutMs);
        return message?.MemberUpdated?.Member;
    }

    /// <summary>读一条红符回包：要么用掉了，要么被拒了。</summary>
    static async Task<ServerMessage?> ReadTalismanAsync(Session session, int timeoutMs) =>
        await session.ReadUntilAsync(
            m => m.TalismanUsed is not null || m.TalismanRejected is not null, timeoutMs);

    /// <summary>
    /// 掉血之后用一张红符：回血、写 heal 组冷却；紧接着再用一次撞共用冷却。
    /// 这是「只回复自己」的正面证据——加的血落在自己身上。
    /// </summary>
    static async Task<int> CaseP15c3TalismanAsync(string url, string item)
    {
        await using var session = await Session.ConnectAsync(url);
        var hurt = await WaitDamagedAsync(session, 10000, wantDown: false);
        if (hurt is null)
        {
            return Fail("case=p15c3_talisman 没等到 Boss 打中自己");
        }

        await session.UseTalismanAsync(item);
        var first = await ReadTalismanAsync(session, 8000);
        var used = first?.TalismanUsed;
        var firstOk = used is not null
                      && used.CasterId == session.Opened.CasterId
                      && used.ItemId == item
                      && used.Healed > 0
                      && used.HpAfter == hurt.Hp + used.Healed
                      && used.CooldownMs > 0
                      && used.CooldownGroup == "heal";

        // 紧接着再来一张：治疗组冷却还没好。红符和牧师治疗共用这一组。
        await session.UseTalismanAsync(item);
        var second = await ReadTalismanAsync(session, 8000);
        var reject = second?.TalismanRejected;
        var secondOk = reject?.Reason == "cooldown" && reject.RetryAfterMs > 0;

        return Report(firstOk && secondOk,
            $"case=p15c3_talisman hp_before={hurt.Hp} healed={used?.Healed} "
            + $"hp_after={used?.HpAfter} cooldown_ms={used?.CooldownMs} "
            + $"group={used?.CooldownGroup} left={used?.CountLeft} "
            + $"second_reason={reject?.Reason} retry_ms={reject?.RetryAfterMs}");
    }

    /// <summary>满血用符：挡在本地，不去扣符——不让玩家白烧一张。</summary>
    static async Task<int> CaseP15c3FullAsync(string url, string item)
    {
        await using var session = await Session.ConnectAsync(url);
        await session.UseTalismanAsync(item);
        var message = await ReadTalismanAsync(session, 8000);
        var reject = message?.TalismanRejected;

        return Report(reject?.Reason == "hp_full" && message?.TalismanUsed is null,
            $"case=p15c3_talisman_full hp={session.Opened.CasterHp}/{session.Opened.CasterHpMax} "
            + $"reason={reject?.Reason}");
    }

    /// <summary>拿别的物品编号来用：角色服只认红符，别的编号一律 not_talisman。</summary>
    static async Task<int> CaseP15c3OtherAsync(string url, string otherItem)
    {
        await using var session = await Session.ConnectAsync(url);
        var hurt = await WaitDamagedAsync(session, 10000, wantDown: false);
        if (hurt is null)
        {
            return Fail("case=p15c3_talisman_other 没等到 Boss 打中自己");
        }

        await session.UseTalismanAsync(otherItem);
        var message = await ReadTalismanAsync(session, 8000);
        var reject = message?.TalismanRejected;

        return Report(reject?.Reason == "not_talisman" && message?.TalismanUsed is null,
            $"case=p15c3_talisman_other item={otherItem} reason={reject?.Reason} "
            + $"hp={reject?.HpAfter}");
    }

    /// <summary>
    /// 倒地之后用符：拒绝。死亡只能靠复活卷轴或牧师的生息拉起，
    /// 红符在倒地时点不了——这就是「不能复活」。
    /// </summary>
    static async Task<int> CaseP15c3DownAsync(string url, string item)
    {
        await using var session = await Session.ConnectAsync(url);
        var down = await WaitDamagedAsync(session, 15000, wantDown: true);
        if (down is null)
        {
            return Fail("case=p15c3_talisman_down 没等到自己倒地");
        }

        await session.UseTalismanAsync(item);
        var message = await ReadTalismanAsync(session, 8000);
        var reject = message?.TalismanRejected;

        return Report(reject?.Reason == "caster_down"
                      && message?.TalismanUsed is null
                      && reject.HpAfter <= 0,
            $"case=p15c3_talisman_down hp={down.Hp} reason={reject?.Reason} "
            + $"hp_after={reject?.HpAfter}");
    }

    /// <summary>
    /// 这一场没有角色服（没配 upstream）：用符拒绝。宁可说这场用不了，
    /// 也不做「本地凭空变出一张符」。
    /// </summary>
    static async Task<int> CaseP15c3OffAsync(string url, string item)
    {
        await using var session = await Session.ConnectAsync(url);
        var hurt = await WaitDamagedAsync(session, 10000, wantDown: false);
        if (hurt is null)
        {
            return Fail("case=p15c3_talisman_off 没等到 Boss 打中自己");
        }

        await session.UseTalismanAsync(item);
        var message = await ReadTalismanAsync(session, 8000);
        var reject = message?.TalismanRejected;

        return Report(reject?.Reason == "upstream_off" && message?.TalismanUsed is null,
            $"case=p15c3_talisman_off reason={reject?.Reason} hp={reject?.HpAfter}");
    }

    /// <summary>
    /// 用符之后立刻放牧师的治疗：撞共用冷却。方案 12.1 写定「和红药、牧师治疗共用冷却」，
    /// 这一条不是靠注释保证的——两边读写的是同一个治疗组。
    /// </summary>
    static async Task<int> CaseP15c3HealShareAsync(string url, string item, uint healSkill)
    {
        await using var session = await Session.ConnectAsync(url);
        var hurt = await WaitDamagedAsync(session, 10000, wantDown: false);
        if (hurt is null)
        {
            return Fail("case=p15c3_talisman_heal_share 没等到 Boss 打中自己");
        }

        await session.UseTalismanAsync(item);
        var used = (await ReadTalismanAsync(session, 8000))?.TalismanUsed;
        if (used is null)
        {
            return Fail("case=p15c3_talisman_heal_share 红符没成功，共用冷却无从验起");
        }

        // 紧接着放治疗：红符刚占了治疗组，这一手应该被冷却挡下。
        await session.SubmitAsync(healSkill, session.Opened.CasterId, Session.NowMs());
        var message = await session.ReadUntilAsync(
            m => m.Result is not null || m.Rejected is not null, 6000);
        var reason = message?.Rejected?.Reason;

        return Report(reason == SkillRejectReason.SkillRejectCooldown,
            $"case=p15c3_talisman_heal_share heal_skill={healSkill} "
            + $"reason={reason?.ToString() ?? "none"} "
            + $"retry_ms={message?.Rejected?.RetryAfterMs} group={used.CooldownGroup}");
    }

    /// <summary>背包里一张都没有：角色服回 no_talisman，不回血。</summary>
    static async Task<int> CaseP15c3NoneAsync(string url, string item)
    {
        await using var session = await Session.ConnectAsync(url);
        var hurt = await WaitDamagedAsync(session, 10000, wantDown: false);
        if (hurt is null)
        {
            return Fail("case=p15c3_talisman_none 没等到 Boss 打中自己");
        }

        await session.UseTalismanAsync(item);
        var message = await ReadTalismanAsync(session, 8000);
        var reject = message?.TalismanRejected;

        return Report(reject?.Reason == "no_talisman" && message?.TalismanUsed is null,
            $"case=p15c3_talisman_none reason={reject?.Reason} hp_before={hurt.Hp} "
            + $"hp_after={reject?.HpAfter}");
    }

    /// <summary>
    /// 用一次耗蓝技能把蓝打下来，返回结算后的蓝量。蓝量在结算时才扣，
    /// 所以要等那一条 SkillResult，不能拿开场值当用符前的蓝。
    /// </summary>
    static async Task<long?> SpendManaAsync(Session session, uint skill)
    {
        await session.SubmitAsync(skill, session.Opened.BossId, Session.NowMs());
        var message = await session.ReadUntilAsync(
            m => m.Result is not null || m.Rejected is not null, 6000);
        return message?.Result?.ManaAfter;
    }

    /// <summary>
    /// 完成标准一「不回血」：蓝符只回自己的蓝，Healed 恒 0、血量一格不动。
    /// 同时验回包里的池、组、回蓝量与上限——回复值来自角色服回包，不是本地算的。
    /// </summary>
    static async Task<int> CaseP15c5BlueAsync(string url, string blue, uint skill)
    {
        await using var session = await Session.ConnectAsync(url);
        var opened = session.Opened;
        var manaBefore = await SpendManaAsync(session, skill);
        if (manaBefore is null || manaBefore >= opened.ManaMax)
        {
            return Fail("case=p15c5_blue 没能把蓝耗下来，蓝符会撞 mana_full");
        }

        await session.UseTalismanAsync(blue);
        var message = await ReadTalismanAsync(session, 8000);
        var used = message?.TalismanUsed;
        var ok = used is not null
                 && used.CasterId == opened.CasterId
                 && used.ItemId == blue
                 && used.Pool == "mana"
                 && used.CooldownGroup == "mana"
                 && used.CooldownMs > 0
                 && used.Healed == 0
                 && used.HpAfter <= opened.CasterHp
                 && used.ManaRestored > 0
                 && used.Restored == used.ManaRestored
                 && used.ManaAfter == manaBefore + used.ManaRestored
                 && used.ManaAfter <= opened.ManaMax;

        return Report(ok,
            $"case=p15c5_blue hp={opened.CasterHp} mana_before={manaBefore} "
            + $"restored={used?.ManaRestored} mana_after={used?.ManaAfter} "
            + $"healed={used?.Healed} hp_after={used?.HpAfter} "
            + $"pool={used?.Pool} group={used?.CooldownGroup} left={used?.CountLeft}");
    }

    /// <summary>满蓝用符：本地挡住，不去扣符——不让玩家白烧一张。</summary>
    static async Task<int> CaseP15c5FullAsync(string url, string blue)
    {
        await using var session = await Session.ConnectAsync(url);
        var opened = session.Opened;
        await session.UseTalismanAsync(blue);
        var message = await ReadTalismanAsync(session, 8000);
        var reject = message?.TalismanRejected;

        return Report(reject?.Reason == "mana_full" && message?.TalismanUsed is null
                      && reject.HpAfter == opened.CasterHp
                      && reject.ManaAfter == opened.Mana,
            $"case=p15c5_blue_full mana={opened.Mana}/{opened.ManaMax} "
            + $"reason={reject?.Reason} hp_after={reject?.HpAfter} mana_after={reject?.ManaAfter}");
    }

    /// <summary>紧接着再用一张：蓝量组冷却没好，还差多久由服务端给。</summary>
    static async Task<int> CaseP15c5TwiceAsync(string url, string blue, uint skill)
    {
        await using var session = await Session.ConnectAsync(url);
        var manaBefore = await SpendManaAsync(session, skill);
        if (manaBefore is null)
        {
            return Fail("case=p15c5_blue_twice 没能把蓝耗下来");
        }

        await session.UseTalismanAsync(blue);
        var used = (await ReadTalismanAsync(session, 8000))?.TalismanUsed;
        if (used is null)
        {
            return Fail("case=p15c5_blue_twice 第一张蓝符没成功："
                + (await ReadTalismanAsync(session, 500))?.TalismanRejected?.Reason);
        }

        await session.UseTalismanAsync(blue);
        var reject = (await ReadTalismanAsync(session, 8000))?.TalismanRejected;

        return Report(reject?.Reason == "cooldown" && reject.RetryAfterMs > 0
                      && reject.ManaAfter == used.ManaAfter,
            $"case=p15c5_blue_twice first_restored={used.ManaRestored} "
            + $"group={used.CooldownGroup} second_reason={reject?.Reason} "
            + $"retry_ms={reject?.RetryAfterMs} mana_after={reject?.ManaAfter}");
    }

    /// <summary>
    /// 完成标准「和红符可以各用一次」：红符用完立刻能用蓝符、蓝符用完立刻能用红符，
    /// 两次都成功且冷却组不同；随后各自再用第二次，分别撞自己那一组。
    /// </summary>
    static async Task<int> CaseP15c5BothAsync(string url, string red, string blue, uint skill)
    {
        await using var session = await Session.ConnectAsync(url);
        var manaBefore = await SpendManaAsync(session, skill);
        var hurt = await WaitDamagedAsync(session, 12000, wantDown: false);
        if (manaBefore is null || hurt is null || hurt.Hp >= hurt.HpMax)
        {
            return Fail("case=p15c5_red_blue_both 没造出「蓝不满 + 血不满」的起点");
        }

        await session.UseTalismanAsync(red);
        var redUsed = (await ReadTalismanAsync(session, 8000))?.TalismanUsed;
        await session.UseTalismanAsync(blue);
        var blueUsed = (await ReadTalismanAsync(session, 8000))?.TalismanUsed;
        await session.UseTalismanAsync(red);
        var redAgain = (await ReadTalismanAsync(session, 8000))?.TalismanRejected;
        await session.UseTalismanAsync(blue);
        var blueAgain = (await ReadTalismanAsync(session, 8000))?.TalismanRejected;

        var ok = redUsed is not null && blueUsed is not null
                 && redUsed.Pool == "hp" && redUsed.CooldownGroup == "heal"
                 && redUsed.Healed > 0 && redUsed.ManaRestored == 0
                 && blueUsed.Pool == "mana" && blueUsed.CooldownGroup == "mana"
                 && blueUsed.ManaRestored > 0 && blueUsed.Healed == 0
                 && blueUsed.HpAfter <= redUsed.HpAfter
                 && redAgain?.Reason == "cooldown"
                 && blueAgain?.Reason == "cooldown";

        return Report(ok,
            $"case=p15c5_red_blue_both red_healed={redUsed?.Healed} "
            + $"red_group={redUsed?.CooldownGroup} blue_restored={blueUsed?.ManaRestored} "
            + $"blue_group={blueUsed?.CooldownGroup} blue_hp_after={blueUsed?.HpAfter} "
            + $"red_2nd={redAgain?.Reason} blue_2nd={blueAgain?.Reason}");
    }

    /// <summary>
    /// 完成标准「不提高伤害」：用符前后同一个技能打同一个 Boss，伤害必须一样。
    /// 蓝量不进入伤害是 P0-3a 写定的，这里验的是蓝符没有偷偷开第二条路。
    /// </summary>
    static async Task<int> CaseP15c5DamageAsync(string url, string blue, uint skill)
    {
        await using var session = await Session.ConnectAsync(url);
        var opened = session.Opened;

        await session.SubmitAsync(skill, opened.BossId, Session.NowMs());
        // 前摇里 Boss 可能先推 MemberUpdated / BossPhase：ReadResultAsync 只看第一条，
        // 这里要跳过广播一直等到这一下的 Result（或 Rejected）。
        var firstMessage = await session.ReadUntilAsync(
            m => m.Result is not null || m.Rejected is not null, 6000);
        var first = firstMessage?.Result;
        if (first is null || first.Damage <= 0)
        {
            return Fail("case=p15c5_blue_damage 用符前那一下没打出来："
                + (firstMessage?.Rejected?.Reason.ToString() ?? "没等到回包"));
        }

        await session.UseTalismanAsync(blue);
        var used = (await ReadTalismanAsync(session, 8000))?.TalismanUsed;
        if (used is null || used.ManaRestored <= 0)
        {
            return Fail("case=p15c5_blue_damage 蓝符没回上蓝");
        }

        // 冷却从接受那次出手算起，等过整个冷却再打第二下。
        await Task.Delay((int)first.CooldownMs + 350);
        await session.SubmitAsync(skill, opened.BossId, Session.NowMs());
        var secondMessage = await session.ReadUntilAsync(
            m => m.Result is not null || m.Rejected is not null, 6000);
        var second = secondMessage?.Result;

        return Report(second is not null
                      && second.Damage == first.Damage
                      && used.Healed == 0
                      && used.ManaAfter <= opened.ManaMax,
            $"case=p15c5_blue_damage skill={skill} first={first.Damage} "
            + $"second={second?.Damage} restored={used.ManaRestored} "
            + $"mana_after={used.ManaAfter} mana_max={opened.ManaMax}");
    }

    /// <summary>拿保护券当符用：角色服不认这个编号，不扣不回。</summary>
    static async Task<int> CaseP15c5OtherAsync(string url, string other)
    {
        await using var session = await Session.ConnectAsync(url);
        var opened = session.Opened;
        await session.UseTalismanAsync(other);
        var message = await ReadTalismanAsync(session, 8000);
        var reject = message?.TalismanRejected;

        return Report(reject?.Reason == "not_talisman" && message?.TalismanUsed is null
                      && reject.HpAfter == opened.CasterHp
                      && reject.ManaAfter == opened.Mana,
            $"case=p15c5_blue_other item={other} reason={reject?.Reason} "
            + $"hp_after={reject?.HpAfter} mana_after={reject?.ManaAfter}");
    }

    /// <summary>倒地之后用蓝符：符不复活，倒地时点不了。</summary>
    static async Task<int> CaseP15c5DownAsync(string url, string blue)
    {
        await using var session = await Session.ConnectAsync(url);
        var down = await WaitDamagedAsync(session, 15000, wantDown: true);
        if (down is null)
        {
            return Fail("case=p15c5_blue_down 没等到自己倒地");
        }

        await session.UseTalismanAsync(blue);
        var message = await ReadTalismanAsync(session, 8000);
        var reject = message?.TalismanRejected;

        return Report(reject?.Reason == "caster_down"
                      && message?.TalismanUsed is null
                      && reject.HpAfter <= 0
                      && reject.ManaAfter == session.Opened.Mana,
            $"case=p15c5_blue_down hp={down.Hp} reason={reject?.Reason} "
            + $"hp_after={reject?.HpAfter} mana_after={reject?.ManaAfter}");
    }

    /// <summary>背包里一张蓝符都不剩：角色服回 no_talisman，血与蓝都不动。</summary>
    static async Task<int> CaseP15c5NoneAsync(string url, string blue, uint skill)
    {
        await using var session = await Session.ConnectAsync(url);
        var opened = session.Opened;
        var manaBefore = await SpendManaAsync(session, skill);
        if (manaBefore is null)
        {
            return Fail("case=p15c5_blue_none 没能把蓝耗下来");
        }

        await session.UseTalismanAsync(blue);
        var message = await ReadTalismanAsync(session, 8000);
        var reject = message?.TalismanRejected;

        return Report(reject?.Reason == "no_talisman" && message?.TalismanUsed is null
                      && reject.ManaAfter == manaBefore
                      && reject.HpAfter <= opened.CasterHp,
            $"case=p15c5_blue_none reason={reject?.Reason} "
            + $"mana_before={manaBefore} mana_after={reject?.ManaAfter} "
            + $"hp_after={reject?.HpAfter}");
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
