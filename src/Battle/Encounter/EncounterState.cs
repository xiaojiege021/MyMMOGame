using System.Diagnostics;
using Mmo.Proto.Battle;
using Mmo.Proto.Character;
using TalismanPool = Mmo.Proto.Character.TalismanPool;

namespace Battle;

/// <summary>
/// 一场 Boss 战，可以是一个人，也可以是一队人（连接带同一个 party 参数就是一队）。
/// 状态全在内存：进程一停这场就没了，重启不会恢复，也不会把没写下的奖励补出来。
///
/// 每个人独立的前摇、后摇、冷却和蓝条；Boss 的血、阶段和出手是全场共享的。
/// 一次只结算一个技能是对单个人说的：我前摇里再发会被挡，队友的手不受影响。
/// 冷却没好、蓝不够、距离不够、目标对不上，都在结算之前退回去，不碰任何人的生命。
/// </summary>
public sealed partial class Encounter
{
    readonly object _gate = new();
    readonly BattleConfig _config;
    readonly SettlementStore _settlements;
    readonly ICharacterStore _characters;
    readonly IExpSettlement _expSettlement;
    readonly ITalismanSpend _talismans;
    readonly IPillSpend _pills;
    readonly IReviveScrollUse _scrolls;
    readonly IBattleProfileSource _profiles;
    readonly DungeonDef? _dungeon;
    readonly Dictionary<ulong, Member> _members = [];

    // Q2：Boss 也吃效果（DoT / 破甲），与玩家共用 StatusSet。
    readonly BossHost _bossUnit;
    readonly Random _rng = new();

    /// <summary>
    /// 这一场打谁。缺省是 config/battle.json 的 boss（副本、练习场、竞技场、战场、
    /// 城战全是它）；世界 Boss 的一场由 config/world_boss.json 的 boss 段给，
    /// 两条线各自独立，谁都不会去改对方的数值。
    /// </summary>
    readonly BossConfig _boss;

    readonly long _bossHpMax;
    readonly double _monsterDamageScale;
    readonly int _partySize;

    /// <summary>
    /// P3-5a：结算人数。0 表示没人设过，按构造时的 party_size 结算（老路径的行为）。
    /// 世界 Boss 在每次有人进场时把它抬到本场实际人数，组队加成按实际人数算。
    /// </summary>
    int _settlementPartySize;

    long _bossHp;
    long _damageTotal;
    int _castCount;
    // 这一场击杀的有资格怪物数。目前只有一个 Boss，打倒它就是 1；
    // 以后副本加怪，每倒一只在这里加一，历练 = config 的 drill × 这个数。
    int _qualifiedKills;
    // 这一场用掉了第几张符。只用来拼事务号：同一场同一个人第几张，重放不会扣两次。
    int _talismanUses;
    int _pillUses;
    // 卷轴另记一套序号：符与卷轴用同一个角色服事务号表，编号不能撞。
    int _scrollUses;
    string _settlementId = "";
    bool _closed;

    /// <summary>
    /// Q9：这一场的收尾事实。通关时是 <see cref="Close"/> 发出去的那一条；没打完就散场
    /// （<see cref="Abandon"/>）时保持 null——「没结算」与「结算了但没通关」是两件事。
    /// 重连到已经收尾的场只把这一条原样回去，绝不调用第二次结算。
    /// </summary>
    public BattleEnded? EndSnapshot { get; private set; }

    /// <summary>这一场是否已经收尾（通关或散场）。</summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <summary>按角色号找这一场里的成员；不在场返回 null（Q9 用它判断能不能续战）。</summary>
    public Member? FindMember(ulong memberId)
    {
        lock (_gate)
        {
            return _members.TryGetValue(memberId, out var member) ? member : null;
        }
    }

