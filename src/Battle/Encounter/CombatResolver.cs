using System.Diagnostics;
using Mmo.Proto.Battle;
using Mmo.Proto.Character;
using TalismanPool = Mmo.Proto.Character.TalismanPool;

namespace Battle;

// 14.8 D3c：Encounter 按域拆出的分片，纯搬代码。
public sealed partial class Encounter
{
    // ---- 技能 ----

    /// <summary>接受就返回 null，拒绝就返回要回给客户端的这条。</summary>
    public SkillRejected? TrySubmit(Member member, SkillRequest request)
    {
        lock (_gate)
        {
            var now = NowMs();

            if (_closed || _bossHp <= 0)
            {
                return Reject(request, SkillRejectReason.SkillRejectNotInBattle);
            }

            if (member.Down)
            {
                return Reject(request, SkillRejectReason.SkillRejectCasterDead);
            }

            if (!_config.Skills.TryGet(request.SkillId, out var skill))
            {
                return Reject(request, SkillRejectReason.SkillRejectUnknownSkill);
            }

            // 别的职业的技能当未知技能：客户端改编号也借不到别人的手。
            if (skill.ClassId != member.Class.Id)
            {
                return Reject(request, SkillRejectReason.SkillRejectUnknownSkill);
            }

            // Q2：被控制时的拒绝。眩晕两样都挡，沉默只挡施法；冷却与资源一个都不动。
            if (member.Status.Stunned)
            {
                return Reject(request, SkillRejectReason.SkillRejectStunned);
            }
            if (member.Status.Silenced)
            {
                return Reject(request, SkillRejectReason.SkillRejectSilenced);
            }

            // 一次只结算一个：前摇里再发一律先挡住，不看冷却。
            if (member.Pending is not null)
            {
                return Reject(request, SkillRejectReason.SkillRejectCasting);
            }

            // 后摇不走完不能接下一个，方案里写的「不取消后摇」。
            if (now < member.RecoveryUntilMs)
            {
                return Reject(request, SkillRejectReason.SkillRejectRecovery);
            }

            member.ReadyAtMs.TryGetValue(skill.Id, out var readyAt);
            // 治疗类技能和红符共用一组冷却（方案 12.1）：谁先用了，另一边都要等。
            // 蓝符是另一组，不并进来，所以这里只看 heal。
            var healReadyAt = member.HealGroupReadyAtMs;
            if (skill.Kind == SkillKind.Heal)
            {
                readyAt = Math.Max(readyAt, healReadyAt);
            }

            if (now < readyAt)
            {
                var rejected = Reject(request, SkillRejectReason.SkillRejectCooldown);
                rejected.RetryAfterMs = (uint)Math.Max(0, readyAt - now);
                return rejected;
            }

            // 目标按技能种类分：打 Boss 的看 Boss 编号，治疗和复活看队友编号。
            Member? target = null;
            // P2-4a：诫律没有复活这条能力。在目标校验之前就拒——不是「目标选错了」，
            // 是这个分支不该发出这次请求，所以没人倒地时也一样拒。
            if (skill.Kind == SkillKind.Resurrect)
            {
                var casterProfile = ProfileOf(member);
                if (casterProfile is not null
                    && string.Equals(casterProfile.Branch, CannotResurrectBranch, StringComparison.Ordinal))
                {
                    return Reject(request, SkillRejectReason.SkillRejectCannotResurrect);
                }
            }

            if (skill.Kind is SkillKind.Heal or SkillKind.Resurrect)
            {
                if (!_members.TryGetValue(request.TargetId, out target))
                {
                    return Reject(request, SkillRejectReason.SkillRejectTargetGone);
                }
                if (skill.Kind == SkillKind.Heal && target.Down)
                {
                    // 倒地只能靠复活，治疗救不起来。
                    return Reject(request, SkillRejectReason.SkillRejectTargetGone);
                }
                if (skill.Kind == SkillKind.Resurrect && (target.Id == member.Id || !target.Down))
                {
                    // 不能拉自己；没倒地的也不用拉。
                    return Reject(request, SkillRejectReason.SkillRejectBadTarget);
                }
            }
            else if (request.TargetId != _boss.Id)
            {
                // 目标编号对不上本场的 Boss，就当目标已经不在了。
                return Reject(request, SkillRejectReason.SkillRejectTargetGone);
            }

            var distance = target is null ? DistanceToBossMm(member) : DistanceBetweenMm(member, target);
            if (distance > skill.RangeMm)
            {
                return Reject(request, SkillRejectReason.SkillRejectOutOfRange);
            }

            // 主动技能耗蓝，蓝不够就放不出去。放在目标检查之后：目标不对就说目标不对，
            // 别拿蓝不够挡在前面。蓝在结算时才扣，撤掉的那次不花钱。
            if (member.Mana < skill.ManaCost)
            {
                return Reject(request, SkillRejectReason.SkillRejectNoMana);
            }

            // 接受：先占住冷却，再等前摇。冷却从这一次出手算起，从结算算会让
            // 高延迟的人占便宜。取消的时候整段退回去。
            member.ReadyAtMs[skill.Id] = now + skill.CooldownMs;
            if (skill.Kind == SkillKind.Heal)
            {
                // 治疗也占治疗组：红符这一段时间同样用不了。
                member.HealGroupReadyAtMs = now + skill.CooldownMs;
            }

            member.Pending = new PendingCast
            {
                Skill = skill,
                CastTimeMs = request.CastTimeMs,
                SettleAtMs = now + skill.ForeswingMs,
                PrevReadyAtMs = readyAt,
                PrevHealGroupReadyAtMs = healReadyAt,
                TargetId = request.TargetId,
            };
            return null;
        }
    }

