using System.Diagnostics;
using Mmo.Proto.Battle;
using Mmo.Proto.Character;
using TalismanPool = Mmo.Proto.Character.TalismanPool;

namespace Battle;

// 14.8 D3c：Encounter 按域拆出的分片，纯搬代码。
public sealed partial class Encounter
{
    // ---- 成员 ----

    /// <summary>
    /// 一个人进场。编号撞了抛异常，由连接层拒绝这条连接。
    /// arena 不为空时，生命、蓝、攻击、治疗与仇恨稳定值全部取竞技场模板，
    /// 不看职业表的属性，也不看这个人的装备与强化等级。
    /// </summary>
    public Member Join(ulong memberId, ClassDef cls, ArenaTemplate? arena = null, CombatStats? combat = null)
    {
        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException("这场已经结束");
            }
            if (_members.ContainsKey(memberId))
            {
                throw new InvalidOperationException($"编号 {memberId} 已经在这场里");
            }

            var member = new Member
            {
                Id = memberId,
                Class = cls,
                Template = arena,
                Combat = combat,
                HpMax = combat?.HpMax ?? arena?.HpMax ?? cls.HpMax(_config.Classes.HpPerVitality),
                ManaMax = combat?.ManaMax ?? arena?.ManaMax ?? cls.ManaMax(),
                X = _config.Caster.SpawnMm.X,
                Y = _config.Caster.SpawnMm.Y,
            };
            member.Hp = member.HpMax;
            member.Mana = member.ManaMax;
            member.Status = new StatusSet(_config.Effects, member);
            member.TouchMoveClock(NowMs());
            _members[memberId] = member;
            return member;
        }
    }

    public void Leave(Member member)
    {
        lock (_gate)
        {
            _members.Remove(member.Id);
            member.Pending = null;
        }
    }

    public BattleOpened Open(Member member)
    {
        lock (_gate)
        {
            var opened = new BattleOpened
            {
                SessionId = SessionId,
                CasterId = member.Id,
                BossId = _boss.Id,
                BossHp = _bossHp,
                BossHpMax = _bossHpMax,
                CasterHp = member.Hp,
                CasterHpMax = member.HpMax,
                DistanceMm = DistanceToBossMm(member),
                ArenaRadiusMm = (uint)Math.Max(0, _config.Arena.RadiusMm),
                MoveSpeedMmPerS = (uint)Math.Max(0, _config.Arena.MoveSpeedMmPerS),
                CasterXMm = (int)Math.Round(member.X),
                CasterYMm = (int)Math.Round(member.Y),
                ClassId = member.Class.Id,
                ClassName = member.Class.Name,
                Mana = member.Mana,
                ManaMax = member.ManaMax,
            };

            // 技能表随开场下发，只发这个人的职业。客户端靠它播前摇、标名字，
            // 不自己存一份数字；伤害不下发，只认回包里的那一个数。
            foreach (var skill in _config.Skills.ForClass(member.Class.Id))
            {
                opened.Skills.Add(ToSkillInfo(skill));
            }

            foreach (var m in _members.Values.OrderBy(m => m.Id))
            {
                opened.Party.Add(ToPartyMember(m));
            }

            // 职业目录也下发，换职业的按钮按这个循环。
            foreach (var cls in _config.Classes.All)
            {
                opened.Classes.Add(new ClassInfo { ClassId = cls.Id, Name = cls.Name });
            }

            // Q2：开场时身上还没效果，这一格是空的；Q9 的断线重连复用同一格，
            // 按服务端时钟剩下的毫秒恢复，不从客户端补算。
            var now = NowMs();
            foreach (var m in _members.Values.OrderBy(m => m.Id))
            {
                opened.Effects.Add(m.Status.State(now));
            }
            opened.Effects.Add(_bossUnit.Status.State(now));

            return opened;
        }
    }

    /// <summary>
    /// 走位。客户端报的是「想去哪」，这里按场地半径和速度上限夹过才落位。
    /// 夹完的位置回给客户端，它照这个摆自己；技能的距离判定用的也是这份坐标。
    /// </summary>
    public MoveState Move(Member member, MoveRequest request)
    {
        lock (_gate)
        {
            var now = NowMs();

            // Q2：定身 / 眩晕时这一步不生效。位置原样回，blocked 让客户端无论偏差多小都照这个回退；
            // 时钟照常推进，解控后不会攒出一次瞬移。
            if (member.Status.Rooted)
            {
                member.TouchMoveClock(now);
                return new MoveState
                {
                    XMm = (int)Math.Round(member.X),
                    YMm = (int)Math.Round(member.Y),
                    DistanceMm = DistanceToBossMm(member),
                    Blocked = true,
                };
            }

            var bossX = (double)_boss.SpawnMm.X;
            var bossY = (double)_boss.SpawnMm.Y;
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
            var dtMs = Math.Clamp(now - member.MoveClockMs(), 1, 200);
            member.TouchMoveClock(now);
            // 减速只压这一段的步长，不冻结；夹在 -90%，再慢也能一步步挪。
            var speedFactor = (100 + member.Status.SlowPct) / 100.0;
            var maxStep = _config.Arena.MoveSpeedMmPerS * dtMs / 1000.0 * speedFactor;

            var dx = targetX - member.X;
            var dy = targetY - member.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len > maxStep && len > 0)
            {
                var scale = maxStep / len;
                targetX = member.X + dx * scale;
                targetY = member.Y + dy * scale;
                clamped = true;
            }

            member.X = targetX;
            member.Y = targetY;

            return new MoveState
            {
                XMm = (int)Math.Round(member.X),
                YMm = (int)Math.Round(member.Y),
                DistanceMm = DistanceToBossMm(member),
                Clamped = clamped,
            };
        }
    }

    // ---- 收尾 ----

    /// <summary>断线或全场没人。这场没打完就不结算，也不留记录，重进是另一场。</summary>
    public void Abandon()
    {
        lock (_gate)
        {
            _closed = true;
            foreach (var member in _members.Values)
            {
                member.Pending = null;
                member.Status.Clear();
            }
            _bossUnit.Status.Clear();
        }
    }
}