    /// <summary>这一场里现在还有几个人。Q9 用它判断散场（最后一个在线的人走了才散）。</summary>
    public int MemberCount
    {
        get
        {
            lock (_gate)
            {
                return _members.Count;
            }
        }
    }

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
        IPillSpend pills, IReviveScrollUse scrolls, IBattleProfileSource profiles, DungeonDef? dungeon,
        BossConfig? boss = null)
    {
        _config = config;
        _settlements = settlements;
        _characters = characters;
        _expSettlement = expSettlement;
        _talismans = talismans;
        _pills = pills;
        _scrolls = scrolls;
        _profiles = profiles;
        _dungeon = dungeon;
        _bossUnit = new BossHost(this);
        // 不传就用配置里那一个 boss：老调用点一个都没变。
        _boss = boss ?? config.Boss;
        SessionId = sessionId;
        _partySize = partySize;
        _bossHpMax = Scale(_boss.HpMax, config.Dungeons.MonsterHpScalePerPlayer, partySize);
        _monsterDamageScale = Factor(config.Dungeons.MonsterDamageScalePerPlayer, partySize);
        _bossHp = _bossHpMax;
    }

    public ulong SessionId { get; }

    /// <summary>这一场的怪物生命上限（缩放后），开场回包给客户端。</summary>
    public long BossHpMax => _bossHpMax;

    public int PartySize => _partySize;

    internal EffectTable EffectsTable => _config.Effects;

    internal ulong BossIdForStatus => _boss.Id;

    internal long BossHpForStatus => _bossHp;

    internal void SetBossHpForStatus(long value) => _bossHp = Math.Max(0, value);

    /// <summary>
    /// P3-5a：这一场结算用的来源标签（写进角色服那一笔流水的 source）。
    /// 副本、练习场、竞技场、战场、城战都是 dungeon，世界 Boss 是 world_boss。
    /// 只改标签，不改基础经验、组队加成、同门加成里任何一个数。
    /// </summary>
    public string SettlementSource { get; set; } = "dungeon";

    /// <summary>
    /// P3-5a：结算时用的人数。世界 Boss 的人数随进场变化，构造那一刻还不知道最后几个人，
    /// 所以每进一个人把它抬到「本场实际人数」。没人调过就是构造时的 party_size：
    /// 副本 / 战场 / 城战 / 练习场的结算人数一个字节都没变。
    /// </summary>
    public void SetSettlementPartySize(int size)
    {
        lock (_gate)
        {
            _settlementPartySize = Math.Max(_settlementPartySize, Math.Max(1, size));
        }
    }

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

    /// <summary>诫律（牧师的两个分支之一）：没有复活这条能力。</summary>
    const string CannotResurrectBranch = "jielv";

    /// <summary>生息：15 级一转就有复活，45 级二转只把扣经验的比例减小。</summary>
    const string ShengxiBranch = "shengxi";

    /// <summary>
    /// 这个人的角色库快照。第一次用到时才向角色服要，之后缓存在这条命上。
    /// 没配上游时返回 null：复活仍按一转基础比例，不因为查不到就不让拉人。
    /// </summary>
    BattleProfile? ProfileOf(Member member)
    {
        if (!member.ProfileChecked)
        {
            member.Profile = _profiles.Get(member.Id);
            member.ProfileChecked = true;
        }
        return member.Profile;
    }

    /// <summary>
    /// B6：进场时在锁外把角色快照取一次（觉醒技编号要随开场包下发）。
    /// Open 里只读这份缓存，锁内不做服间调用——一次 gRPC 超时会把 tick 一起卡住。
    /// </summary>
    public void PrimeProfile(Member member) => ProfileOf(member);

    /// <summary>单调时钟，毫秒。冷却和前摇都用它算，不受系统时间跳变影响。</summary>
    static long NowMs() => (long)Stopwatch.GetElapsedTime(0).TotalMilliseconds;

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
        var dx = member.X - _boss.SpawnMm.X;
        var dy = member.Y - _boss.SpawnMm.Y;
        return (uint)Math.Round(Math.Sqrt(dx * dx + dy * dy));
    }

    static uint DistanceBetweenMm(Member a, Member b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (uint)Math.Round(Math.Sqrt(dx * dx + dy * dy));
    }
}
