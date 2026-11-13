using System.Diagnostics;
using Mmo.Proto.Battle;
using Mmo.Proto.Character;
using TalismanPool = Mmo.Proto.Character.TalismanPool;

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

    /// <summary>
    /// 接受这次施法之前的治疗组冷却。只有治疗技能会用到——红符与治疗共用这一组，
    /// 撤掉一次治疗要把这一组也退回原样，否则取消一次就白占了两千毫秒。
    /// </summary>
    public required long PrevHealGroupReadyAtMs { get; init; }

    /// <summary>目标。打 Boss 的技能是 Boss 编号，治疗和复活是队友编号。</summary>
    public required ulong TargetId { get; init; }
}

/// <summary>一场里的一条命。编号就是连接进来时的角色编号。</summary>
public sealed class Member
{
    public required ulong Id { get; init; }
    public required ClassDef Class { get; init; }

    /// <summary>
    /// 竞技场模板。为空表示这一场按职业表算属性——副本、练习场与所有不带
    /// arena_mode 的老连接都走这条，行为与 P0 完全一致。
    /// </summary>
    public ArenaTemplate? Template { get; init; }

    /// <summary>攻击：有模板就用模板给的绝对值，没有才读职业表的主属性系数。</summary>
    public double AttackPower() => Template?.Attack ?? Class.Attack();

    /// <summary>治疗：模板给绝对值；没有模板仍是精神 × 职业治疗系数。</summary>
    public double HealPower() => Template?.HealPower ?? Class.Attributes.Spirit * Class.HealPerSpirit;

    /// <summary>仇恨稳定值：模板给绝对值；没有模板仍是体力 × 职业系数。</summary>
    public long ThreatStability() => Template?.ThreatStability ?? Class.ThreatStability();

    public long Hp { get; internal set; }
    public long HpMax { get; internal set; }
    public long Mana { get; internal set; }
    public long ManaMax { get; internal set; }

    // 实时坐标。客户端报上来的位置只是「想去哪」，夹过之后才写进这两个。
    internal double X;
    internal double Y;
    long _lastMoveMs;

    internal readonly Dictionary<uint, long> ReadyAtMs = [];
    internal PendingCast? Pending;
    internal long RecoveryUntilMs = long.MinValue;

    /// <summary>
    /// 治疗组的共用冷却到期时刻。方案 12.1 写定红符和红药、牧师治疗共用冷却——
    /// 所以这几条不是各记各的：牧师的治疗技能结算会写这里，红符用掉也会写这里，
    /// 两边提交前都查这里。蓝符是另一组（mana），不并进来。
    /// </summary>
    internal long HealGroupReadyAtMs = long.MinValue;

    /// <summary>
    /// 蓝量组的共用冷却到期时刻。方案 12.1 写定蓝符「和蓝药共用冷却」，
    /// 且红符「不和蓝符共用冷却」——所以蓝符用掉只写这里，不碰治疗组；
    /// 蓝药以后接进战斗服时写同一个字段即可，不必另开一组。
    /// </summary>
    internal long ManaGroupReadyAtMs = long.MinValue;

    /// <summary>打出去的伤害累计。仇恨里看得见的那部分。</summary>
    internal long DamageDealt;

    public bool Down => Hp <= 0;

    /// <summary>有效仇恨 = 打出的伤害 + 稳定值（职业表是体力 × 系数，竞技场是模板值）。</summary>
    public long EffectiveThreat => DamageDealt + ThreatStability();

    internal void TouchMoveClock(long now) => _lastMoveMs = now;

    internal long MoveClockMs() => _lastMoveMs;
}

/// <summary>
/// 一场 Boss 战，可以是一个人，也可以是一队人（连接带同一个 party 参数就是一队）。
/// 状态全在内存：进程一停这场就没了，重启不会恢复，也不会把没写下的奖励补出来。
///
/// 每个人独立的前摇、后摇、冷却和蓝条；Boss 的血、阶段和出手是全场共享的。
/// 一次只结算一个技能是对单个人说的：我前摇里再发会被挡，队友的手不受影响。
/// 冷却没好、蓝不够、距离不够、目标对不上，都在结算之前退回去，不碰任何人的生命。
/// </summary>
public sealed class Encounter
{
    readonly object _gate = new();
    readonly BattleConfig _config;
    readonly SettlementStore _settlements;
    readonly ICharacterStore _characters;
    readonly IExpSettlement _expSettlement;
    readonly ITalismanSpend _talismans;
    readonly IReviveScrollUse _scrolls;
    readonly Dictionary<ulong, Member> _members = [];

