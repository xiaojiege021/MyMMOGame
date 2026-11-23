using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mmo.Proto.Battle;


namespace Battle;

sealed class PartyConn
{
    public required WebSocket Socket { get; init; }
    public required SemaphoreSlim Send { get; init; }
    public required Member Member { get; init; }

    /// <summary>
    /// P3-3a2：这条连接已经收到 BattleOpened。城战名单变化时只给已开场的连接
    /// 重发同屏档位，免得档位包抢在开场包前面、把客户端的第一条消息顶掉。
    /// </summary>
    public bool ViewReady { get; set; }

    /// <summary>
    /// P3-5a：这条连接可以收广播了。世界 Boss 一场最多 20 个人先后进场，
    /// 第 2 个人以后 tick 已经在跑，广播可能抢在开场包前面把客户端的第一条消息顶掉；
    /// 所以进场时先置 false，开场包发出去再置 true。其余路径默认 true，行为一字不变。
    /// </summary>
    public bool Ready { get; set; } = true;
}

/// <summary>
/// 战场里等着开场的一条连接。属性先按「职业表 + 装备强化折算」算成本场真实值，
/// 但先不进 Encounter：同队要压进 1.3 倍，得等这一队凑满或到时限、
/// 知道队里最高战力之后才能定每个人的系数。
/// </summary>

sealed class BattlefieldEntry
{
    public required WebSocket Socket { get; init; }
    public required SemaphoreSlim Send { get; init; }
    public required ulong CharId { get; init; }
    public required ClassDef Class { get; init; }
    public required long RawAttack { get; init; }
    public required long RawHp { get; init; }
    public required long RawMana { get; init; }
    public required long RawHeal { get; init; }
    public required long RawThreat { get; init; }
    public required long RawPower { get; init; }

    public long AdjustedPower { get; set; }
    public double K { get; set; } = 1.0;

    /// <summary>
    /// P3-3a3：只城战进场时从社交服读到的宗门科技等级（0 表示没有加成）。
    /// 事实值只活在这条连接上，离场即丢，不写角色库、不进结算。
    /// </summary>
    public int SectTechLevel { get; set; }

    /// <summary>
    /// 城战专属的第二层系数：装备全额（K=1）之上再乘宗门科技。
    /// 其他模式恒 1，所以战场 / 竞技场 / 副本 / 练习场的属性一字不变。
    /// </summary>
    public double TechFactor { get; set; } = 1.0;
    public Member? Member { get; set; }
    public bool Rejected { get; set; }
    public string RejectReason { get; set; } = "";

    /// <summary>开场算完系数就置位；true 表示已经 Join 成功、可以发开场包。</summary>
    public readonly TaskCompletionSource<bool> Ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>一场：一个 Encounter 加上连进来的所有人。最后一个人走了这场就散。</summary>

sealed class Party
{
    public required string Key { get; init; }
    public required Encounter Encounter { get; init; }
    public readonly List<PartyConn> Connections = [];
    public required CancellationTokenSource Ticker { get; init; }

    /// <summary>
    /// 这一场是哪个副本。没指定副本的连接是练习场，不套人数上限
    /// （P0 的老核对与老客户端都不带这个参数，不能被主线的 1 人上限挡掉）。
    /// </summary>
    public required DungeonDef? Dungeon { get; init; }

    /// <summary>进本人数，与角色服扣份额/疲劳用的是同一个数字；缺省 1（单人练习场）。</summary>
    public required int PartySize { get; init; }

    /// <summary>
    /// 这一场是不是竞技场。为空是普通副本或练习场；不为空时场上每个人的属性
    /// 都取竞技场模板，不看装备。同一个队伍钥匙不能一半人打副本、一半人打竞技场。
    /// </summary>
    public required ArenaModeDef? ArenaMode { get; init; }

    /// <summary>
    /// P3-3a4：城战两队对 Boss 的累计有效伤害。只在广播层按队伍累加，不改 Encounter
    /// 的伤害公式，也不进结算 / 经验 / 发奖；离场散场后随 party 一起丢掉。
    /// </summary>
    public readonly long[] SiegeDamage = new long[2];

    /// <summary>
    /// 本场登记过的成员 → 队伍。与 _siegeTeams 的区别：离场时不清这里，
    /// 否则打完就断线的人会让「胜方宗门是谁」查不到。只活在这一场实例里。
    /// </summary>
    public readonly Dictionary<ulong, uint> SiegeRoster = new();

    /// <summary>一场城战只判一次胜负、只写回一次。</summary>
    public bool SiegeFinished;

    /// <summary>
    /// P3-5a：这一场是哪个场次的世界 Boss（空表示不是世界 Boss）。同一场次只有一场，
    /// 钥匙由服务端按 config/world_boss.json 与自己的时钟算，客户端带不进来。
    /// </summary>
    public string WorldBossWindow { get; init; } = "";

    /// <summary>
    /// 本场每人归属的宗门编号（0 表示没宗门）。进场时向社交服只读查一次，
    /// 只用来算「伤害最高的宗门是哪个」——不加属性、不进伤害公式、不写角色库。
    /// 离场不清：名字要按本场名单算，打完就断线的人也一样算这一场。
    /// </summary>
    public readonly Dictionary<ulong, ulong> WorldBossSects = new();

    /// <summary>
    /// 按宗门累计的有效伤害。键 0 是「没有宗门的人」，只用来算总伤害，
    /// 挑归属时不参与。只活在这场实例的内存里。
    /// </summary>
    public readonly Dictionary<ulong, long> WorldBossDamage = new();

    /// <summary>一场世界 Boss 只把事实值交回入口一次。</summary>
    public bool WorldBossFinished;
}

/// <summary>
/// Q9：一个人从这条连接上掉下来了，还在等他自己回来。
///
/// 两个时刻各有各的用处，别合成一个：
///   - 宽限（config/reconnect.json 的 battle_grace_ms）先到：位置、血量、冷却与效果
///     都失效了，成员离开这一场（Boss 不再打他，队友名单里也没有他），但还没判他逃跑；
///   - 窗口（reconnect_window_s）才到：没人回来，按逃跑记一次惩罚。
/// 窗口内回来（哪怕宽限已过）都算回来，不罚；只有超过窗口才罚。
///
/// 这条记录只活在进程内存里（技术架构第 6 节：在线状态不进库）；惩罚本身由角色服
/// 按事实值落表，进程重启后惩罚还在。
/// </summary>

sealed class OfflineWatch
{
    public required ulong CharacterId { get; init; }
    public required ulong SessionId { get; init; }
    public required string PartyKey { get; init; }
    public required long SinceMs { get; init; }

    /// <summary>主动发 Close 帧退出的：立即判罚，不给宽限。</summary>
    public required bool Voluntary { get; init; }

    int _claimed;

    /// <summary>回连时抢一次。两个连接同时回来只有一个能接管这条记录。</summary>
    public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
}

/// <summary>
/// 一条本机 WebSocket 就是一个人，带同一个 party 参数的连接进同一场。
/// 网关鉴权和协议加密按方案放到后续，这里只听 127.0.0.1，
/// 端口从配置来，配 0 就随机取一个，启动时把实际地址打出来。
///
/// 连接参数（query）：class 选职业（缺省用配置里的默认职业），
/// char 是角色编号（缺省用配置里的 caster.id），party 是队伍钥匙（缺省独享一场）。
/// </summary>
