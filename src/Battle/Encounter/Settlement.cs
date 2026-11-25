using System.Diagnostics;
using Mmo.Proto.Battle;
using Mmo.Proto.Character;
using TalismanPool = Mmo.Proto.Character.TalismanPool;

namespace Battle;

// 14.8 D3c：Encounter 按域拆出的分片，纯搬代码。
public sealed partial class Encounter
{
    void Settle(Member member, PendingCast pending, long now, List<ServerMessage> outbox)
    {
        var skill = pending.Skill;

        if (member.Down)
        {
            // 前摇里人倒了，这一下不结算，当没发过。
            outbox.Add(new ServerMessage
            {
                Rejected = Reject(PendingRequest(pending), SkillRejectReason.SkillRejectCasterDead),
            });
            return;
        }

        if (skill.Kind is SkillKind.Basic or SkillKind.Active)
        {
            if (_bossHp <= 0)
            {
                // 前摇里目标没了，这一下不算。
                outbox.Add(new ServerMessage
                {
                    Rejected = Reject(new SkillRequest
                    {
                        SkillId = skill.Id,
                        TargetId = _boss.Id,
                        CastTimeMs = pending.CastTimeMs,
                    }, SkillRejectReason.SkillRejectTargetGone),
                });
                return;
            }

            // 只剩这么多血就只算这么多，不把溢出伤害记上去。
            // Q2：先让 Boss 身上的护盾吃一截，剩下的才落到血上；破盾也要重发效果快照。
            var raw = DamageOf(member, skill);
            var afterShield = _bossUnit.Status.Absorb(raw);
            if (afterShield != raw)
            {
                outbox.Add(new ServerMessage { BossState = BossStateOf(now) });
            }
            var applied = Math.Min(afterShield, _bossHp);
            _bossHp -= applied;
            member.DamageDealt += applied;
            _damageTotal += applied;
            _castCount++;
            member.Mana -= skill.ManaCost;
            member.RecoveryUntilMs = now + skill.RecoveryMs;

            outbox.Add(new ServerMessage
            {
                Result = new SkillResult
                {
                    CasterId = member.Id,
                    SkillId = skill.Id,
                    TargetId = _boss.Id,
                    Hit = true,
                    Damage = applied,
                    Healing = 0,
                    StaggerMs = (uint)Math.Max(0, skill.StaggerMs),
                    CooldownMs = (uint)Math.Max(0, skill.CooldownMs),
                    TargetHpAfter = _bossHp,
                    ManaAfter = member.Mana,
                    ThreatAfter = member.EffectiveThreat,
                },
            });

            ApplySkillEffects(member, skill, null, now, outbox);

            CheckPhase(outbox);

            if (_bossHp <= 0)
            {
                // 只有已经进本、且角色服在进本那一步判过资格的副本才计击杀。
                // 表世界/竞技场没有副本编号，这一场一笔历练都不给。
                if (_dungeon is not null)
                {
                    _qualifiedKills++;
                }

                outbox.Add(new ServerMessage { Ended = Close(member.Id) });
            }

            return;
        }

        if (skill.Kind == SkillKind.Heal)
        {
            if (!_members.TryGetValue(pending.TargetId, out var target) || target.Down)
            {
                outbox.Add(new ServerMessage
                {
                    Rejected = Reject(PendingRequest(pending), SkillRejectReason.SkillRejectTargetGone),
                });
                return;
            }

            // 治疗读精神：精神 × 职业治疗系数 × 技能倍率；竞技场里读模板的治疗值。
            var healing = (long)Math.Round(member.HealPower() * skill.DamageMultiplier);
            target.Hp = Math.Min(target.HpMax, target.Hp + healing);
            member.Mana -= skill.ManaCost;
            _castCount++;
            member.RecoveryUntilMs = now + skill.RecoveryMs;

            outbox.Add(new ServerMessage
            {
                Result = new SkillResult
                {
                    CasterId = member.Id,
                    SkillId = skill.Id,
                    TargetId = target.Id,
                    Hit = true,
                    Damage = 0,
                    Healing = healing,
                    StaggerMs = 0,
                    CooldownMs = (uint)Math.Max(0, skill.CooldownMs),
                    TargetHpAfter = target.Hp,
                    ManaAfter = member.Mana,
                    ThreatAfter = member.EffectiveThreat,
                },
            });
            outbox.Add(new ServerMessage { MemberUpdated = new() { Member = ToPartyMember(target) } });
            ApplySkillEffects(member, skill, target, now, outbox);
            return;
        }

        // 复活：拉起倒地的队友，不能拉自己（提交时已挡过，结算时再核一遍）。
        if (!_members.TryGetValue(pending.TargetId, out var raised) || !raised.Down || raised.Id == member.Id)
        {
            outbox.Add(new ServerMessage
            {
                Rejected = Reject(PendingRequest(pending), SkillRejectReason.SkillRejectBadTarget),
            });
            return;
        }

        // P2-4a：诫律没有复活这条能力。这不是目标选错，是这个分支本来就不该有这次请求。
        var casterProfile = ProfileOf(member);
        if (casterProfile is not null
            && string.Equals(casterProfile.Branch, CannotResurrectBranch, StringComparison.Ordinal))
        {
            outbox.Add(new ServerMessage
            {
                Rejected = Reject(PendingRequest(pending), SkillRejectReason.SkillRejectCannotResurrect),
            });
            return;
        }

        var restore = Math.Max(1L, raised.HpMax * _config.Classes.Resurrect.HpRestorePct / 100);
        // 生息二转只把这个比例减小；卷轴走角色服那一笔，仍按基础比例，不在这里。
        var costPct = _config.Classes.Resurrect.ExpCostPct;
        if (casterProfile is not null && casterProfile.BranchStep >= 2
            && string.Equals(casterProfile.Branch, ShengxiBranch, StringComparison.Ordinal))
        {
            costPct = _config.Classes.Resurrect.SecondTurnExpCostPct;
        }

        var expCost = ExpCostOf(raised.Id, costPct);
        if (expCost is null)
        {
            // 角色那张表写不下就不拉，和结算不给号同一个道理。
            outbox.Add(new ServerMessage
            {
                Rejected = Reject(PendingRequest(pending), SkillRejectReason.SkillRejectUnspecified),
            });
            return;
        }

        raised.Hp = restore;
        member.Mana -= skill.ManaCost;
        _castCount++;
        member.RecoveryUntilMs = now + skill.RecoveryMs;

        outbox.Add(new ServerMessage
        {
            Result = new SkillResult
            {
                CasterId = member.Id,
                SkillId = skill.Id,
                TargetId = raised.Id,
                Hit = true,
                Damage = 0,
                Healing = restore,
                StaggerMs = 0,
                CooldownMs = (uint)Math.Max(0, skill.CooldownMs),
                TargetHpAfter = raised.Hp,
                ManaAfter = member.Mana,
                ThreatAfter = member.EffectiveThreat,
            },
        });
        outbox.Add(new ServerMessage { MemberUpdated = new() { Member = ToPartyMember(raised) } });
        ApplySkillEffects(member, skill, raised, now, outbox);
    }

