using System.Diagnostics;
using Mmo.Proto.Battle;

namespace Battle;

/// <summary>一次已经接受、还没结算的施法。</summary>
sealed class PendingCast
{
    public required SkillDef Skill { get; init; }

    /// <summary>客户端给的施放时刻，原样带回，客户端靠它把回包对上自己那一次。</summary>
    public required long CastTimeMs { get; init; }

    /// <summary>单调时钟上的结算时刻。前摇走完就在这里结算。</summary>
    public required long SettleAtMs { get; init; }

    /// <summary>接受这次施法之前的冷却到期时刻。取消要把冷却退回这里。</summary>
    public required long PrevReadyAtMs { get; init; }
}

/// <summary>
/// 一场单人 Boss。状态全在内存：进程一停这场就没了，重启不会恢复，
/// 也不会把没写下的奖励补出来。
///
/// 一次只结算一个技能：前摇没走完再发算 CASTING，结算完还在后摇里算 RECOVERY。
/// 冷却没好、距离不够、目标对不上，都在结算之前就退回去，不碰任何人的生命。
/// </summary>
public sealed class Encounter
{
    readonly object _gate = new();
    readonly BattleConfig _config;
    readonly SettlementStore _settlements;
    readonly Dictionary<uint, long> _readyAtMs = [];

    long _bossHp;
    long _damageTotal;
    int _castCount;
    long _recoveryUntilMs = long.MinValue;
    PendingCast? _pending;
    string _settlementId = "";
    bool _closed;

    // 施法者的实时坐标。客户端报上来的位置只是「想去哪」，夹过之后才写进这两个。
    // 距离判定读这里，不读出生点——否则走位就只是画面上的事，走近了照样打不中。
    double _casterX;
    double _casterY;
    long _lastMoveMs;

    public Encounter(BattleConfig config, SettlementStore settlements, ulong sessionId)
    {
        _config = config;
        _settlements = settlements;
        SessionId = sessionId;
        _bossHp = config.Boss.HpMax;
        _casterX = config.Caster.SpawnMm.X;
        _casterY = config.Caster.SpawnMm.Y;
        _lastMoveMs = NowMs();
    }

    public ulong SessionId { get; }

    /// <summary>单调时钟，毫秒。冷却和前摇都用它算，不受系统时间跳变影响。</summary>
    static long NowMs() => (long)Stopwatch.GetElapsedTime(0).TotalMilliseconds;

    public BattleOpened Open()
    {
        lock (_gate)
        {
            var opened = new BattleOpened
            {
                SessionId = SessionId,
                CasterId = _config.Caster.Id,
                BossId = _config.Boss.Id,
                BossHp = _bossHp,
                BossHpMax = _config.Boss.HpMax,
                CasterHp = _config.Caster.HpMax,
                CasterHpMax = _config.Caster.HpMax,
                DistanceMm = DistanceToBossMm(),
                ArenaRadiusMm = (uint)Math.Max(0, _config.Arena.RadiusMm),
                MoveSpeedMmPerS = (uint)Math.Max(0, _config.Arena.MoveSpeedMmPerS),
                CasterXMm = (int)Math.Round(_casterX),
                CasterYMm = (int)Math.Round(_casterY),
            };

            // 技能表随开场下发。客户端靠它播前摇、标名字，不自己存一份数字。
            foreach (var skill in _config.Skills.All)
            {
                opened.Skills.Add(new SkillInfo
                {
                    SkillId = skill.Id,
                    Name = skill.Name,
                    RangeMm = skill.RangeMm,
                    ForeswingMs = (uint)Math.Max(0, skill.ForeswingMs),
                    RecoveryMs = (uint)Math.Max(0, skill.RecoveryMs),
                    CooldownMs = (uint)Math.Max(0, skill.CooldownMs),
                    StaggerMs = (uint)Math.Max(0, skill.StaggerMs),
                });
            }

            return opened;
        }
    }