    readonly long _bossHpMax;
    readonly double _monsterDamageScale;
    readonly int _partySize;

    long _bossHp;
    long _damageTotal;
    int _castCount;
    // 这一场用掉了第几张符。只用来拼事务号：同一场同一个人第几张，重放不会扣两次。
    int _talismanUses;
    // 卷轴另记一套序号：符与卷轴用同一个角色服事务号表，编号不能撞。
    int _scrollUses;
    string _settlementId = "";
    bool _closed;

    // Boss 的出手与阶段
    int _phaseIndex;
    (ulong MemberId, long LandAtMs)? _bossPending;
    long _nextBossAttackAt;

    /// <summary>
    /// party_size 是进本时说好的人数（与角色服扣减用的是同一个数字）。
    /// 怪物生命与伤害按它缩放，机制不变：技能、阶段血线比例、结算经验都不跟人数改。
    /// 缩放系数在 config/dungeons.json，方案没写定曲线，代码里不给兜底数字。
    /// 单人（练习场与所有不带 party_size 的老连接）倍率恰好是 1，行为与 P0 完全一致。
    /// </summary>
    public Encounter(
        BattleConfig config, SettlementStore settlements, ICharacterStore characters,
        ulong sessionId, int partySize, IExpSettlement expSettlement, ITalismanSpend talismans,
        IReviveScrollUse scrolls)
    {
        _config = config;
        _settlements = settlements;
        _characters = characters;
        _expSettlement = expSettlement;
        _talismans = talismans;
        _scrolls = scrolls;
        SessionId = sessionId;
        _partySize = partySize;
        _bossHpMax = Scale(config.Boss.HpMax, config.Dungeons.MonsterHpScalePerPlayer, partySize);
        _monsterDamageScale = Factor(config.Dungeons.MonsterDamageScalePerPlayer, partySize);
        _bossHp = _bossHpMax;
    }

    public ulong SessionId { get; }

    /// <summary>这一场的怪物生命上限（缩放后），开场回包给客户端。</summary>
    public long BossHpMax => _bossHpMax;

    public int PartySize => _partySize;

    /// <summary>倍率 = 1 + (人数 - 1) × 每多一人的增量；单人固定 1，不经过浮点。</summary>
    static double Factor(double perPlayer, int partySize) =>
        partySize <= 1 ? 1.0 : 1.0 + (partySize - 1) * perPlayer;

    static long Scale(long baseValue, double perPlayer, int partySize)
    {
        var factor = Factor(perPlayer, partySize);
        return factor == 1.0
            ? baseValue
            : checked((long)Math.Round(baseValue * factor));
    }

    static long ScaleDamage(long baseDamage, double scale) =>
        scale == 1.0 ? baseDamage : checked((long)Math.Round(baseDamage * scale));

    /// <summary>单调时钟，毫秒。冷却和前摇都用它算，不受系统时间跳变影响。</summary>
    static long NowMs() => (long)Stopwatch.GetElapsedTime(0).TotalMilliseconds;

    // ---- 成员 ----