    /// <summary>前摇里取消。撤掉了就没有伤害，冷却也退回出手前的样子。</summary>
    public SkillCancelled Cancel(Member member, SkillCancelRequest request)
    {
        lock (_gate)
        {
            var pending = member.Pending;
            if (pending is null ||
                pending.Skill.Id != request.SkillId ||
                pending.CastTimeMs != request.CastTimeMs)
            {
                // 已经结算完了，或者对不上是哪一次，撤不回来。
                return new SkillCancelled
                {
                    SkillId = request.SkillId,
                    CastTimeMs = request.CastTimeMs,
                    Accepted = false,
                };
            }

            member.Pending = null;
            member.ReadyAtMs[pending.Skill.Id] = pending.PrevReadyAtMs;
            if (pending.Skill.Kind == SkillKind.Heal)
            {
                // 撤掉的治疗不占组冷却：这一手没结算出去。
                member.HealGroupReadyAtMs = pending.PrevHealGroupReadyAtMs;
            }
            return new SkillCancelled
            {
                SkillId = request.SkillId,
                CastTimeMs = request.CastTimeMs,
                Accepted = true,
            };
        }
    }

    // ---- 卷轴用毕 ----

    // ---- 时间推进 ----

    /// <summary>
    /// 推进时间：各人前摇到点结算，Boss 按阶段出手，血线过阈就换阶段。
    /// 返回的消息要发给这场里的所有人。
    /// </summary>
    public List<ServerMessage> Tick()
    {
        lock (_gate)
        {
            var outbox = new List<ServerMessage>();
            if (_closed)
            {
                return outbox;
            }

            var now = NowMs();

            foreach (var member in _members.Values)
            {
                var pending = member.Pending;
                if (pending is null || now < pending.SettleAtMs)
                {
                    continue;
                }

                member.Pending = null;
                Settle(member, pending, now, outbox);

                if (_bossHp <= 0)
                {
                    return outbox;
                }
            }

            TickBossAttack(now, outbox);
            TickEffects(now, outbox);
            return outbox;
        }
    }

    /// <summary>
    /// 推进所有效果的时钟：DoT / HoT 按服务端时钟跳，到期就摘，护盾破了就清。
    /// 血量变化对人是 MemberUpdated、对 Boss 是 BossState；效果集合变了整份重发。
    /// DoT 把 Boss 打死时，结算与 Close 与技能打死走同一条路。
    /// </summary>
    void TickEffects(long now, List<ServerMessage> outbox)
    {
        if (_config.Effects.IsMissing)
        {
            return;
        }

        foreach (var member in _members.Values.ToList())
        {
            var tick = member.Status.Tick(now);
            var changed = tick.EffectsChanged;

            // 死了就清空，护盾不跨死；已经倒地的不会反复清。
            if (member.Down && member.Status.Active.Count > 0)
            {
                member.Status.Clear();
                changed = true;
                member.Pending = null;
            }

            if (changed)
            {
                outbox.Add(new ServerMessage { Effects = member.Status.State(now) });
            }
            if (tick.HpDelta != 0)
            {
                outbox.Add(new ServerMessage { MemberUpdated = new() { Member = ToPartyMember(member) } });
            }
        }

        var bossTick = _bossUnit.Status.Tick(now);
        if (bossTick.HpDelta < 0)
        {
            _damageTotal += -bossTick.HpDelta;
        }
        if (bossTick.EffectsChanged || bossTick.HpDelta != 0)
        {
            outbox.Add(new ServerMessage { BossState = BossStateOf(now) });
        }

        if (_bossHp <= 0 && !_closed)
        {
            _bossUnit.Status.Clear();
            outbox.Add(new ServerMessage { BossState = BossStateOf(now) });
            if (_dungeon is not null)
            {
                _qualifiedKills++;
            }
            outbox.Add(new ServerMessage { Ended = Close(bossTick.LastSourceId) });
            return;
        }

        if (bossTick.HpDelta != 0)
        {
            CheckPhase(outbox);
        }
    }