    /// <summary>
    /// 被拉起来的人只扣当前等级已有经验的一部分，不掉级；
    /// 当前级经验是零时这次不扣。等级不动，只动经验。
    /// 写不下去返回 null，由调用方把这次复活退回去。
    /// </summary>
    long? ExpCostOf(ulong characterId, int costPct)
    {
        try
        {
            var state = _characters.Get(characterId);
            var cost = state.Exp * costPct / 100;
            _characters.Save(characterId, state with { Exp = Math.Max(0, state.Exp - cost) });
            return cost;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"character_error=character={characterId} {ex.Message}");
            return null;
        }
    }

    /// <summary>只有通关才写记录。写失败就不给事务号，客户端拿不到号就领不了奖。</summary>
    BattleEnded Close(ulong killerId)
    {
        _closed = true;

        // Q2：一场结束，所有人 / Boss 身上的效果全部清空；护盾不跨场，
        // DoT 不会在结算之后再跳一次。
        foreach (var member in _members.Values)
        {
            member.Status.Clear();
        }
        _bossUnit.Status.Clear();

        try
        {
            var record = new SettlementRecord
            {
                SettlementId = $"bt{SessionId:x16}",
                SessionId = SessionId,
                CasterId = killerId,
                BossId = _boss.Id,
                SkillTableVersion = _config.Skills.Version,
                ExpGain = _boss.ExpOnClear,
                DamageTotal = _damageTotal,
                CastCount = _castCount,
                ClosedAt = DateTimeOffset.UtcNow,
            };
            _settlementId = _settlements.Write(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不下就不发号，宁可这次没有奖励，也不发一个查不到的号。
            Console.WriteLine($"settlement_error=session={SessionId} {ex.Message}");
            _settlementId = "";
        }

        // 同一场每人一份个人经验，交给角色服的那一笔结算。事务号用本地结算号，
        // 角色服按号幂等；没配上游时空实现什么都不做，P0 行为不变。
        // 伤害、掉落、疲劳、次数都不在这里改，组队加成只加经验。
        // P3-5a：人数用「本场实际人数」时（世界 Boss 会设），组队与同门那一截按它算；
        // 没设过就是进本时说好的人数，副本 / 战场 / 城战一个字都没变。
        if (_settlementId.Length > 0)
        {
            var settledSize = _settlementPartySize > 0
                ? Math.Max(_settlementPartySize, _partySize)
                : _partySize;
            var dungeonId = _dungeon?.Id ?? "";
            // N8：世界 Boss 不是 dungeons.json 副本，掉落表键固定 world_boss。
            if (dungeonId.Length == 0
                && string.Equals(SettlementSource, "world_boss", StringComparison.Ordinal))
            {
                dungeonId = "world_boss";
            }

            var drops = dungeonId.Length > 0 && _bossHp <= 0
                ? _config.DungeonDrops.LinesFor(dungeonId)
                : (IReadOnlyList<DropLineDef>)[];
            var memberIds = _members.Keys.ToArray();

            // Q8/H2：先在服务端把这一份掉落分到人头上，再逐人入包。
            // personal 时每人是整份（N6 的口径一字不变）；need_greed 时每行只有一个赢家，
            // 掷点只在这里做一次——同一件不会被两个人各拿一份。
            var scope = _expSettlement.ResolveLootScope(memberIds);
            var allocation = LootAllocator.Allocate(scope, memberIds, drops, SnapshotLootIntents());
            LootResult = string.Equals(allocation.Mode, PartyLoot.NeedGreed, StringComparison.Ordinal)
                ? allocation
                : null;

            Console.WriteLine("loot_alloc mode=" + allocation.Mode
                + " members=" + memberIds.Length
                + " lines=" + drops.Count
                + " announced=" + allocation.Lines.Count);

            _expSettlement.SettleParty(
                _settlementId, SettlementSource, settledSize, memberIds,
                dungeonId, _qualifiedKills, allocation.ByMember);

            // B11：竞技场模式下这一场打掉 Boss 就是一场胜仗，逐人把段位分交给角色服。
            // 散场（没打掉）不进这里，不记分也不扣分；分数怎么加、档位怎么升全在
            // 角色服的 config/arena_rank.json，战斗服不读段位表。
            if (ArenaModeId.Length > 0 && _bossHp <= 0)
            {
                _arenaRank.ReportWin(_settlementId, ArenaModeId, memberIds);
            }
        }

        EndSnapshot = new BattleEnded
        {
            SessionId = SessionId,
            Cleared = _bossHp <= 0,
            SettlementId = _settlementId,
            ExpGain = string.IsNullOrEmpty(_settlementId) ? 0 : _boss.ExpOnClear,
        };
        return EndSnapshot;
    }
}
