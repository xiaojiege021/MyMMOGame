using System.Diagnostics;
using Mmo.Proto.Battle;
using Mmo.Proto.Character;
using TalismanPool = Mmo.Proto.Character.TalismanPool;

namespace Battle;

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

/// <summary>
/// 战场这一层的最终属性：职业表 + 装备强化折算，再乘同队平衡系数。
/// 与竞技场的 ArenaTemplate 分开：模板是按职业与分支取的固定值，
/// 这里是「装备生效之后再压缩」算出来的结果。
/// </summary>
public sealed record CombatStats(
    long Attack, long HpMax, long ManaMax, long HealPower, long ThreatStability);

/// <summary>一场里的一条命。编号就是连接进来时的角色编号。</summary>
public sealed class Member : IStatusHost
{
    public required ulong Id { get; init; }
    public required ClassDef Class { get; init; }

    /// <summary>
    /// 竞技场模板。为空表示这一场按职业表算属性——副本、练习场与所有不带
    /// arena_mode 的老连接都走这条，行为与 P0 完全一致。
    /// </summary>
    public ArenaTemplate? Template { get; init; }

    /// <summary>
    /// 战场算好的最终属性（装备生效 + 同队平衡）。优先级高于模板与职业表；
    /// 只有带 battlefield 参数的连接才会有它。
    /// </summary>
    public CombatStats? Combat { get; init; }

    /// <summary>
    /// 攻击：战场 > 竞技场模板 > 职业表主属性系数 + 已穿装备的固定攻击（B9）。
    /// 装备那一截只在走后一条时加：竞技场按模板统一属性、战场与城战各自已经
    /// 把装备折进 Combat，再加一次就是双算。装备固定攻击来自角色服快照，
    /// 没配上游时是 0，与 B9 之前逐字相同。
    /// </summary>
    public double AttackPower() =>
        Combat?.Attack ?? Template?.Attack
        ?? (Class.Attack(Bonus) + (Profile?.EquipAttack ?? 0));

    /// <summary>
    /// A9：这个人的加点。快照还没取到时是四条 0（与 A9 之前一样）；竞技场模板与
    /// 战场 / 城战的 Combat 不看它——那两条各自已经把属性算死了。
    /// </summary>
    public AttrBonus Bonus => Profile?.Bonus ?? AttrBonus.None;

    /// <summary>治疗：战场 > 模板 > 精神 × 职业治疗系数。</summary>
    public double HealPower() =>
        Combat?.HealPower ?? Template?.HealPower ?? Class.HealPower(Bonus);

    /// <summary>仇恨稳定值：战场 > 模板 > 体力 × 职业系数。</summary>
    public long ThreatStability() =>
        Combat?.ThreatStability ?? Template?.ThreatStability ?? Class.ThreatStability(Bonus);

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

    /// <summary>
    /// P2-4a：这个人的角色库快照（职业、分支、二转步数）。第一次用到时才向角色服要，
    /// 之后缓存在这条命上——复活判定要读它，一场里不会变。
    /// Q2：伤害第一段的境界加成也从这里取，所以「问过一次」要记下来：
    /// 查不到时不能每一手都去问一次上游。
    /// </summary>
    internal BattleProfile? Profile;

    internal bool ProfileChecked;

    public bool Down => Hp <= 0;

    /// <summary>
    /// Q2：这条命身上的效果。每人一份，DoT / HoT / 控制 / 护盾都只在这里算，
    /// 战斗服时钟推进，客户端只读快照。
    /// </summary>
    public StatusSet Status { get; internal set; } = null!;

    ulong IStatusHost.UnitId => Id;

    void IStatusHost.SetHp(long value) => Hp = Math.Max(0, value);

    /// <summary>有效仇恨 = 打出的伤害 + 稳定值（职业表是体力 × 系数，竞技场是模板值）。</summary>
    public long EffectiveThreat => DamageDealt + ThreatStability();

    internal void TouchMoveClock(long now) => _lastMoveMs = now;

    internal long MoveClockMs() => _lastMoveMs;
}

/// <summary>
/// Boss 作为效果宿主：血量和阶段仍是 Encounter 的字段，这里只是把同一套
/// StatusSet 接到它身上——Boss 也会吃 DoT / 破甲，不另写一份计时。
/// </summary>
sealed class BossHost : IStatusHost
{
    readonly Encounter _owner;

    public BossHost(Encounter owner)
    {
        _owner = owner;
        Status = new StatusSet(owner.EffectsTable, this);
    }

    public ulong UnitId => _owner.BossIdForStatus;

    public long Hp => _owner.BossHpForStatus;

    public long HpMax => _owner.BossHpMax;

    public bool Down => _owner.BossHpForStatus <= 0;

    public void SetHp(long value) => _owner.SetBossHpForStatus(value);

    public StatusSet Status { get; }
}

/// <summary>
/// 一场 Boss 战，可以是一个人，也可以是一队人（连接带同一个 party 参数就是一队）。
/// 状态全在内存：进程一停这场就没了，重启不会恢复，也不会把没写下的奖励补出来。
///
/// 每个人独立的前摇、后摇、冷却和蓝条；Boss 的血、阶段和出手是全场共享的。
/// 一次只结算一个技能是对单个人说的：我前摇里再发会被挡，队友的手不受影响。
/// 冷却没好、蓝不够、距离不够、目标对不上，都在结算之前退回去，不碰任何人的生命。
/// </summary>
