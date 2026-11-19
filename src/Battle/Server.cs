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

/// <summary>一场里的一条连接：_socket 加上这条命。</summary>
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
public sealed class BattleServer(
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

    public async Task RunAsync(CancellationToken stopping)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // 让 stdout 只有我们自己的 kv 行，核对脚本才好 grep。
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/battle", HandleAsync);
        app.Urls.Add("http://" + config.Listen);

        await app.StartAsync(stopping);

        var feature = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        Console.WriteLine("listen=" + feature.Addresses.First());

        try
        {
            await Task.Delay(Timeout.Infinite, stopping);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var query = context.Request.Query;

        var classId = QueryOrNull(query, "class") ?? config.DefaultClass;
        if (!config.Classes.TryGet(classId, out var cls))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var charRaw = QueryOrNull(query, "char");
        if (!ulong.TryParse(charRaw, out var charId) || charId == 0)
        {
            charId = config.Caster.Id;
        }

        // Q9/H4：逃跑惩罚挡在所有战斗入口的最前面（副本 / 竞技场 / 战场 / 城战 /
        // 世界 Boss 都算），不是只挡副本——否则换个模式就能绕过去。被罚的人连握手
        // 都不给，直接 403 带剩余秒数；重连回同一场的人不在惩罚表里，不受影响。
        if (ReconnectOn)
        {
            var remaining = await DeserterRemainingMsAsync(charId);
            if (remaining > 0)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "deserter_penalty",
                    remaining_s = (long)Math.Ceiling(remaining / 1000.0),
                });
                Console.WriteLine(
                    $"join_reject=deserter_penalty character={charId} remaining_ms={remaining}");
                return;
            }
        }

        // P3-5a：世界 Boss。入口在世界服，进去是战斗服的一场实例：按 config/world_boss.json
        // 的时刻出现，同一时刻一场（场次钥匙由服务端按自己的时钟算），首发上限走配置，
        // 一个人也能进。与副本 / 竞技场 / 战场 / 城战的参数互斥，混着带直接拒。
        var worldBossRaw = QueryOrNull(query, "worldboss");
        if (worldBossRaw is not null)
        {
            if (QueryOrNull(query, "dungeon") is not null
                || QueryOrNull(query, "arena_mode") is not null
                || QueryOrNull(query, "battlefield") is not null
                || QueryOrNull(query, "siege") is not null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=worldboss_with_other_mode entrance=" + worldBossRaw);
                return;
            }

            await HandleWorldBossAsync(context, worldBossRaw.Trim(), charRaw, cls);
            return;
        }

        // 城战：40 对 40，装备全额生效，不压同队战力、不套竞技场模板。
        // 与副本、竞技场、战场互斥；两支队伍共用同一场 Encounter，技能仍由本进程结算。
        var siegeRaw = QueryOrNull(query, "siege");
        if (siegeRaw is not null)
        {
            if (QueryOrNull(query, "dungeon") is not null
                || QueryOrNull(query, "arena_mode") is not null
                || QueryOrNull(query, "battlefield") is not null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=siege_with_other_mode match=" + siegeRaw);
                return;
            }

            if (config.Siege.IsMissing)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=siege_table_missing");
                return;
            }

            var siegeTeamRaw = (QueryOrNull(query, "team") ?? "").Trim().ToLowerInvariant();
            var siegeTeam = siegeTeamRaw switch { "a" => 0, "b" => 1, _ => -1 };
            if (siegeTeam < 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=siege_team_invalid raw=" + siegeTeamRaw);
                return;
            }

            // 城战属性以角色库为准，所以必须给角色编号：拿 caster 兜底会查到别人。
            if (!ulong.TryParse(charRaw, out var siegeCharId) || siegeCharId == 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=siege_character_missing");
                return;
            }

            await HandleSiegeAsync(context, siegeRaw.Trim(), siegeTeam, siegeCharId, cls);
            return;
        }

        // 战场：10 对 10，装备生效后按同队 1.3 倍压缩。与副本、竞技场互斥。
        var battlefieldRaw = QueryOrNull(query, "battlefield");
        if (battlefieldRaw is not null)
        {
            if (QueryOrNull(query, "dungeon") is not null || QueryOrNull(query, "arena_mode") is not null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=battlefield_with_other_mode match=" + battlefieldRaw);
                return;
            }

            if (config.Battlefield.IsMissing)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=battlefield_table_missing");
                return;
            }

            var teamRaw = (QueryOrNull(query, "team") ?? "").Trim().ToLowerInvariant();
            var team = teamRaw switch { "a" => 0, "b" => 1, _ => -1 };
            if (team < 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=battlefield_team_invalid raw=" + teamRaw);
                return;
            }

            // 战场属性以角色库为准，所以必须给角色编号：拿 caster 兜底会查到别人。
            if (!ulong.TryParse(charRaw, out var battleCharId) || battleCharId == 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=battlefield_character_missing");
                return;
            }

            await HandleBattlefieldAsync(context, battlefieldRaw.Trim(), team, battleCharId, cls);
            return;
        }

        // 不带 party 就独享一场，老客户端一个连接一场的行为不变。
        var partyKey = QueryOrNull(query, "party") ?? "solo-" + Guid.NewGuid().ToString("N");

        // 副本：不指定就是练习场，不套人数上限（老核对与老客户端都不带这个参数）。
        // 指定了才按 config/dungeons.json 校验，规则与角色服读同一份。
        var dungeonRaw = QueryOrNull(query, "dungeon");
        DungeonDef? dungeon = null;
        if (dungeonRaw is not null)
        {
            dungeon = config.Dungeons.Find(dungeonRaw);
            if (dungeon is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=dungeon_unknown id=" + dungeonRaw);
                return;
            }
        }

        // 进本人数决定怪物生命与伤害的缩放。不带就是单人：P0 的老客户端、
        // 老核对与练习场都走这一条，倍率恰好 1，行为不变。
        var partySize = 1;
        var partySizeRaw = QueryOrNull(query, "party_size");
        if (partySizeRaw is not null)
        {
            if (!int.TryParse(partySizeRaw, out partySize) || partySize < 1 || partySize > 100)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=party_size_invalid raw=" + partySizeRaw);
                return;
            }
        }

        if (dungeon is not null && partySize > dungeon.MaxPlayers)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            Console.WriteLine("join_reject=party_size_over_limit party_size=" + partySize
                + " limit=" + dungeon.MaxPlayers + " dungeon=" + dungeon.Id);
            return;
        }

        // 高难本不能单人进（龙巢至少 2 人）。普通本 min_players 缺省 1，这条不会命中。
        if (dungeon is not null && partySize < dungeon.MinPlayers)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            Console.WriteLine("join_reject=party_too_small party_size=" + partySize
                + " min=" + dungeon.MinPlayers + " dungeon=" + dungeon.Id);
            return;
        }

        // 竞技场：1v1 或 3v3，属性走模板（装备、强化、等级都不参与）。
        // 老连接不带 arena_mode，仍然走副本与练习场那一套，行为不变。
        var arenaModeRaw = QueryOrNull(query, "arena_mode");
        ArenaModeDef? arenaMode = null;
        ArenaTemplate? arenaTemplate = null;
        var branchId = QueryOrNull(query, "branch") ?? "";
        if (arenaModeRaw is not null)
        {
            if (dungeon is not null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=arena_with_dungeon arena_mode=" + arenaModeRaw
                    + " dungeon=" + dungeon.Id);
                return;
            }

            if (!config.ArenaMatch.TryGetMode(arenaModeRaw, out arenaMode))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=arena_mode_unknown id=" + arenaModeRaw);
                return;
            }

            // 人数必须正好是这个模式的开打人数：1v1 两人、3v3 六人。
            // 人数决定怪物缩放，不能让它和模式各说一套。
            if (partySize != arenaMode.Players)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=arena_party_size_mismatch arena_mode=" + arenaMode.Id
                    + " party_size=" + partySize + " players=" + arenaMode.Players);
                return;
            }

            if (!config.ArenaMatch.TryGetTemplate(classId, branchId, out arenaTemplate))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Console.WriteLine("join_reject=arena_template_missing class=" + classId
                    + " branch=" + branchId);
                return;
            }
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        // Q9：宽限期内回连。同一把队伍钥匙、同一个角色号、那一场还没收尾，就把原来
        // 那个人整份接回来：血量、蓝、位置、冷却与效果都还在 Encounter 里，开场包
        // 里的 effects 是服务端时钟算出的剩余毫秒，客户端不用自己补算。
        var send = new SemaphoreSlim(1, 1);
        if (TryResume(charId, partyKey, out var resumedParty, out var resumedMember))
        {
            await ServeResumedAsync(socket, send, resumedParty, resumedMember, context.RequestAborted);
            return;
        }

        Party party;
        Member member;
        var created = false;

        // 满员与换本要先把原因发回去再关，不能无声断开——客户端与核对都靠这条。
        EnterRejected? rejected = null;
        // Q9：这一场已经收尾时，把它当时那一条 Ended 原样回给重连的人。
        BattleEnded? settled = null;
        lock (_gate)
        {
            if (!_parties.TryGetValue(partyKey, out party!))
            {
                party = new Party
                {
                    Key = partyKey,
                    Encounter = new Encounter(
                        config, settlements, characters, NextSessionId(), partySize,
                        expSettlement, talismans, pills, scrolls, profiles, dungeon),
                    Ticker = new CancellationTokenSource(),
                    Dungeon = dungeon,
                    PartySize = partySize,
                    ArenaMode = arenaMode,
                };
                _parties[partyKey] = party;
                created = true;
            }
            else if (party.PartySize != partySize)
            {
                // 同一场里两个人报了不同的进本人数，缩放与扣减会对不上，直接挡住。
                rejected = new EnterRejected
                {
                    Reason = "party_size_mismatch",
                    Limit = (uint)party.PartySize,
                    Current = (uint)partySize,
                    DungeonId = party.Dungeon?.Id ?? "",
                };
            }
            else if (!SameDungeon(party.Dungeon, dungeon))
            {
                rejected = new EnterRejected
                {
                    Reason = "dungeon_mismatch",
                    Limit = (uint)(party.Dungeon?.MaxPlayers ?? 0),
                    Current = (uint)party.Connections.Count,
                    DungeonId = party.Dungeon?.Id ?? "",
                };
            }
            else if (!SameArena(party.ArenaMode, arenaMode))
            {
                // 同一个队伍钥匙一半人打竞技场、一半人打副本，属性来源会对不上。
                rejected = new EnterRejected
                {
                    Reason = "arena_mode_mismatch",
                    Limit = (uint)(party.ArenaMode?.Players ?? 0),
                    Current = (uint)(arenaMode?.Players ?? 0),
                    DungeonId = party.ArenaMode?.Id ?? "",
                };
            }
            else if (dungeon is not null && party.Connections.Count >= dungeon.MaxPlayers)
            {
                // 主线这里就是 1：第二个人进不来。
                rejected = new EnterRejected
                {
                    Reason = "party_full",
                    Limit = (uint)dungeon.MaxPlayers,
                    Current = (uint)party.Connections.Count,
                    DungeonId = dungeon.Id,
                };
            }

            if (rejected is not null)
            {
                member = null!;
            }
            else if (party.Encounter.IsClosed && party.Encounter.EndSnapshot is { } ended)
            {
                // Q9：这一场已经收尾了（队友还在连着，所以 party 还没散）。只把当时
                // 那一条 Ended 原样回去，不 Join、不结算、不再发掉落归属——重复结算
                // 在这里结构上不可能发生：结算只在 Encounter.Close 里做过一次。
                settled = ended;
                member = null!;
            }
            else
            {
                try
                {
                    member = party.Encounter.Join(charId, cls, arenaTemplate);
                }
                catch (InvalidOperationException ex)
                {
                    // 编号撞了：同一场里已经有人用这个角色编号。
                    Console.WriteLine($"join_reject=character={charId} reason={ex.Message}");
                    socket.Abort();
                    return;
                }

                // 宽限已过但窗口没到：他是重新入场的（满血、出生点），不是续战。
                // 但窗口内回来也算回来——把那条等待记录撤掉，别到点再罚一次。
                if (ReconnectOn && _offline.TryRemove(charId, out _))
                {
                    Console.WriteLine($"reconnect_rejoin=session={party.Encounter.SessionId} character={charId}");
                }

                party.Connections.Add(new PartyConn { Socket = socket, Send = send, Member = member });
            }
        }

        if (settled is not null)
        {
            Console.WriteLine(
                $"reconnect_settled=session={settled.SessionId} character={charId} "
                + $"settlement={settled.SettlementId} cleared={(settled.Cleared ? "true" : "false")}");
            await SendAsync(socket, send, new ServerMessage { Ended = settled }, context.RequestAborted);
            await CloseAsync(socket, "already_settled");
            return;
        }

        if (rejected is not null)
        {
            // 先把原因发出去，再正常关。客户端据此提示「本满了」。
            Console.WriteLine(
                $"join_reject=party={partyKey} dungeon={dungeon?.Id ?? "none"} reason={rejected.Reason} "
                + $"current={rejected.Current} limit={rejected.Limit}");
            await SendAsync(socket, send, new ServerMessage { EnterRejected = rejected },
                context.RequestAborted);
            await CloseAsync(socket, "enter_rejected");
            return;
        }

        // tick 一场只有一个循环，第二个人进来不另起。
        if (created)
        {
            _ = TickLoopAsync(party);
        }

        Console.WriteLine(
            $"session_open={party.Encounter.SessionId} caster={charId} class={cls.Id} party={partyKey} boss={config.Boss.Id}"
            + $" party_size={party.PartySize} boss_hp_max={party.Encounter.BossHpMax}"
            + (arenaMode is { } mode
                ? $" arena_mode={mode.Id} arena_players={mode.Players} stats_source=arena_template branch={branchId}"
                : ""));

        // 模板值单独打一行，核对脚本按这一行验「竞技场里用的是模板、不是职业表」。
        if (arenaTemplate is { } template)
        {
            Console.WriteLine(
                $"arena_template=class={template.ClassId} branch={template.BranchId}"
                + $" attack={template.Attack} hp_max={template.HpMax} mana_max={template.ManaMax}"
                + $" heal_power={template.HealPower} threat={template.ThreatStability}");
        }

        // 新人进场说给老人听，界面上的队友名单靠这条更新。不发给本人：
        // 本人已经在 Opened 里拿到整份名单，多发一条会把它的读序顶乱。
        var joined = new ServerMessage { MemberUpdated = new() { Member = Snapshot(member, party) } };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        // Q9：客户端主动发 Close 帧退出的（不是网断），不给宽限，立即按逃跑处理。
        var voluntary = false;
        try
        {
            await SendAsync(socket, send, new ServerMessage { Opened = party.Encounter.Open(member) }, linked.Token);
            await BroadcastAsync(party, joined, party.Ticker.Token, exceptSocket: socket);

            voluntary = await ReceiveLoopAsync(socket, send, party, member, linked.Token);
            await linked.CancelAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (Exception ex)
        {
            // 连接层出了没想到的错，打出来再让它断，不能无声无息地停。
            Console.WriteLine($"conn_error=session={party.Encounter.SessionId} {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            Detach(party, member, voluntary);
            Console.WriteLine($"session_close={party.Encounter.SessionId}");
        }
    }

    /// <summary>
    /// 战场的一次进场。先向角色服取只读快照（职业、分支、等级、强化总级），
    /// 按配置折算成「装备生效」后的属性与战力；然后等这一队凑满或到时限，
    /// 算出同队最高战力，把低于「最高 / ratio」的人抬到那条线，属性同乘一个系数。
    /// 两名对手只交换报名（编号 + 队伍 + 战力），角色库一个字段都不搬过来。
    /// </summary>
    async Task HandleBattlefieldAsync(
        HttpContext context, string matchId, int team, ulong charId, ClassDef cls)
    {
        var profile = profiles.Get(charId);
        if (profile is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            Console.WriteLine($"join_reject=battle_profile_missing match={matchId} character={charId}");
            return;
        }

        // 职业以角色库为准：query 里的 class 只是老客户端的兼容参数，改不了战场属性。
        if (!config.Classes.TryGet(profile.ClassId, out var realClass))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            Console.WriteLine("join_reject=battle_profile_bad_class match=" + matchId
                + " character=" + charId + " class=" + profile.ClassId);
            return;
        }

        var table = config.Battlefield;
        var level = profile.EnhanceTotal;
        var rawAttack = (long)Math.Round(realClass.Attack()) + level * table.AttackPerLevel;
        var rawHp = realClass.HpMax(config.Classes.HpPerVitality) + level * table.HpPerLevel;
        var rawMana = realClass.ManaMax() + level * table.ManaPerLevel;
        var rawHeal = (long)Math.Round(realClass.Attributes.Spirit * realClass.HealPerSpirit)
                      + level * table.HealPerLevel;
        var rawThreat = realClass.ThreatStability();
        var rawPower = table.Power(rawAttack, rawHp, rawMana, rawHeal);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var send = new SemaphoreSlim(1, 1);
        var entry = new BattlefieldEntry
        {
            Socket = socket,
            Send = send,
            CharId = charId,
            Class = realClass,
            RawAttack = rawAttack,
            RawHp = rawHp,
            RawMana = rawMana,
            RawHeal = rawHeal,
            RawThreat = rawThreat,
            RawPower = rawPower,
        };

        bool full;
        lock (_fieldGate)
        {
            var key = matchId + "|" + team.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_fieldWaits.TryGetValue(key, out var waiting))
            {
                waiting = [];
                _fieldWaits[key] = waiting;
            }
            waiting.Add(entry);
            full = waiting.Count >= table.TeamSize;
        }

        Console.WriteLine($"battlefield_join=match={matchId} team={team} character={charId}"
            + $" class={realClass.Id} level={profile.Level} enhance_total={profile.EnhanceTotal}"
            + $" enhance_slots={profile.EnhanceSlots} power={rawPower} filled={full}");

        if (full)
        {
            StartBattlefieldTeam(matchId, team);
        }
        else
        {
            // 没凑齐就等时限：人陆续到齐时由最后到的那个人开场，这里只管超时那一支。
            _ = Task.Delay(table.FillTimeoutMs)
                .ContinueWith(_ => StartBattlefieldTeam(matchId, team), TaskScheduler.Default);
        }

        var finished = await Task.WhenAny(entry.Ready.Task, Task.Delay(table.FillTimeoutMs + 5000));
        if (finished != entry.Ready.Task || !entry.Ready.Task.Result)
        {
            Console.WriteLine($"join_reject=battlefield_not_started match={matchId} character={charId}");
            await CloseAsync(socket,
                entry.RejectReason.Length > 0 ? entry.RejectReason : "battlefield_not_started");
            send.Dispose();
            return;
        }

        var party = _parties["field-" + matchId];
        var member = entry.Member!;
        var joined = new ServerMessage { MemberUpdated = new() { Member = Snapshot(member, party) } };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            await SendAsync(socket, send, new ServerMessage { Opened = party.Encounter.Open(member) }, linked.Token);
            await BroadcastAsync(party, joined, party.Ticker.Token, exceptSocket: socket);

            await ReceiveLoopAsync(socket, send, party, member, linked.Token);
            await linked.CancelAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"conn_error=session={party.Encounter.SessionId} {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            Detach(party, member);
            Console.WriteLine($"session_close={party.Encounter.SessionId}");
        }
    }

    /// <summary>
    /// 一队开场：算出同队最高战力，把每个人抬到「最高 / ratio」这条线（最高的人不动），
    /// 属性同乘系数后一起 Join。两支队伍共用同一个 Encounter，先到齐的那支先开场。
    /// </summary>
    void StartBattlefieldTeam(string matchId, int team)
    {
        List<BattlefieldEntry> entries;
        lock (_fieldGate)
        {
            var key = matchId + "|" + team.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_fieldWaits.TryGetValue(key, out var waiting) || waiting.Count == 0)
            {
                return;
            }
            entries = [.. waiting];
            _fieldWaits.Remove(key);
        }

        var teamMaxPower = entries.Max(e => e.RawPower);
        foreach (var entry in entries)
        {
            entry.AdjustedPower = config.Battlefield.AdjustedPower(entry.RawPower, teamMaxPower);
            entry.K = entry.RawPower <= 0 ? 1.0 : (double)entry.AdjustedPower / entry.RawPower;
        }

        Party party;
        var created = false;
        lock (_gate)
        {
            var partyKey = "field-" + matchId;
            if (!_parties.TryGetValue(partyKey, out party!))
            {
                party = new Party
                {
                    Key = partyKey,
                    Encounter = new Encounter(
                        config, settlements, characters, NextSessionId(), config.Battlefield.Players,
                        expSettlement, talismans, pills, scrolls, profiles, null),
                    Ticker = new CancellationTokenSource(),
                    Dungeon = null,
                    PartySize = config.Battlefield.Players,
                    ArenaMode = null,
                };
                _parties[partyKey] = party;
                created = true;
            }

            foreach (var entry in entries)
            {
                if (party.Connections.Count >= config.Battlefield.Players)
                {
                    entry.Rejected = true;
                    entry.RejectReason = "battlefield_full";
                    continue;
                }

                try
                {
                    entry.Member = party.Encounter.Join(entry.CharId, entry.Class, null, ScaledStats(entry));
                }
                catch (InvalidOperationException ex)
                {
                    entry.Rejected = true;
                    entry.RejectReason = ex.Message;
                    continue;
                }

                party.Connections.Add(new PartyConn
                {
                    Socket = entry.Socket,
                    Send = entry.Send,
                    Member = entry.Member,
                });
            }
        }

        if (created)
        {
            _ = TickLoopAsync(party);
        }

        foreach (var entry in entries)
        {
            if (!entry.Rejected)
            {
                // 这行是核对证据：同队所有人的 adjusted_power 之比不超过 ratio。
                Console.WriteLine(
                    $"battlefield_open=match={matchId} team={team} character={entry.CharId}"
                    + $" power={entry.RawPower} adjusted_power={entry.AdjustedPower}"
                    + $" k={entry.K:0.###} team_max_power={teamMaxPower}"
                    + $" ratio={config.Battlefield.Ratio}");
            }
            entry.Ready.TrySetResult(!entry.Rejected);
        }
    }

    /// <summary>
    /// 属性同乘一个系数。战力是属性的线性组合，所以乘完之后战力也正好乘这个系数。
    /// K 是竞技场 / 战场的平衡压缩；TechFactor 只在城战开场赋值，其他路径恒 1。
    /// </summary>
    static CombatStats ScaledStats(BattlefieldEntry entry)
    {
        var factor = entry.K * entry.TechFactor;
        return new CombatStats(
            (long)Math.Round(entry.RawAttack * factor),
            (long)Math.Round(entry.RawHp * factor),
            (long)Math.Round(entry.RawMana * factor),
            (long)Math.Round(entry.RawHeal * factor),
            (long)Math.Round(entry.RawThreat * factor));
    }

    /// <summary>
    /// 城战的一次进场（P3-3a1）。属性以角色库为准，按「职业表 + 强化总级 × 每级折算」
    /// 算出全额值，不做同队压缩（k 恒为 1）、不套竞技场模板。同一 match 的同一队先登记，
    /// 凑满 team_size 或到时限一起开场；两支队伍共用同一个 Encounter，技能仍由本进程结算。
    /// 这里不读世界服、也不发任何结算：本卡只做进场，胜负写回留给 P3-3a4。
    /// </summary>
    async Task HandleSiegeAsync(
        HttpContext context, string matchId, int team, ulong charId, ClassDef cls)
    {
        var profile = profiles.Get(charId);
        if (profile is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            Console.WriteLine($"join_reject=battle_profile_missing match={matchId} character={charId} mode=siege");
            return;
        }

        // 职业以角色库为准：query 里的 class 只是老客户端的兼容参数，改不了城战属性。
        if (!config.Classes.TryGet(profile.ClassId, out var realClass))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            Console.WriteLine("join_reject=battle_profile_bad_class match=" + matchId
                + " character=" + charId + " class=" + profile.ClassId + " mode=siege");
            return;
        }

        var table = config.Siege;
        var level = profile.EnhanceTotal;
        var rawAttack = (long)Math.Round(realClass.Attack()) + level * table.AttackPerLevel;
        var rawHp = realClass.HpMax(config.Classes.HpPerVitality) + level * table.HpPerLevel;
        var rawMana = realClass.ManaMax() + level * table.ManaPerLevel;
        var rawHeal = (long)Math.Round(realClass.Attributes.Spirit * realClass.HealPerSpirit)
                      + level * table.HealPerLevel;
        var rawThreat = realClass.ThreatStability();
        var rawPower = table.Power(rawAttack, rawHp, rawMana, rawHeal);

        // P3-3a3：只在城战开场读社交服的宗门科技。练习场 / 副本 / 竞技场 / 战场
        // 都不走这条分支，读不到也没有这份加成；上游不可用降级为 0，不挡进场。
        // 科技等级是社交服给的事实值，折算系数只从 config/sect_tech.json 取。
        var techLevel = config.SectTech.IsMissing
            ? 0
            : Math.Clamp(sectTech.Get(charId), 0, config.SectTech.MaxLevel);
        var techFactor = config.SectTech.Factor(techLevel);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var send = new SemaphoreSlim(1, 1);
        var entry = new BattlefieldEntry
        {
            Socket = socket,
            Send = send,
            CharId = charId,
            Class = realClass,
            RawAttack = rawAttack,
            RawHp = rawHp,
            RawMana = rawMana,
            RawHeal = rawHeal,
            RawThreat = rawThreat,
            RawPower = rawPower,
            // 装备全额生效：登记时的属性就是进场属性，K 从 1 开始不再改。
            AdjustedPower = rawPower,
            K = 1.0,
            SectTechLevel = techLevel,
            TechFactor = techFactor,
        };

        bool full;
        lock (_siegeGate)
        {
            var key = matchId + "|" + team.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_siegeWaits.TryGetValue(key, out var waiting))
            {
                waiting = [];
                _siegeWaits[key] = waiting;
            }
            waiting.Add(entry);
            full = waiting.Count >= table.TeamSize;
        }

        Console.WriteLine($"siege_join=match={matchId} team={team} character={charId}"
            + $" class={realClass.Id} level={profile.Level} enhance_total={profile.EnhanceTotal}"
            + $" enhance_slots={profile.EnhanceSlots} power={rawPower} balance=none filled={full}"
            + $" sect_tech_level={techLevel}"
            + $" sect_tech_bonus={config.SectTech.Bonus(rawPower, techLevel)}");

        if (full)
        {
            StartSiegeTeam(matchId, team);
        }
        else
        {
            // 没凑齐就等时限：人陆续到齐时由最后到的那个人开场，这里只管超时那一支。
            _ = Task.Delay(table.FillTimeoutMs)
                .ContinueWith(_ => StartSiegeTeam(matchId, team), TaskScheduler.Default);
        }

        var finished = await Task.WhenAny(entry.Ready.Task, Task.Delay(table.FillTimeoutMs + 5000));
        if (finished != entry.Ready.Task || !entry.Ready.Task.Result)
        {
            Console.WriteLine($"join_reject=siege_not_started match={matchId} character={charId}");
            await CloseAsync(socket,
                entry.RejectReason.Length > 0 ? entry.RejectReason : "siege_not_started");
            send.Dispose();
            return;
        }

        var party = _parties["siege-" + matchId];
        var member = entry.Member!;
        var joined = new ServerMessage { MemberUpdated = new() { Member = Snapshot(member, party) } };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            await SendAsync(socket, send, new ServerMessage { Opened = party.Encounter.Open(member) }, linked.Token);
            lock (_gate)
            {
                foreach (var conn in party.Connections)
                {
                    if (conn.Member == member)
                    {
                        conn.ViewReady = true;
                        break;
                    }
                }
            }
            // P3-3a2：本场名单由战斗服重新确认，给每条已开场的连接各发一份按自己算的
            // 同屏档位。先发开场包再刷新，客户端第一条永远是 BattleOpened。
            await RefreshSiegeViewsAsync(party, linked.Token);
            await BroadcastAsync(party, joined, party.Ticker.Token, exceptSocket: socket);

            await ReceiveLoopAsync(socket, send, party, member, linked.Token);
            await linked.CancelAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"conn_error=session={party.Encounter.SessionId} {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            Detach(party, member);
            Console.WriteLine($"session_close={party.Encounter.SessionId}");
        }
    }

    /// <summary>
    /// 一队开场：装备全额生效，不算同队最高战力、不调 AdjustedPower，K 一律 1。
    /// 两支队伍共用同一个 Encounter，先到齐（或先超时）的那支先开场。
    /// </summary>
    void StartSiegeTeam(string matchId, int team)
    {
        List<BattlefieldEntry> entries;
        lock (_siegeGate)
        {
            var key = matchId + "|" + team.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_siegeWaits.TryGetValue(key, out var waiting) || waiting.Count == 0)
            {
                return;
            }
            entries = [.. waiting];
            _siegeWaits.Remove(key);
        }

        var table = config.Siege;
        foreach (var entry in entries)
        {
            // 全额：power 与属性都不做第二次调整。
            entry.AdjustedPower = entry.RawPower;
            entry.K = 1.0;
        }

        Party party;
        var created = false;
        lock (_gate)
        {
            var partyKey = "siege-" + matchId;
            if (!_parties.TryGetValue(partyKey, out party!))
            {
                party = new Party
                {
                    Key = partyKey,
                    Encounter = new Encounter(
                        config, settlements, characters, NextSessionId(), table.Players,
                        expSettlement, talismans, pills, scrolls, profiles, null),
                    Ticker = new CancellationTokenSource(),
                    Dungeon = null,
                    PartySize = table.Players,
                    ArenaMode = null,
                };
                _parties[partyKey] = party;
                created = true;
            }

            foreach (var entry in entries)
            {
                if (party.Connections.Count >= table.Players)
                {
                    entry.Rejected = true;
                    entry.RejectReason = "siege_full";
                    Console.WriteLine($"join_reject=siege_full match={matchId} team={team}"
                        + $" character={entry.CharId} players={table.Players}");
                    continue;
                }

                try
                {
                    entry.Member = party.Encounter.Join(entry.CharId, entry.Class, null, ScaledStats(entry));
                }
                catch (InvalidOperationException ex)
                {
                    entry.Rejected = true;
                    entry.RejectReason = ex.Message;
                    continue;
                }

                party.Connections.Add(new PartyConn
                {
                    Socket = entry.Socket,
                    Send = entry.Send,
                    Member = entry.Member,
                });
            }
        }

        // 队伍编号只是表现事实值：同屏超限单位按它显示队伍颜色。
        // 不进 Encounter、不进技能结算，也不写角色库。
        lock (_siegeGate)
        {
            foreach (var entry in entries)
            {
                if (entry.Member is not null)
                {
                    _siegeTeams[entry.Member.Id] = (uint)team;
                    // P3-3a4：本场名单留一份不随离场清掉的，结束判胜方宗门要用。
                    party.SiegeRoster[entry.Member.Id] = (uint)team;
                }
            }
        }

        if (created)
        {
            _ = TickLoopAsync(party);
            // P3-3a4：领地表是可选表。缺失时（老卡的临时配置目录没有这张表）根本
            // 不排结束定时器，城战照常打、不写回；配了才到时限按本场事实值判胜方。
            var city = config.SiegeTerritory.CityOf(matchId);
            if (city is not null)
            {
                _ = FinishSiegeAfterAsync(party, matchId, city);
            }
        }

        foreach (var entry in entries)
        {
            if (!entry.Rejected)
            {
                // 这行是核对证据：k=1 / balance=none 且两队共用同一个 session。
                Console.WriteLine(
                    $"siege_open=match={matchId} team={team} character={entry.CharId}"
                    + $" power={entry.RawPower} effective_power={entry.AdjustedPower}"
                    + $" k={entry.K:0.###} balance=none"
                    + $" team_size={table.TeamSize} teams={table.Teams} players={table.Players}"
                    + $" session={party.Encounter.SessionId}"
                    + $" sect_tech_level={entry.SectTechLevel}"
                    + $" sect_tech_bonus={config.SectTech.Bonus(entry.RawPower, entry.SectTechLevel)}");
            }
            entry.Ready.TrySetResult(!entry.Rejected);
        }
    }

    /// <summary>
    /// P3-3a4：城战开场到配置时限就判一次胜负，并把结果交给角色服写回城池。
    /// 胜方只按「本场各队对 Boss 的累计有效伤害」这一个服务端事实值判定，
    /// 客户端表达不了谁赢、归属改成谁、抽成多少；平局 / 无人出手 / 胜方没宗门
    /// 都不写回。写回只做一次，同一场重复触发也不会改第二次库。
    /// 这里不改 Encounter、不写结算 / 经验 / 发奖、不关连接：技术架构 3 / 6 节
    /// 把持久数据的唯一写入放在角色服，战斗服只给事实值。
    /// </summary>
    async Task FinishSiegeAfterAsync(
        Party party, string matchId, SiegeTerritoryTable.CityRule city)
    {
        await Task.Delay(city.BattleTimeoutMs);

        lock (_gate)
        {
            if (!_parties.TryGetValue(party.Key, out var current)
                || !ReferenceEquals(current, party)
                || party.SiegeFinished)
            {
                return;
            }
            party.SiegeFinished = true;
        }

        long damageA, damageB;
        KeyValuePair<ulong, uint>[] roster;
        lock (_gate)
        {
            damageA = party.SiegeDamage[0];
            damageB = party.SiegeDamage[1];
        }
        lock (_siegeGate)
        {
            // 用本场登记过的名单，不是当前在线名单：打完就断线的人也算这一队。
            roster = party.SiegeRoster.OrderBy(kv => kv.Key).ToArray();
        }

        // 2 表示没有胜方：两边一样、或者这一场谁都没打中。
        uint winnerTeam = damageA > damageB ? 0u : damageB > damageA ? 1u : 2u;
        ulong winnerSect = 0;
        if (winnerTeam <= 1)
        {
            var teamMembers = roster
                .Where(kv => kv.Value == winnerTeam)
                .Select(kv => kv.Key)
                .ToArray();

            // 按成员编号稳定排序取第一个查得到宗门的人；方案没写定同队多宗门怎么
            // 取代表，这里不编 weighting，只按事实值里稳定可核对的那一个。
            foreach (var memberId in teamMembers)
            {
                var sect = sectFacts.SectOf(memberId);
                if (sect is > 0)
                {
                    winnerSect = sect.Value;
                    break;
                }
            }
        }

        var txnId = "siege:" + matchId + ":" + party.Encounter.SessionId;
        var reason = winnerTeam > 1 ? "no_winner" : winnerSect == 0 ? "no_sect" : "";
        var reply = new SiegeResultReply(false, reason, false, city.CityId, "", 0);
        if (reason.Length == 0)
        {
            reply = siegeResult.Apply(new SiegeResultFact(
                txnId, city.CityId, matchId, winnerTeam, winnerSect));
            reason = reply.Ok ? "" : reply.Reason;
        }

        Console.WriteLine($"siege_result=match={matchId} city={city.CityId}"
            + $" session={party.Encounter.SessionId}"
            + $" damage_a={damageA} damage_b={damageB} winner_team={winnerTeam}"
            + $" winner_sect={winnerSect} txn={txnId}"
            + $" ok={(reply.Ok ? "true" : "false")} duplicate={(reply.Duplicate ? "true" : "false")}"
            + $" owner={reply.OwnerSect} tax_pct={reply.TaxPct} reason={reason}");
    }

    /// <summary>
    /// P3-5a：世界 Boss 的一次进场。几件事都是服务端事实值：
    ///   1. 时刻：只读自己的时钟，没到点 / 过点就 worldboss_closed，客户端带不进时间；
    ///   2. 场次：钥匙 = entrance-日期-窗口编号，同一时刻所有人算出同一把，所以「同一时刻一场」；
    ///   3. 人数：上限只从 config/world_boss.json 读，第 cap+1 个人回 worldboss_full；
    ///   4. 打掉过就关：这一进程记着已经打掉的场次，再来的连接回 worldboss_fought。
    /// 属性按职业表算（与副本同一层）：本卡不引入装备折算层，也不做同队平衡压缩。
    /// 世界服不结算这场技能，技能、伤害、仇恨、硬直全在本进程算。
    /// </summary>
    async Task HandleWorldBossAsync(
        HttpContext context, string entranceId, string? charRaw, ClassDef cls)
    {
        var table = config.WorldBoss;
        if (table.IsMissing)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            Console.WriteLine("join_reject=worldboss_table_missing entrance=" + entranceId);
            return;
        }

        if (!string.Equals(entranceId, table.EntranceId, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            Console.WriteLine("join_reject=worldboss_unknown_entrance entrance=" + entranceId
                + " configured=" + table.EntranceId);
            return;
        }

        // 归属与经验都按角色编号记，拿 caster 兜底会记到别人头上，所以必须给编号。
        if (!ulong.TryParse(charRaw, out var charId) || charId == 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            Console.WriteLine("join_reject=worldboss_character_missing entrance=" + entranceId);
            return;
        }

        var window = table.WindowOf(DateTimeOffset.UtcNow, config.Calendar);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var send = new SemaphoreSlim(1, 1);

        // 时间未到（或这一窗已经过点）：入口进不去。理由走 EnterRejected 回给客户端，
        // 未生效的数值一律 0，不留一份「看起来进来了」的回包。
        if (!window.Open)
        {
            await RejectEnterAsync(socket, send,
                new EnterRejected
                {
                    Reason = "worldboss_closed",
                    Limit = (uint)table.Cap,
                    Current = 0,
                },
                context.RequestAborted);
            return;
        }

        // Q11：名额先向世界服要。抢名额发生在进这一场之前，所以不会出现
        // 「一百个人同时连进来再判满」。世界服不可达时按本进程人数判满（不挡人），
        // 也不假装占到了名额：这一场的名额账只在世界服那一份。
        var slot = worldBossSink.Hold(charId);
        if (!slot.Ok && !slot.UpstreamDown)
        {
            await RejectEnterAsync(socket, send,
                new EnterRejected
                {
                    Reason = slot.Reason,
                    Limit = (uint)(slot.Cap > 0 ? slot.Cap : table.Cap),
                    Current = (uint)Math.Max(0, slot.Current),
                },
                context.RequestAborted);
            return;
        }

        if (slot.Ok)
        {
            Console.WriteLine($"worldboss_slot=op=hold character={charId} window={slot.WindowKey}"
                + $" current={slot.Current} cap={slot.Cap} line={slot.LineKey}"
                + $" line_occupancy={slot.LineOccupancy} line_capacity={slot.LineCapacity}"
                + $" expires_at_ms={slot.ExpiresAtMs}");
        }
        else
        {
            Console.WriteLine($"worldboss_slot=op=hold character={charId} degraded={slot.Reason}"
                + " fallback=party_connections");
        }

        var partyKey = "wb-" + window.Key;
        var created = false;
        Party party = null!;
        Member? member = null;
        PartyConn? myConn = null;
        EnterRejected? rejected = null;
        var current = 0;

        lock (_gate)
        {
            if (_worldBossFought.Contains(window.Key))
            {
                rejected = new EnterRejected
                {
                    Reason = "worldboss_fought",
                    Limit = (uint)table.Cap,
                    Current = 0,
                };
            }
            else
            {
                if (!_parties.TryGetValue(partyKey, out party!))
                {
                    party = new Party
                    {
                        Key = partyKey,
                        Encounter = new Encounter(
                            config, settlements, characters, NextSessionId(), 1,
                            expSettlement, talismans, pills, scrolls, profiles, null, table.Boss),
                        Ticker = new CancellationTokenSource(),
                        Dungeon = null,
                        PartySize = 1,
                        ArenaMode = null,
                        WorldBossWindow = window.Key,
                    };
                    // 这一场的经验来源单列 world_boss；人数随进场变化，结算时按本场实际人数
                    // 算组队与同门那一截（副本 / 战场 / 城战的结算人数一个字都没变）。
                    party.Encounter.SettlementSource = "world_boss";
                    party.Encounter.SetSettlementPartySize(1);
                    _parties[partyKey] = party;
                    created = true;
                }

                current = party.Connections.Count;
                if (current >= table.Cap)
                {
                    // 首发上限 20，第 21 个人被拒绝：上限只从配置读，代码里没有第二个数字。
                    rejected = new EnterRejected
                    {
                        Reason = "worldboss_full",
                        Limit = (uint)table.Cap,
                        Current = (uint)current,
                    };
                }
                else
                {
                    try
                    {
                        member = party.Encounter.Join(charId, cls);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // 编号撞了：同一场里已经有人用这个角色编号。
                        Console.WriteLine($"join_reject=worldboss character={charId} reason={ex.Message}");
                        _parties.TryRemove(partyKey, out _);
                        socket.Abort();
                        send.Dispose();
                        return;
                    }

                    var conn = new PartyConn
                    {
                        Socket = socket,
                        Send = send,
                        Member = member,
                        // tick 已经在跑：开场包发出去之前不给这条连接推广播。
                        Ready = false,
                    };
                    party.Connections.Add(conn);
                    myConn = conn;
                    current = party.Connections.Count;
                    party.Encounter.SetSettlementPartySize(current);
                }
            }
        }

        if (rejected is not null)
        {
            if (created)
            {
                _parties.TryRemove(partyKey, out _);
            }

            if (slot.Ok)
            {
                // 名额拿到了但这一场没进成（本地兜底判满 / 编号撞车）：立刻还回去，
                // 不让一个没进场的人白占着位置等 hold_s 到期。
                worldBossSink.Release(charId, "rejected");
            }

            await RejectEnterAsync(socket, send, rejected, context.RequestAborted);
            return;
        }

        // 宗门是社交服的只读事实值：进场查一次，查不到就按「没有宗门」，不挡进场。
        // 它只用来在结束时挑「伤害最高的宗门」记名字，不进属性、不进伤害、不写库。
        var sectId = sectFacts.SectOf(charId) ?? 0;
        lock (_gate)
        {
            party.WorldBossSects[member!.Id] = sectId;
            if (!party.WorldBossDamage.ContainsKey(sectId))
            {
                party.WorldBossDamage[sectId] = 0;
            }
        }

        if (slot.Ok)
        {
            // 第一次续约带 entered=true：世界服据此把到期时刻从 hold_s 换成 renew_s。
            var confirmed = worldBossSink.Renew(charId, entered: true);
            Console.WriteLine($"worldboss_slot=op=confirm character={charId} ok={(confirmed.Ok ? "true" : "false")}"
                + $" reason={(confirmed.Reason.Length == 0 ? "none" : confirmed.Reason)}"
                + $" current={confirmed.Current} cap={confirmed.Cap}");
        }

        if (created)
        {
            _ = TickLoopAsync(party);
            _ = WorldBossRenewLoopAsync(party);
            Console.WriteLine($"worldboss_open=entrance={table.EntranceId} window={window.Key}"
                + $" session={party.Encounter.SessionId} boss={table.Boss.Id}"
                + $" boss_hp_max={party.Encounter.BossHpMax} cap={table.Cap}"
                + $" stats=class_table balance=none starts_at_ms={window.StartsAtMs}"
                + $" ends_at_ms={window.EndsAtMs}");
        }

        Console.WriteLine($"worldboss_join=entrance={table.EntranceId} window={window.Key}"
            + $" character={charId} class={cls.Id} party={partyKey}"
            + $" session={party.Encounter.SessionId} cap={table.Cap} current={current}"
            + $" sect={sectId} boss={table.Boss.Id} stats=class_table balance=none");

        var joined = new ServerMessage { MemberUpdated = new() { Member = Snapshot(member!, party) } };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            await SendAsync(socket, send, new ServerMessage { Opened = party.Encounter.Open(member!) }, linked.Token);
            // 开场包已经在路上，这条连接从这一行起可以收广播了。
            lock (_gate)
            {
                if (myConn is not null)
                {
                    myConn.Ready = true;
                }
            }

            await BroadcastAsync(party, joined, party.Ticker.Token, exceptSocket: socket);
            await ReceiveLoopAsync(socket, send, party, member!, linked.Token);
            await linked.CancelAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"conn_error=session={party.Encounter.SessionId} {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            Detach(party, member!);
            // Q11：人走了就把名额还回去；世界服还记着这一场谁占着位置。
            if (slot.Ok)
            {
                var released = worldBossSink.Release(charId, "leave");
                Console.WriteLine($"worldboss_slot=op=leave character={charId} ok={(released.Ok ? "true" : "false")}"
                    + $" reason={(released.Reason.Length == 0 ? "none" : released.Reason)}"
                    + $" current={released.Current}");
            }

            Console.WriteLine($"session_close={party.Encounter.SessionId}");
        }
    }

    /// <summary>
    /// Q11：这一场的续约节拍。每 renew_s 把在场每个人的名额续一次；续不上的只记日志，
    /// 不踢人——名额丢了不等于人不在场（世界服重启过、上游抖过），这一场里谁还在
    /// 由战斗服自己按 Q9 的宽限管。节拍只跑在这一场，散场随 party.Ticker 一起停。
    /// </summary>
    async Task WorldBossRenewLoopAsync(Party party)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, config.WorldBoss.Hold.RenewSeconds));
        try
        {
            while (!party.Ticker.IsCancellationRequested)
            {
                await Task.Delay(period, party.Ticker.Token);
                List<ulong> ids;
                lock (_gate)
                {
                    ids = party.Connections.Select(c => c.Member.Id).ToList();
                }

                foreach (var id in ids)
                {
                    var reply = worldBossSink.Renew(id, entered: true);
                    if (!reply.Ok && !reply.UpstreamDown)
                    {
                        Console.WriteLine($"worldboss_renew=character={id} ok=false reason={reply.Reason}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// P3-5a：这一场结束时把事实值交回入口——打掉没打掉、本场伤害最高的宗门叫什么。
    /// 只记名字：不加属性、不改税收、不把灵石矿脉转走，也不写任何库表。
    /// 平手取编号小的那个（稳定可核对），没人打出伤害就一个名字都不记。
    /// 同一场只交一次：打完就断线的人不会让这场结两次。
    /// </summary>
    void FinishWorldBoss(Party party, bool killed)
    {
        string windowKey;
        ulong winnerSect = 0;
        long winnerDamage = 0;
        long damageTotal = 0;
        lock (_gate)
        {
            if (party.WorldBossFinished)
            {
                return;
            }

            party.WorldBossFinished = true;
            windowKey = party.WorldBossWindow;
            foreach (var item in party.WorldBossDamage.OrderBy(kv => kv.Key))
            {
                damageTotal += item.Value;
                // 键 0 是「没有宗门的人」：能进、能拿个人奖励，但不会被记成归属。
                if (item.Key == 0 || item.Value <= winnerDamage)
                {
                    continue;
                }

                winnerSect = item.Key;
                winnerDamage = item.Value;
            }

            if (killed)
            {
                // 被击杀，入口就关（方案 10.4）：这一进程里这一场不再接受新的进场。
                _worldBossFought.Add(windowKey);
            }
        }

        var sectName = winnerSect == 0 ? "" : sectFacts.NameOf(winnerSect);
        var fact = new WorldBossOutcomeFact(
            "worldboss:" + windowKey, config.WorldBoss.EntranceId, windowKey,
            killed, winnerSect, sectName, damageTotal);
        var reply = worldBossSink.Apply(fact);

        Console.WriteLine($"worldboss_result=window={windowKey} entrance={fact.EntranceId}"
            + $" session={party.Encounter.SessionId} killed={killed}"
            + $" damage_total={damageTotal} winner_sect={winnerSect}"
            + $" winner_name={(sectName.Length == 0 ? "none" : sectName)}"
            + $" txn={fact.TxnId} ok={reply.Ok} duplicate={reply.Duplicate}"
            + $" owner={reply.OwnerSectId} entrance_closed={reply.EntranceClosed}"
            + $" reason={(reply.Reason.Length == 0 ? "none" : reply.Reason)}");
    }

    /// <summary>进场被拒：先把原因发出去再正常关。客户端据此显示「还没到点 / 满了 / 打过了」。</summary>
    static async Task RejectEnterAsync(
        WebSocket socket, SemaphoreSlim send, EnterRejected rejected, CancellationToken token)
    {
        Console.WriteLine($"join_reject=worldboss reason={rejected.Reason}"
            + $" current={rejected.Current} limit={rejected.Limit}");
        await SendAsync(socket, send, new ServerMessage { EnterRejected = rejected }, token);
        await CloseAsync(socket, "enter_rejected");
    }

    /// <summary>线程安全地取一条队友快照，给进场广播用。</summary>
    static Mmo.Proto.Battle.PartyMember Snapshot(Member member, Party party)
    {
        // MemberUpdated 只带一个人，直接按当前值组。
        return new Mmo.Proto.Battle.PartyMember
        {
            MemberId = member.Id,
            ClassId = member.Class.Id,
            Name = member.Class.Name,
            Hp = member.Hp,
            HpMax = member.HpMax,
            Down = member.Down,
        };
    }

    /// <summary>
    /// P3-3a2：城战名单变了，战斗服重新算一遍同屏档位，各发各的那一份。
    /// 这是这份档位唯一的下发点：档位由服务端按 config/siege.json 的上限确认，
    /// 客户端表达不了「我想把谁画全」，也不会自己按距离挑人。
    /// </summary>
    async Task RefreshSiegeViewsAsync(Party party, CancellationToken token)
    {
        if (config.Siege.IsMissing)
        {
            return;
        }

        PartyConn[] conns;
        lock (_gate)
        {
            conns = [.. party.Connections.Where(c => c.ViewReady)];
        }
        if (conns.Length == 0)
        {
            return;
        }

        var units = new List<SiegeViewUnit>(conns.Length);
        lock (_siegeGate)
        {
            foreach (var conn in conns)
            {
                var m = conn.Member;
                _siegeTeams.TryGetValue(m.Id, out var team);
                units.Add(new SiegeViewUnit(m.Id, team, m.X, m.Y, m.Hp, m.HpMax));
            }
        }

        var limit = config.Siege.ViewFullModelLimit;
        foreach (var conn in conns)
        {
            var view = SiegeViewTable.Build(
                party.Encounter.SessionId, conn.Member.Id, limit, units);
            try
            {
                await SendAsync(conn.Socket, conn.Send,
                    new ServerMessage { SiegeView = view }, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
            }

            Console.WriteLine($"siege_view=session={party.Encounter.SessionId}"
                + $" viewer={conn.Member.Id} limit={limit} full={view.FullModelCount}"
                + $" units={view.UnitCount} markers={view.UnitCount - view.FullModelCount}");
        }
    }

    void Detach(Party party, Member member, bool voluntary = false)
    {
        Party? removed = null;
        OfflineWatch? watch = null;
        lock (_gate)
        {
            party.Connections.RemoveAll(c => c.Member == member);

            // Q9：主线（副本 / 练习场 / 竞技场）断线先留宽限，别的模式按老口径
            // 立即离场——战场 / 城战 / 世界 Boss 是服务端组队与排队制，一场不由
            // 一把钥匙决定，重连要重新排队，留一个「人在场却没人操作」的成员只会坏账。
            if (!Resumable(party) || party.Encounter.IsClosed)
            {
                party.Encounter.Leave(member);
                if (party.Connections.Count == 0)
                {
                    // 最后一个人走了：散场，tick 停掉。
                    _parties.TryRemove(party.Key, out _);
                    removed = party;
                }
            }
            else
            {
                watch = new OfflineWatch
                {
                    CharacterId = member.Id,
                    SessionId = party.Encounter.SessionId,
                    PartyKey = party.Key,
                    SinceMs = NowMs(),
                    Voluntary = voluntary,
                };
                _offline[member.Id] = watch;
            }
        }

        // 队伍编号只活在这场实例里，人走了就一起清掉，不留在进程内存。
        lock (_siegeGate)
        {
            _siegeTeams.Remove(member.Id);
        }

        if (watch is not null)
        {
            Console.WriteLine($"session_offline=session={watch.SessionId} character={watch.CharacterId}"
                + $" party={watch.PartyKey} voluntary={(voluntary ? "true" : "false")}"
                + $" grace_ms={config.Reconnect.GraceMs} window_ms={config.Reconnect.WindowMs}");
            _ = WatchOfflineAsync(party, member, watch);
            return;
        }

        if (removed is not null)
        {
            DissolveParty(removed);
        }
        else if (party.Key.StartsWith("siege-", StringComparison.Ordinal))
        {
            // 城战有人离场：名单少了，剩下的人再收一份服务端确认过的档位。
            _ = RefreshSiegeViewsAsync(party, party.Ticker.Token);
        }
    }

    /// <summary>
    /// Q9：这一场能不能被同一个人接回去。只有主线三个模式（副本 / 练习场 / 竞技场）
    /// 是「一把 party 钥匙 = 一场」，所以只有它们的队伍钥匙能当重连凭据。
    /// </summary>
    bool Resumable(Party party) =>
        ReconnectOn
        && !party.Key.StartsWith("field-", StringComparison.Ordinal)
        && !party.Key.StartsWith("siege-", StringComparison.Ordinal)
        && !party.Key.StartsWith("wb-", StringComparison.Ordinal);

    /// <summary>
    /// Q9：掉线的人在等自己回来。主动退当场判罚；网断先给宽限（位置与效果留在场内），
    /// 宽限一过就把他从这一场拿掉，窗口一到还没人回来才按逃跑记一次。
    /// </summary>
    async Task WatchOfflineAsync(Party party, Member member, OfflineWatch watch)
    {
        if (watch.Voluntary)
        {
            if (_offline.TryRemove(watch.CharacterId, out var current) && ReferenceEquals(current, watch))
            {
                DropFromParty(party, member);
                RecordDeserter(watch.CharacterId, watch.SessionId, "voluntary");
            }

            return;
        }

        var graceMs = config.Reconnect.GraceMs;
        var windowMs = config.Reconnect.WindowMs;

        await Task.Delay((int)Math.Min(graceMs, int.MaxValue));
        if (_offline.TryGetValue(watch.CharacterId, out var waiting) && ReferenceEquals(waiting, watch))
        {
            DropFromParty(party, member);
            Console.WriteLine($"reconnect_grace_expired=session={watch.SessionId}"
                + $" character={watch.CharacterId} grace_ms={graceMs}");
        }

        var left = windowMs - graceMs;
        if (left > 0)
        {
            await Task.Delay((int)Math.Min(left, int.MaxValue));
        }

        if (_offline.TryRemove(watch.CharacterId, out var expired) && ReferenceEquals(expired, watch))
        {
            RecordDeserter(watch.CharacterId, watch.SessionId, "timeout");
        }
    }

    /// <summary>宽限到点：把人从这一场拿掉；这一场再没有人了就地散场（不结算）。</summary>
    void DropFromParty(Party party, Member member)
    {
        Party? removed = null;
        lock (_gate)
        {
            party.Encounter.Leave(member);
            if (party.Connections.Count == 0 && party.Encounter.MemberCount == 0
                && _parties.TryRemove(party.Key, out _))
            {
                removed = party;
            }
        }

        if (removed is not null)
        {
            DissolveParty(removed);
        }
    }

    void DissolveParty(Party party)
    {
        party.Encounter.Abandon();
        party.Ticker.Cancel();

        // P3-5a：这一场散了（没打掉也算一场），把事实值交回入口。
        // 已经在打掉那一条报过的不会再报第二次（WorldBossFinished）。
        if (party.WorldBossWindow.Length > 0)
        {
            _ = Task.Run(() => FinishWorldBoss(party, false));
        }
    }

    /// <summary>
    /// Q9：宽限内回来，把原来那个人整份接回来。命中要求三件事同时成立：
    /// 同一把队伍钥匙还在、那一场里这个角色还在（没被宽限到点拿掉）、这条等待
    /// 记录还没被别人抢走。命中之后不再 Join——血量、蓝、位置、冷却与效果都还在。
    /// </summary>
    bool TryResume(ulong characterId, string partyKey, out Party party, out Member member)
    {
        party = null!;
        member = null!;

        if (!ReconnectOn || !_parties.TryGetValue(partyKey, out var found) || !Resumable(found))
        {
            return false;
        }

        if (!_offline.TryGetValue(characterId, out var watch)
            || watch.SessionId != found.Encounter.SessionId
            || !watch.TryClaim())
        {
            return false;
        }

        var existing = found.Encounter.FindMember(characterId);
        if (existing is null || !_offline.TryRemove(characterId, out _))
        {
            // 宽限到点已经被拿掉了：这次按重新入场处理（走正常 Join，满血出生点）。
            return false;
        }

        party = found;
        member = existing;
        return true;
    }

    /// <summary>
    /// Q9：续战的一条连接。开场包里的 everything（血量、位置、效果剩余毫秒）都是
    /// 现在这一刻的服务端事实值，客户端照抄就行。这里不广播 MemberUpdated：
    /// 队友那边从来没收到「他离场」，也就不需要一条「他回来了」。
    /// </summary>
    async Task ServeResumedAsync(
        WebSocket socket, SemaphoreSlim send, Party party, Member member, CancellationToken requestAborted)
    {
        Console.WriteLine($"reconnect_resumed=session={party.Encounter.SessionId}"
            + $" character={member.Id} hp={member.Hp} effects={member.Status.State(NowMs()).Effects.Count}");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var voluntary = false;
        try
        {
            await SendAsync(socket, send, new ServerMessage { Opened = party.Encounter.Open(member) }, linked.Token);
            voluntary = await ReceiveLoopAsync(socket, send, party, member, linked.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"conn_error=session={party.Encounter.SessionId} {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            Detach(party, member, voluntary);
            Console.WriteLine($"session_close={party.Encounter.SessionId}");
        }
    }

    /// <summary>
    /// Q9：进场前问一次还能不能进。内存兜底与角色库事实值取较大的那个：
    /// 角色服没起时至少本进程内还挡得住，角色服活着时惩罚跨进程重启也还在。
    /// </summary>
    async Task<long> DeserterRemainingMsAsync(ulong characterId)
    {
        var local = _penaltyUntilMs.TryGetValue(characterId, out var until) ? until - NowMs() : 0;
        var remote = await Task.Run(() => deserter.RemainingMs(characterId));
        return Math.Max(0, Math.Max(local, remote));
    }

    /// <summary>
    /// Q9：记一次逃跑。时长只从 config/reconnect.json 读；到期时刻由角色服按这个
    /// 时长现算，战斗服这边只留一份本机兜底（进程内有效）。
    /// </summary>
    void RecordDeserter(ulong characterId, ulong sessionId, string reason)
    {
        var penaltyMs = config.Reconnect.PenaltyMs;
        if (penaltyMs <= 0)
        {
            Console.WriteLine($"deserter_penalty=character={characterId} session={sessionId}"
                + $" reason={reason} penalty_ms=0 skipped=true");
            return;
        }

        var until = NowMs() + penaltyMs;
        _penaltyUntilMs.AddOrUpdate(
            characterId,
            until,
            (_, existing) => Math.Max(existing, until));

        var remaining = deserter.Record(characterId, sessionId, reason, penaltyMs);
        Console.WriteLine($"deserter_penalty=character={characterId} session={sessionId}"
            + $" reason={reason} penalty_ms={penaltyMs} remaining_ms={Math.Max(remaining, penaltyMs)}");
    }

    async Task TickLoopAsync(Party party)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(party.Ticker.Token))
            {
                var cleared = false;
                foreach (var message in party.Encounter.Tick())
                {
                    if (message.Ended is { Cleared: true })
                    {
                        cleared = true;
                    }
                    await BroadcastAsync(party, message, party.Ticker.Token);

                    // Q8：need_greed 的归属在 Ended 之后公布一次；personal 没有这条。
                    if (message.Ended is not null && party.Encounter.LootResult is { } loot)
                    {
                        await BroadcastAsync(
                            party,
                            new ServerMessage { LootAssigned = ToLootAssigned(loot) },
                            party.Ticker.Token);
                    }
                }

                // P3-5a：这一场打掉了，把事实值交回入口（只记名字，不写库）。
                // 先让回包出去，再报；同一场只报一次由 party.WorldBossFinished 挡。
                if (cleared && party.WorldBossWindow.Length > 0)
                {
                    _ = Task.Run(() => FinishWorldBoss(party, true));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // tick 挂了这场就停在半路，必须打出来，不能当没看见。
            Console.WriteLine($"tick_error=session={party.Encounter.SessionId} {ex.GetType().Name} {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 收一条连接上的消息，直到它断。返回值是 Q9 要的那一个位：true 表示客户端
    /// 主动发了 Close 帧（自己退的），false 表示网断或服务端取消——两者的宽限与
    /// 惩罚不一样，主动退立即算逃跑。
    /// </summary>
    async Task<bool> ReceiveLoopAsync(
        WebSocket socket, SemaphoreSlim send, Party party, Member member, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var assembled = new MemoryStream();

        while (!token.IsCancellationRequested)
        {
            assembled.SetLength(0);

            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, token);
                    return true;
                }

                if (received.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }

                assembled.Write(buffer, 0, received.Count);
                if (assembled.Length > MaxMessageBytes)
                {
                    // 不像正常请求，直接掐掉这条连接。
                    socket.Abort();
                    return false;
                }
            }
            while (!received.EndOfMessage);

            if (assembled.Length == 0)
            {
                continue;
            }

            ClientMessage inbound;
            try
            {
                inbound = ClientMessage.Parser.ParseFrom(assembled.ToArray());
            }
            catch (InvalidProtocolBufferException)
            {
                // 解析不了就不理，不当成一次施法。
                continue;
            }

            switch (inbound.BodyCase)
            {
                case ClientMessage.BodyOneofCase.Skill:
                    var rejected = party.Encounter.TrySubmit(member, inbound.Skill);
                    if (rejected is not null)
                    {
                        // 拒绝只回给这个人：别的前摇不用跟着停。
                        await SendAsync(socket, send, new ServerMessage { Rejected = rejected }, token);
                    }
                    break;

                case ClientMessage.BodyOneofCase.Cancel:
                    await SendAsync(
                        socket,
                        send,
                        new ServerMessage { Cancelled = party.Encounter.Cancel(member, inbound.Cancel) },
                        token);
                    break;

                case ClientMessage.BodyOneofCase.Move:
                    // 每次走位都回一条夹过的位置，客户端拿它对一下自己摆得对不对。
                    await SendAsync(
                        socket,
                        send,
                        new ServerMessage { Moved = party.Encounter.Move(member, inbound.Move) },
                        token);
                    break;

                case ClientMessage.BodyOneofCase.Talisman:
                    // 用符只影响自己：结果或拒绝都只回给这个人，不广播。
                    await SendAsync(
                        socket,
                        send,
                        party.Encounter.UseTalisman(member, inbound.Talisman),
                        token);
                    break;

                case ClientMessage.BodyOneofCase.Pill:
                    // B1：吃丹只影响自己：结果或拒绝都只回给这个人，不广播。
                    await SendAsync(
                        socket,
                        send,
                        party.Encounter.UsePill(member, inbound.Pill),
                        token);
                    break;

                case ClientMessage.BodyOneofCase.Scroll:
                    // 卷轴只拉起自己：结果或拒绝都只回给这个人，不广播。
                    await SendAsync(
                        socket,
                        send,
                        party.Encounter.UseReviveScroll(member, inbound.Scroll),
                        token);
                    break;

                case ClientMessage.BodyOneofCase.LootIntent:
                    // Q8：只记表态，不回包、不广播。掷点与归属都在结算时由服务端算，
                    // 客户端这条消息里没有任何数值能影响结果。
                    party.Encounter.SetLootIntent(
                        member.Id, inbound.LootIntent.ItemId, inbound.LootIntent.Decision);
                    break;
            }
        }

        return false;
    }

    /// <summary>结算结果、队友状态、阶段和收尾发全场：技术架构写的是「把结果发给本里的人」。</summary>
    Task BroadcastAsync(Party party, ServerMessage message, CancellationToken token) =>
        BroadcastAsync(party, message, token, exceptSocket: null);

    /// <summary>
    /// Q8：把服务端算好的归属转成一条回包。Winner=0 的行是「全员放弃」，客户端
    /// 只显示「无人需求」，不能据此认为自己可以捡——那一行没有发给任何人。
    /// </summary>
    static LootAssigned ToLootAssigned(LootAllocation allocation)
    {
        var message = new LootAssigned { Mode = allocation.Mode };
        foreach (var line in allocation.Lines)
        {
            message.Lines.Add(new LootAssignment
            {
                ItemId = line.ItemId,
                Count = line.Count,
                WinnerCharacterId = line.Winner,
            });
        }

        return message;
    }

    async Task BroadcastAsync(Party party, ServerMessage message, CancellationToken token, WebSocket? exceptSocket)
    {
        // P3-3a4：城战胜负只按服务端结算出来的有效伤害累计，按队伍归到这一场的
        // 两个桶里。不改 Encounter 的伤害公式、不进结算 / 经验 / 发奖；领地表缺失
        // （老卡临时配置）时连这一步都不做，老链路一条断言都不受影响。
        if (!config.SiegeTerritory.IsMissing
            && party.Key.StartsWith("siege-", StringComparison.Ordinal)
            && message.Result is { Hit: true, Damage: > 0 } result)
        {
            uint team = 0;
            lock (_siegeGate)
            {
                _siegeTeams.TryGetValue(result.CasterId, out team);
            }
            if (team <= 1)
            {
                lock (_gate)
                {
                    party.SiegeDamage[team] += result.Damage;
                }
            }
        }

        // P3-5a：世界 Boss 按宗门累计有效伤害。只用来在结束时挑「记住谁的名字」，
        // 不改伤害公式、不进结算 / 经验 / 发奖；属性里也没有任何归属项。
        // 键 0 是没宗门的人，照样累计（只算总伤害），只是不会被记成归属。
        if (party.WorldBossWindow.Length > 0
            && message.Result is { Hit: true, Damage: > 0 } bossHit)
        {
            ulong hitSect = 0;
            lock (_gate)
            {
                party.WorldBossSects.TryGetValue(bossHit.CasterId, out hitSect);
                party.WorldBossDamage.TryGetValue(hitSect, out var acc);
                party.WorldBossDamage[hitSect] = acc + bossHit.Damage;
            }
        }

        PartyConn[] conns;
        lock (_gate)
        {
            conns = [.. party.Connections.Where(c => c.Ready && c.Socket != exceptSocket)];
        }

        foreach (var conn in conns)
        {
            await SendAsync(conn.Socket, conn.Send, message, token);
        }
    }

    static async Task SendAsync(
        WebSocket socket, SemaphoreSlim send, ServerMessage message, CancellationToken token)
    {
        var bytes = message.ToByteArray();
        // WebSocket 不允许两条 SendAsync 并发，tick 和收包都会发，串起来。
        await send.WaitAsync(token);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    token);
            }
        }
        finally
        {
            send.Release();
        }
    }

    static string? QueryOrNull(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var values) && values.Count > 0 ? values[0] : null;

    /// <summary>同一个队伍钥匙只能待在一个副本里。空表示练习场。</summary>
    static bool SameDungeon(DungeonDef? left, DungeonDef? right) =>
        string.Equals(left?.Id ?? "", right?.Id ?? "", StringComparison.Ordinal);

    /// <summary>同一个队伍钥匙只能有一种竞技场模式。空表示不是竞技场。</summary>
    static bool SameArena(ArenaModeDef? left, ArenaModeDef? right) =>
        string.Equals(left?.Id ?? "", right?.Id ?? "", StringComparison.Ordinal);

    /// <summary>正常关一条连接。进本被拒时先说原因，再走这里。</summary>
    static async Task CloseAsync(WebSocket socket, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token);
        }
        catch (Exception)
        {
            // 对面已经断了就算了。
        }
    }

    /// <summary>时间戳加进程内序号。重启后也不会和上一轮的场次撞号。</summary>
    static ulong NextSessionId()
    {
        var ms = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seq = (ulong)Interlocked.Increment(ref _sessionSeq) & 0xFFFFUL;
        return (ms << 16) | seq;
    }
}