    /// <summary>接受就返回 null，拒绝就返回要回给客户端的这条。</summary>
    public SkillRejected? TrySubmit(SkillRequest request)
    {
        lock (_gate)
        {
            var now = NowMs();

            if (_closed || _bossHp <= 0)
            {
                return Reject(request, SkillRejectReason.SkillRejectNotInBattle);
            }

            if (!_config.Skills.TryGet(request.SkillId, out var skill))
            {
                return Reject(request, SkillRejectReason.SkillRejectUnknownSkill);
            }

            // 目标编号对不上本场的 Boss，就当目标已经不在了。
            // 客户端不能靠改一个编号去打别的东西。
            if (request.TargetId != _config.Boss.Id)
            {
                return Reject(request, SkillRejectReason.SkillRejectTargetGone);
            }

            // 一次只结算一个：前摇里再发一律先挡住，不看冷却。
            if (_pending is not null)
            {
                return Reject(request, SkillRejectReason.SkillRejectCasting);
            }

            // 后摇不走完不能接下一个，方案里写的「不取消后摇」。
            if (now < _recoveryUntilMs)
            {
                return Reject(request, SkillRejectReason.SkillRejectRecovery);
            }

            _readyAtMs.TryGetValue(skill.Id, out var readyAt);
            if (now < readyAt)
            {
                var rejected = Reject(request, SkillRejectReason.SkillRejectCooldown);
                rejected.RetryAfterMs = (uint)Math.Max(0, readyAt - now);
                return rejected;
            }

            var distance = DistanceToBossMm();
            if (distance > skill.RangeMm)
            {
                return Reject(request, SkillRejectReason.SkillRejectOutOfRange);
            }

            // 接受：先占住冷却，再等前摇。冷却从这一次出手算起，从结算算会让
            // 高延迟的人占便宜。取消的时候整段退回去。
            _readyAtMs[skill.Id] = now + skill.CooldownMs;
            _pending = new PendingCast
            {
                Skill = skill,
                CastTimeMs = request.CastTimeMs,
                SettleAtMs = now + skill.ForeswingMs,
                PrevReadyAtMs = readyAt,
            };
            return null;
        }
    }

    /// <summary>前摇里取消。撤掉了就没有伤害，冷却也退回出手前的样子。</summary>
    public SkillCancelled Cancel(SkillCancelRequest request)
    {
        lock (_gate)
        {
            var pending = _pending;
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

            _pending = null;
            _readyAtMs[pending.Skill.Id] = pending.PrevReadyAtMs;
            return new SkillCancelled
            {
                SkillId = request.SkillId,
                CastTimeMs = request.CastTimeMs,
                Accepted = true,
            };
        }
    }

