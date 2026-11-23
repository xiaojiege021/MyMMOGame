using System.Diagnostics;
using Mmo.Proto.Battle;
using Mmo.Proto.Character;
using TalismanPool = Mmo.Proto.Character.TalismanPool;

namespace Battle;

// 14.8 D3c：Encounter 按域拆出的分片，纯搬代码。
public sealed partial class Encounter
{
    BossState BossStateOf(long now) => new()
    {
        BossId = _boss.Id,
        Hp = _bossHp,
        HpMax = _bossHpMax,
        Effects = { _bossUnit.Status.Snapshots(now) },
    };

    // ---- Boss 的出手与阶段 ----

    void TickBossAttack(long now, List<ServerMessage> outbox)
    {
        if (_bossHp <= 0)
        {
            return;
        }

        var attack = Phase(_phaseIndex).Attack;
        if (attack is null)
        {
            return;
        }

        if (_bossPending is null && now >= _nextBossAttackAt)
        {
            // 打仇恨最高的那个活着的人。坦克的稳定值高，Boss 就咬着坦克打；
            // 稳定值低的时候谁输出凶 Boss 就掉头去打谁。
            var target = _members.Values.Where(m => !m.Down)
                .OrderByDescending(m => m.EffectiveThreat)
                .ThenBy(m => m.Id)
                .FirstOrDefault();

            if (target is null)
            {
                _nextBossAttackAt = now + attack.IntervalMs;
                return;
            }

            _bossPending = (target.Id, now + attack.ForeswingMs);
        }

        if (_bossPending is { } pending && now >= pending.LandAtMs)
        {
            _bossPending = null;
            _nextBossAttackAt = now + attack.IntervalMs;

            if (_members.TryGetValue(pending.MemberId, out var victim) && !victim.Down)
            {
                var damage = ScaleDamage(attack.Damage, _monsterDamageScale);
                // Q2：护盾先吃伤害；被打断只在前摇里发生，蓝在结算时才扣，所以没有已花掉的蓝要退。
                var afterShield = victim.Status.Absorb(damage);
                if (afterShield != damage)
                {
                    outbox.Add(new ServerMessage { Effects = victim.Status.State(now) });
                }

                var after = victim.Hp - afterShield;
                victim.Hp = after > 0 ? after : 0;

                if (!_config.Effects.IsMissing
                    && damage > 0
                    && victim.Pending is { } interrupted)
                {
                    victim.Pending = null;
                    victim.ReadyAtMs[interrupted.Skill.Id] =
                        now + _config.Effects.Interaction.InterruptCooldownMs;
                    outbox.Add(new ServerMessage
                    {
                        Cancelled = new SkillCancelled
                        {
                            SkillId = interrupted.Skill.Id,
                            CastTimeMs = interrupted.CastTimeMs,
                            Accepted = true,
                        },
                    });
                    Console.WriteLine($"effect_interrupt=unit={victim.Id} skill={interrupted.Skill.Id}"
                        + $" cooldown_ms={_config.Effects.Interaction.InterruptCooldownMs}");
                }

                if (victim.Down && victim.Status.Active.Count > 0)
                {
                    victim.Status.Clear();
                    outbox.Add(new ServerMessage { Effects = victim.Status.State(now) });
                }

                outbox.Add(new ServerMessage { MemberUpdated = new() { Member = ToPartyMember(victim) } });
            }
        }
    }

    /// <summary>血线过阈就换阶段。阶段表来自配置，切了以后 Boss 按新阶段的参数出手。</summary>
    void CheckPhase(List<ServerMessage> outbox)
    {
        var index = PhaseIndexFor(_bossHp);
        if (index == _phaseIndex)
        {
            return;
        }

        _phaseIndex = index;
        _bossPending = null;
        _nextBossAttackAt = NowMs();
        outbox.Add(new ServerMessage
        {
            BossPhase = new() { PhaseIndex = index, Name = Phase(index).Name },
        });
    }

    List<PhaseConfig> Phases() =>
        _boss.Phases is { Count: > 0 } phases
            ? phases
            :
            [
                new PhaseConfig
                {
                    Name = _boss.Name,
                    HpPctFrom = 100,
                    HpPctTo = 0,
                    Attack = _boss.Attack,
                },
            ];

    PhaseConfig Phase(int index) => Phases()[Math.Clamp(index, 0, Phases().Count - 1)];

    int PhaseIndexFor(long hp)
    {
        var phases = Phases();
        if (phases.Count == 1)
        {
            return 0;
        }

        var pct = hp * 100.0 / Math.Max(1, _bossHpMax);
        for (var i = 0; i < phases.Count; i++)
        {
            // 每段盖 (to, from]：血量到 from 的那一刻还在上一段，掉过 to 才算下一段。
            if (pct <= phases[i].HpPctFrom && pct > phases[i].HpPctTo)
            {
                return i;
            }
        }

        return phases.Count - 1;
    }
}