    /// <summary>
    /// 技能结算时施加效果。技能表里没有 effect 块就什么都不做；有就一条条走
    /// <see cref="StatusSet.Apply"/>——丹药 / 道具以后也走同一个入口，不另起一套。
    /// target=self 恒指施法者；target=target 是这一手本来选中的单位（Boss 或队友）。
    /// </summary>
    void ApplySkillEffects(Member caster, SkillDef skill, Member? memberTarget, long now, List<ServerMessage> outbox)
    {
        if (_config.Effects.IsMissing || skill.Effects.Count == 0)
        {
            return;
        }

        foreach (var spec in skill.Effects)
        {
            if (spec.Target == "self")
            {
                ApplyToMember(caster, spec, caster.Id, now, outbox);
            }
            else if (memberTarget is not null)
            {
                ApplyToMember(memberTarget, spec, caster.Id, now, outbox);
            }
            else if (_bossHp > 0)
            {
                var result = _bossUnit.Status.Apply(spec.EffectId, caster.Id, now, spec.Stacks);
                if (result is EffectApply.Applied or EffectApply.Refreshed or EffectApply.Stacked)
                {
                    Console.WriteLine($"effect_apply=unit={_boss.Id} effect={spec.EffectId}"
                        + $" result={result} source={caster.Id}");
                    outbox.Add(new ServerMessage { BossState = BossStateOf(now) });
                }
            }
        }
    }

    void ApplyToMember(Member target, EffectSpec spec, ulong sourceId, long now, List<ServerMessage> outbox)
    {
        var result = target.Status.Apply(spec.EffectId, sourceId, now, spec.Stacks);
        if (result is EffectApply.Applied or EffectApply.Refreshed or EffectApply.Stacked)
        {
            Console.WriteLine($"effect_apply=unit={target.Id} effect={spec.EffectId}"
                + $" result={result} source={sourceId}");
            outbox.Add(new ServerMessage { Effects = target.Status.State(now) });
        }
    }

    /// <summary>
    /// Q2：四段伤害。属性仍来自战斗快照（职业表 / 竞技场模板 / 战场压缩后 / 城战全额），
    /// 这里只加效果层：攻击百分比来自自己身上的 buff，防御百分比来自目标身上的 debuff，
    /// 境界加成来自角色服下发的被动百分比。效果表缺失时退回恒等，老链路逐字不变。
    /// B10：第二段的强化系数与结晶固定值只在「按职业表算属性」的那条路上给——
    /// 竞技场按模板统一属性、战场（含城战全额）已经把装备折进 Combat 并压缩到 1.3 倍以内，
    /// 这里再加一次就是双算；两条的老数一个都不动。
    /// </summary>
    long DamageOf(Member member, SkillDef skill)
    {
        var profile = ProfileOf(member);
        var wearsEquipment = member.Template is null && member.Combat is null;
        var input = new DamageInput(
            Attack: member.AttackPower(),
            SkillMultiplier: skill.DamageMultiplier,
            TargetDefense: _boss.Defense,
            RealmBonusPct: profile?.PassivePctBps ?? 0,
            AttackPct: member.Status.AttackPct,
            DefensePct: _bossUnit.Status.DefensePct,
            EnhanceCoef: wearsEquipment ? (profile?.EnhanceCoefBps ?? 0) / 10000.0 : 0,
            CrystalFlatAttack: wearsEquipment ? profile?.CrystalFlatAttack ?? 0 : 0);
        return DamagePipeline.Compute(_config.Effects, input, _rng).Rounded;
    }

    // ---- 换算 ----

    static SkillRequest PendingRequest(PendingCast pending) => new()
    {
        SkillId = pending.Skill.Id,
        TargetId = pending.TargetId,
        CastTimeMs = pending.CastTimeMs,
    };

    static SkillRejected Reject(SkillRequest request, SkillRejectReason reason) => new()
    {
        SkillId = request.SkillId,
        TargetId = request.TargetId,
        CastTimeMs = request.CastTimeMs,
        Reason = reason,
    };
}