    /// <summary>
    /// 走位。客户端报的是「想去哪」，这里按场地半径和速度上限夹过才落位。
    /// 夹完的位置回给客户端，它照这个摆自己；技能的距离判定用的也是这份坐标。
    /// </summary>
    public MoveState Move(MoveRequest request)
    {
        lock (_gate)
        {
            var bossX = (double)_config.Boss.SpawnMm.X;
            var bossY = (double)_config.Boss.SpawnMm.Y;
            var targetX = (double)request.XMm;
            var targetY = (double)request.YMm;
            var clamped = false;

            // 场地是一个以 Boss 为圆心的圆，走出去就沿半径拉回来。
            var radius = _config.Arena.RadiusMm;
            var outX = targetX - bossX;
            var outY = targetY - bossY;
            var outLen = Math.Sqrt(outX * outX + outY * outY);
            if (outLen > radius)
            {
                var scale = radius / outLen;
                targetX = bossX + outX * scale;
                targetY = bossY + outY * scale;
                clamped = true;
            }

            // 两次上报之间最多走 速度 × 时间。间隔按服务端时钟算，客户端改不了；
            // 单次封顶 200 毫秒，等于「一次上报最多走两步」——站着不动很久也不能
            // 攒出一次瞬移。客户端 100 毫秒报一次，留了一倍的量和抖动的余量。
            var now = NowMs();
            var dtMs = Math.Clamp(now - _lastMoveMs, 1, 200);
            _lastMoveMs = now;
            var maxStep = _config.Arena.MoveSpeedMmPerS * dtMs / 1000.0;

            var dx = targetX - _casterX;
            var dy = targetY - _casterY;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len > maxStep && len > 0)
            {
                var scale = maxStep / len;
                targetX = _casterX + dx * scale;
                targetY = _casterY + dy * scale;
                clamped = true;
            }

            _casterX = targetX;
            _casterY = targetY;

            return new MoveState
            {
                XMm = (int)Math.Round(_casterX),
                YMm = (int)Math.Round(_casterY),
                DistanceMm = DistanceToBossMm(),
                Clamped = clamped,
            };
        }
    }

    uint DistanceToBossMm()
    {
        var dx = _casterX - _config.Boss.SpawnMm.X;
        var dy = _casterY - _config.Boss.SpawnMm.Y;
        return (uint)Math.Round(Math.Sqrt(dx * dx + dy * dy));
    }

    /// <summary>推进时间。前摇到点就结算，这一下可能是收尾的一下。</summary>
    public List<ServerMessage> Tick()
    {
        lock (_gate)
        {
            var outbox = new List<ServerMessage>();
            if (_closed || _pending is null)
            {
                return outbox;
            }

            var now = NowMs();
            if (now < _pending.SettleAtMs)
            {
                return outbox;
            }

            var pending = _pending;
            _pending = null;

            if (_bossHp <= 0)
            {
                // 前摇里目标没了，这一下不算。
                outbox.Add(new ServerMessage
                {
                    Rejected = Reject(new SkillRequest
                    {
                        SkillId = pending.Skill.Id,
                        TargetId = _config.Boss.Id,
                        CastTimeMs = pending.CastTimeMs,
                    }, SkillRejectReason.SkillRejectTargetGone),
                });
                return outbox;
            }

            // 只剩这么多血就只算这么多，不把溢出伤害记上去。
            var applied = Math.Min(pending.Skill.Damage, _bossHp);
            _bossHp -= applied;
            _damageTotal += applied;
            _castCount++;
            _recoveryUntilMs = now + pending.Skill.RecoveryMs;

            outbox.Add(new ServerMessage
            {
                Result = new SkillResult
                {
                    CasterId = _config.Caster.Id,
                    SkillId = pending.Skill.Id,
                    TargetId = _config.Boss.Id,
                    Hit = true,
                    Damage = applied,
                    Healing = 0,
                    StaggerMs = (uint)Math.Max(0, pending.Skill.StaggerMs),
                    CooldownMs = (uint)Math.Max(0, pending.Skill.CooldownMs),
                    TargetHpAfter = _bossHp,
                },
            });

            if (_bossHp <= 0)
            {
                outbox.Add(new ServerMessage { Ended = Close() });
            }

            return outbox;
        }
    }

    /// <summary>断线。这场没打完就不结算，也不留记录，重进是另一场。</summary>
    public void Abandon()
    {
        lock (_gate)
        {
            _closed = true;
            _pending = null;
        }
    }

    /// <summary>只有通关才写记录。写失败就不给事务号，客户端拿不到号就领不了奖。</summary>
    BattleEnded Close()
    {
        _closed = true;
        try
        {
            var record = new SettlementRecord
            {
                SettlementId = $"bt{SessionId:x16}",
                SessionId = SessionId,
                CasterId = _config.Caster.Id,
                BossId = _config.Boss.Id,
                SkillTableVersion = _config.Skills.Version,
                ExpGain = _config.Boss.ExpOnClear,
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

        return new BattleEnded
        {
            SessionId = SessionId,
            Cleared = _bossHp <= 0,
            SettlementId = _settlementId,
            ExpGain = string.IsNullOrEmpty(_settlementId) ? 0 : _config.Boss.ExpOnClear,
        };
    }

    static SkillRejected Reject(SkillRequest request, SkillRejectReason reason) => new()
    {
        SkillId = request.SkillId,
        TargetId = request.TargetId,
        CastTimeMs = request.CastTimeMs,
        Reason = reason,
    };
}