    /// <summary>
    /// 一个人进场。编号撞了抛异常，由连接层拒绝这条连接。
    /// arena 不为空时，生命、蓝、攻击、治疗与仇恨稳定值全部取竞技场模板，
    /// 不看职业表的属性，也不看这个人的装备与强化等级。
    /// </summary>
    public Member Join(ulong memberId, ClassDef cls, ArenaTemplate? arena = null)
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
                HpMax = arena?.HpMax ?? cls.HpMax(_config.Classes.HpPerVitality),
                ManaMax = arena?.ManaMax ?? cls.ManaMax(),
                X = _config.Caster.SpawnMm.X,
                Y = _config.Caster.SpawnMm.Y,
            };
            member.Hp = member.HpMax;
            member.Mana = member.ManaMax;
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
                BossId = _config.Boss.Id,
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

            return opened;
        }
    }

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
            else if (request.TargetId != _config.Boss.Id)
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

    /// <summary>
    /// 走位。客户端报的是「想去哪」，这里按场地半径和速度上限夹过才落位。
    /// 夹完的位置回给客户端，它照这个摆自己；技能的距离判定用的也是这份坐标。
    /// </summary>
    public MoveState Move(Member member, MoveRequest request)
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
            var dtMs = Math.Clamp(now - member.MoveClockMs(), 1, 200);
            member.TouchMoveClock(now);
            var maxStep = _config.Arena.MoveSpeedMmPerS * dtMs / 1000.0;

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

    // ---- 用符 ----

    /// <summary>
    /// 用一张符（红符回血、蓝符回蓝）。方案 12.1 的边界都落在这里：
    ///   · 只回复自己——请求里没有目标编号，这里也恒拿 member 自己当目标，
    ///     队友只能靠牧师的技能治，符这条路指不过去；
    ///   · 不复活——倒地（Hp ≤ 0）时直接拒绝，死了就点不了符，也就没有「把自己拉起来」；
    ///   · 回复不超过同级药——回复值由角色服按配置给，配置校验里已经卡死上限，
    ///     这里只按池上限截断，不自己算第二套数；
    ///   · 蓝符不回血、不提高精神、不增加伤害——只写 Mana，另一个池与属性一律不碰。
    ///
    /// 扣符交给角色服（背包只有它能让写），扣不掉就不回：宁可这一手没做成，
    /// 也不做「本地凭空变出一张符」或「回了但符没扣」。
    /// 红符与牧师治疗共用治疗组冷却，蓝符用蓝量组，两组互不占用。
    /// </summary>
    public ServerMessage UseTalisman(Member member, UseTalismanRequest request)
    {
        var itemId = (request.ItemId ?? "").Trim();
        if (itemId.Length == 0)
        {
            return RejectTalisman(itemId, "not_talisman", 0, member.Hp, member.Mana);
        }

        // 只读查询：这个编号回血还是回蓝，只有角色服读配置说了算。不扣、不写库。
        // 先在锁外问清楚，才能在扣符之前判「池满没满」「哪一组冷却到没到」。
        var info = _talismans.Describe(member.Id, itemId);
        if (!info.Ok)
        {
            return RejectTalisman(itemId, info.Reason, 0, member.Hp, member.Mana);
        }

        string precheck;
        long now;
        int seq;
        lock (_gate)
        {
            now = NowMs();
            precheck = TalismanPrecheck(member, now, info.Pool);
            seq = ++_talismanUses;
        }

        if (precheck.Length > 0)
        {
            var retry = 0u;
            if (precheck == "cooldown")
            {
                lock (_gate)
                {
                    var readyAt = info.Pool == TalismanPool.Mana
                        ? member.ManaGroupReadyAtMs
                        : member.HealGroupReadyAtMs;
                    retry = (uint)Math.Max(0, readyAt - NowMs());
                }
            }

            return RejectTalisman(itemId, precheck, retry, member.Hp, member.Mana);
        }

        // 事务号在这一场里唯一：同一场同一个人第几张，重放不会扣两次。
        var txnId = "talisman:" + SessionId.ToString("x16")
            + ":" + member.Id.ToString("x16") + ":" + seq.ToString();

        // 网络调用放在锁外面：一次扣符最长等五秒，不能把整场的结算一起挂住。
        var spend = _talismans.Spend(member.Id, itemId, txnId);
        if (!spend.Ok)
        {
            return RejectTalisman(itemId, spend.Reason, 0, member.Hp, member.Mana);
        }

        lock (_gate)
        {
            // 扣符这一下里这场可能已经结束了、人倒了，或者池被别人加满了：
            // 正常不会走到这里（上面预判过），真撞上就按对应原因拒绝，不回另一个池。
            if (member.Down)
            {
                return RejectTalisman(itemId, "caster_down", 0, member.Hp, member.Mana);
            }

            // 回包里的池是权威：配置改了要重启才生效，两次调用之间不会变。
            if (spend.Pool == TalismanPool.Mana && member.Mana < member.ManaMax)
            {
                var restored = Math.Min(spend.ManaRestore, member.ManaMax - member.Mana);
                member.Mana += restored;
                if (spend.CooldownMs > 0)
                {
                    // 只写蓝量组：和蓝药共用，和治疗组、红符都不共用。
                    member.ManaGroupReadyAtMs = NowMs() + spend.CooldownMs;
                }

                return TalismanApplied(member, itemId, spend, 0, restored);
            }

            if (spend.Pool == TalismanPool.Hp && member.Hp < member.HpMax)
            {
                var healed = Math.Min(spend.HpRestore, member.HpMax - member.Hp);
                member.Hp += healed;
                if (spend.CooldownMs > 0)
                {
                    // 只写治疗组：和牧师治疗共用，和蓝符不共用。
                    member.HealGroupReadyAtMs = NowMs() + spend.CooldownMs;
                }

                return TalismanApplied(member, itemId, spend, healed, 0);
            }

            return RejectTalisman(itemId,
                spend.Pool == TalismanPool.Mana ? "mana_full" : "hp_full",
                0, member.Hp, member.Mana);
        }
    }

    /// <summary>
    /// 符真扣掉了，把这一个池的增量回给本人。另一个池的数值原样回、不改动——
    /// 「蓝符不回血」「红符不回蓝」在这里也落死：healed 与 mana_restored 只有一个非零。
    /// 伤害、精神、职业属性一个都不碰（方案 12.1「不提高精神，不增加伤害」）。
    /// </summary>
    static ServerMessage TalismanApplied(
        Member member, string itemId, TalismanSpendResult spend, long healed, long restored) =>
        new()
        {
            TalismanUsed = new TalismanUsed
            {
                CasterId = member.Id,
                ItemId = itemId,
                Healed = healed,
                HpAfter = member.Hp,
                CooldownMs = (uint)Math.Max(0, spend.CooldownMs),
                CooldownGroup = spend.CooldownGroup,
                CountLeft = spend.CountLeft,
                Pool = spend.Pool.ToString().ToLowerInvariant(),
                Restored = healed + restored,
                ManaAfter = member.Mana,
                ManaRestored = restored,
            },
        };

    /// <summary>
    /// 用符的本地前置判定。池由角色服的只读查询给，所以满池与冷却都查对的那一个：
    /// 红符看血与治疗组，蓝符看蓝与蓝量组。返回空串表示能进下一步（去扣符）。
    /// 这一步在扣符之前，判不过一个字都不写库。
    /// </summary>
    string TalismanPrecheck(Member member, long now, TalismanPool pool)
    {
        if (_closed || _bossHp <= 0)
        {
            return "not_in_battle";
        }

        // 倒地不能点符：倒地只能靠复活卷轴或牧师的生息，符不复活。
        if (member.Down)
        {
            return "caster_down";
        }

        if (pool == TalismanPool.Mana)
        {
            // 冷却先于池满：两张连着用时，客户端该拿到「还差多久」，
            // 而不是「蓝满了」——后者会让人以为等蓝耗掉就能再用。
            if (now < member.ManaGroupReadyAtMs)
            {
                return "cooldown";
            }

            if (member.Mana >= member.ManaMax)
            {
                // 满蓝用了也是白烧一张，直接挡下，别让玩家白扣。
                return "mana_full";
            }

            return "";
        }

        if (pool != TalismanPool.Hp)
        {
            // 只有查得到池的符能走到这里；Unspecified 不该出现。
            return "not_talisman";
        }

        if (now < member.HealGroupReadyAtMs)
        {
            return "cooldown";
        }

        if (member.Hp >= member.HpMax)
        {
            // 满血用了也是白烧一张，直接挡下，别让玩家白扣。
            return "hp_full";
        }

        return "";
    }

    static ServerMessage RejectTalisman(
        string itemId, string reason, uint retryAfterMs, long hpAfter, long manaAfter) =>
        new()
        {
            TalismanRejected = new TalismanRejected
            {
                ItemId = itemId,
                Reason = reason,
                RetryAfterMs = retryAfterMs,
                HpAfter = hpAfter,
                ManaAfter = manaAfter,
            },
        };

    // ---- 用卷轴 ----

    /// <summary>
    /// 用一张复活卷轴。方案 12.1 写定「只拉起自己，不能拉队友」——请求里没有
    /// 目标编号，这里也只把发起者本人的血拉起来，队友只能靠牧师的生息。
    /// 「结算后或人已离开就拒绝」「不满血、不回蓝、不加属性」都落在这几行：
    /// 生命按角色服回包里的比例回到一小段，蓝量与属性一个字段都不写。
    ///
    /// 冷却是这个人的全局冷却，存在角色库：先只读查到期时刻，本地判不过就
    /// 根本不扣卷轴；判过了才让角色服在一笔事务里扣卷、扣当前级经验、写冷却。
    /// 这一段不开始生息技能的冷却，也不占用治疗组、蓝量组——卷轴是第三段计时。
    /// 扣成功之后必须把人拉起来（卷轴、经验、冷却都已经落库），不能白扣。
    /// </summary>
    public ServerMessage UseReviveScroll(Member member, UseReviveScrollRequest request)
    {
        var itemId = (request.ItemId ?? "").Trim();
        if (itemId.Length == 0)
        {
            return RejectScroll(itemId, "not_scroll", 0, member.Hp, member.Mana);
        }

        // 只读查询：编号对不对、冷却到没到、背包有几张、回血比例多少。
        // 冷却到期来自角色库那一行，不是本场的计时器，所以换一场仍然读到它。
        var info = _scrolls.Describe(member.Id, itemId);
        if (!info.Ok)
        {
            return RejectScroll(itemId, info.Reason, 0, member.Hp, member.Mana);
        }

        string precheck;
        int seq;
        uint retry = 0;
        lock (_gate)
        {
            precheck = ScrollPrecheck(member, info);
            seq = ++_scrollUses;
            if (precheck == "cooldown")
            {
                var left = Math.Max(0L, info.ReadyAtMs - info.NowMs);
                retry = (uint)Math.Min(left, (long)uint.MaxValue);
            }
        }

        if (precheck.Length > 0)
        {
            return RejectScroll(itemId, precheck, retry, member.Hp, member.Mana);
        }

        // 事务号这一场唯一：同一场同一个人第几张，重放不会扣两次。
        var txnId = "scroll:" + SessionId.ToString("x16")
            + ":" + member.Id.ToString("x16") + ":" + seq.ToString();

        // 网络调用放在锁外面：一次扣卷最长等五秒，不能把整场的结算一起挂住。
        var used = _scrolls.Use(member.Id, itemId, txnId);
        if (!used.Ok)
        {
            // 角色服那笔事务自己再核一次冷却（两个战斗进程同时点也只过一笔）。
            return RejectScroll(itemId, used.Reason, 0, member.Hp, member.Mana);
        }

        lock (_gate)
        {
            // 按回包里的比例回到一小段生命：不满血、不回蓝、不加属性。
            var restore = Math.Max(1L, member.HpMax * used.HpRestorePct / 100);
            if (restore >= member.HpMax)
            {
                restore = Math.Max(1L, member.HpMax - 1);
            }

            member.Hp = restore;
            return new ServerMessage
            {
                ScrollUsed = new ReviveScrollUsed
                {
                    CasterId = member.Id,
                    ItemId = itemId,
                    HpAfter = member.Hp,
                    ManaAfter = member.Mana,
                    ExpCost = used.ExpCost,
                    Level = used.Level,
                    ExpAfter = used.ExpAfter,
                    CooldownMs = (uint)Math.Max(0, used.CooldownMs),
                    ReadyAtMs = used.ReadyAtMs,
                    CountLeft = used.CountLeft,
                },
            };
        }
    }

    /// <summary>
    /// 用卷轴的本地前置判定，全部在扣卷之前。冷却先于背包空判：还在冷却时
    /// 第二张该回「还差多久」，而不是「没卷轴了」让人以为买一张就能再用。
    /// </summary>
    string ScrollPrecheck(Member member, ReviveScrollInfo info)
    {
        if (_closed || _bossHp <= 0)
        {
            // 这场已经结算：方案 8.3 写定结算后拒绝。
            return "not_in_battle";
        }

        if (!_members.ContainsKey(member.Id))
        {
            // 人已经离开这场，同样拒绝。
            return "left_battle";
        }

        if (!member.Down)
        {
            // 还活着点不了自救卷轴；拉队友只能走生息。
            return "not_down";
        }

        if (info.NowMs < info.ReadyAtMs)
        {
            return "cooldown";
        }

        if (info.CountLeft <= 0)
        {
            return "no_scroll";
        }

        return "";
    }

    static ServerMessage RejectScroll(
        string itemId, string reason, uint retryAfterMs, long hpAfter, long manaAfter) =>
        new()
        {
            ScrollRejected = new ReviveScrollRejected
            {
                ItemId = itemId,
                Reason = reason,
                RetryAfterMs = retryAfterMs,
                HpAfter = hpAfter,
                ManaAfter = manaAfter,
            },
        };

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
            return outbox;
        }
    }

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
                        TargetId = _config.Boss.Id,
                        CastTimeMs = pending.CastTimeMs,
                    }, SkillRejectReason.SkillRejectTargetGone),
                });
                return;
            }

            // 只剩这么多血就只算这么多，不把溢出伤害记上去。
            var applied = Math.Min(DamageOf(member, skill), _bossHp);
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
                    TargetId = _config.Boss.Id,
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

            CheckPhase(outbox);

            if (_bossHp <= 0)
            {
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

        var restore = Math.Max(1L, raised.HpMax * _config.Classes.Resurrect.HpRestorePct / 100);
        var expCost = ExpCostOf(raised.Id);
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
    }

    /// <summary>
    /// 被拉起来的人只扣当前等级已有经验的一部分，不掉级；
    /// 当前级经验是零时这次不扣。等级不动，只动经验。
    /// 写不下去返回 null，由调用方把这次复活退回去。
    /// </summary>
    long? ExpCostOf(ulong characterId)
    {
        try
        {
            var state = _characters.Get(characterId);
            var cost = state.Exp * _config.Classes.Resurrect.ExpCostPct / 100;
            _characters.Save(characterId, state with { Exp = Math.Max(0, state.Exp - cost) });
            return cost;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"character_error=character={characterId} {ex.Message}");
            return null;
        }
    }

    long DamageOf(Member member, SkillDef skill) =>
        (long)Math.Round(member.AttackPower() * skill.DamageMultiplier);

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
                var after = victim.Hp - damage;
                victim.Hp = after > 0 ? after : 0;
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
        _config.Boss.Phases is { Count: > 0 } phases
            ? phases
            :
            [
                new PhaseConfig
                {
                    Name = _config.Boss.Name,
                    HpPctFrom = 100,
                    HpPctTo = 0,
                    Attack = _config.Boss.Attack,
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
            }
        }
    }

    /// <summary>只有通关才写记录。写失败就不给事务号，客户端拿不到号就领不了奖。</summary>
    BattleEnded Close(ulong killerId)
    {
        _closed = true;
        try
        {
            var record = new SettlementRecord
            {
                SettlementId = $"bt{SessionId:x16}",
                SessionId = SessionId,
                CasterId = killerId,
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

        // 同一场每人一份个人经验，交给角色服的那一笔结算。事务号用本地结算号，
        // 角色服按号幂等；没配上游时空实现什么都不做，P0 行为不变。
        // 伤害、掉落、疲劳、次数都不在这里改，组队加成只加经验。
        if (_settlementId.Length > 0)
        {
            _expSettlement.SettleParty(_settlementId, "dungeon", _partySize, _members.Keys.ToArray());
        }

        return new BattleEnded
        {
            SessionId = SessionId,
            Cleared = _bossHp <= 0,
            SettlementId = _settlementId,
            ExpGain = string.IsNullOrEmpty(_settlementId) ? 0 : _config.Boss.ExpOnClear,
        };
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

    PartyMember ToPartyMember(Member member) => new()
    {
        MemberId = member.Id,
        ClassId = member.Class.Id,
        Name = member.Class.Name,
        Hp = member.Hp,
        HpMax = member.HpMax,
        Down = member.Down,
    };

    SkillInfo ToSkillInfo(SkillDef skill) => new()
    {
        SkillId = skill.Id,
        Name = skill.Name,
        RangeMm = skill.RangeMm,
        ForeswingMs = (uint)Math.Max(0, skill.ForeswingMs),
        RecoveryMs = (uint)Math.Max(0, skill.RecoveryMs),
        CooldownMs = (uint)Math.Max(0, skill.CooldownMs),
        StaggerMs = (uint)Math.Max(0, skill.StaggerMs),
        ManaCost = (uint)Math.Max(0, skill.ManaCost),
    };

    uint DistanceToBossMm(Member member)
    {
        var dx = member.X - _config.Boss.SpawnMm.X;
        var dy = member.Y - _config.Boss.SpawnMm.Y;
        return (uint)Math.Round(Math.Sqrt(dx * dx + dy * dy));
    }

    static uint DistanceBetweenMm(Member a, Member b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (uint)Math.Round(Math.Sqrt(dx * dx + dy * dy));
    }
}
