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

/// <summary>
/// 一条本机 WebSocket 就是一个人，带同一个 party 参数的连接进同一场。
/// 网关鉴权和协议加密按方案放到后续，这里只听 127.0.0.1，
/// 端口从配置来，配 0 就随机取一个，启动时把实际地址打出来。
///
/// 连接参数（query）：class 选职业（缺省用配置里的默认职业），
/// char 是角色编号（缺省用配置里的 caster.id），party 是队伍钥匙（缺省独享一场）。
/// </summary>
public sealed partial class BattleServer(
    BattleConfig config, SettlementStore settlements, ICharacterStore characters,
    IExpSettlement expSettlement, ITalismanSpend talismans, IPillSpend pills,
    IReviveScrollUse scrolls,
    IBattleProfileSource profiles, ISectTechSource sectTech,
    ISiegeSectSource sectFacts, ISiegeResultSink siegeResult, IWorldBossSink worldBossSink,
    IDeserterPenalty deserter)
{
    /// <summary>20Hz。前摇、冷却、后摇的判定精度都在这个粒度上。</summary>
    const int TickIntervalMs = 50;

    const int MaxMessageBytes = 64 * 1024;

    static long _sessionSeq;

    readonly ConcurrentDictionary<string, Party> _parties = new();
    readonly object _gate = new();

    // 战场：同一 match 的同一队先登记，凑满 team_size 或到时限才一起开场，
    // 因为同队战力要压进 1.3 倍必须先知道全队最高的那个人。
    readonly Dictionary<string, List<BattlefieldEntry>> _fieldWaits = new(StringComparer.Ordinal);
    readonly object _fieldGate = new();

    // 城战：同一 match 的同一队先登记，凑满 team_size 或到时限才一起开场。
    // 与战场分开排队，城战不需要等全队算平衡系数，但两支队伍要共用同一场实例，
    // 所以也按「队」登记；属性在登记时就已经是全额，不再二次调整。
    readonly Dictionary<string, List<BattlefieldEntry>> _siegeWaits = new(StringComparer.Ordinal);
    readonly object _siegeGate = new();

    // 城战：成员编号 → 队伍编号（0 / 1）。只给同屏表现用，不进技能、伤害与结算。
    readonly Dictionary<ulong, uint> _siegeTeams = new();

    // 世界 Boss：这一进程里已经打掉的场次。打完入口就关，不再接受新的进场；
    // 和场次一样只活在内存里（技术架构第 6 节：在线状态放进程内存）。
    readonly HashSet<string> _worldBossFought = new(StringComparer.Ordinal);

    // Q9：掉线等回连的人（角色号 → 那条记录）。只有主线（副本 / 练习场 / 竞技场）
    // 用这套：那三个模式一场只由一条 party 钥匙决定，同一场能被同一个人接回去。
    // 战场 / 城战 / 世界 Boss 是服务端组队与排队制，掉线仍按老口径立即离场。
    readonly ConcurrentDictionary<ulong, OfflineWatch> _offline = new();

    // Q9：本机口径的惩罚兜底。角色服可达时以角色库的到期时刻为准，这里只补
    // 「角色服没起 / 写不进」的那一段；两边都取，进场看较大的那个。
    readonly ConcurrentDictionary<ulong, long> _penaltyUntilMs = new();

    static long NowMs() => (long)Stopwatch.GetElapsedTime(0).TotalMilliseconds;

    /// <summary>这张队列里有没有断线重连表。没有就是老口径：断线即离场，不罚。</summary>
    bool ReconnectOn => !config.Reconnect.IsMissing;
}
