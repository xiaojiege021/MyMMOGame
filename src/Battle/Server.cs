using System.Collections.Concurrent;
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
    IExpSettlement expSettlement)
{
    /// <summary>20Hz。前摇、冷却、后摇的判定精度都在这个粒度上。</summary>
    const int TickIntervalMs = 50;

    const int MaxMessageBytes = 64 * 1024;

    static long _sessionSeq;

    readonly ConcurrentDictionary<string, Party> _parties = new();
    readonly object _gate = new();

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

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        Party party;
        var send = new SemaphoreSlim(1, 1);
        Member member;
        var created = false;

        // 满员与换本要先把原因发回去再关，不能无声断开——客户端与核对都靠这条。
        EnterRejected? rejected = null;
        lock (_gate)
        {
            if (!_parties.TryGetValue(partyKey, out party!))
            {
                party = new Party
                {
                    Key = partyKey,
                    Encounter = new Encounter(
                        config, settlements, characters, NextSessionId(), partySize, expSettlement),
                    Ticker = new CancellationTokenSource(),
                    Dungeon = dungeon,
                    PartySize = partySize,
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
            else
            {
                try
                {
                    member = party.Encounter.Join(charId, cls);
                }
                catch (InvalidOperationException ex)
                {
                    // 编号撞了：同一场里已经有人用这个角色编号。
                    Console.WriteLine($"join_reject=character={charId} reason={ex.Message}");
                    socket.Abort();
                    return;
                }

                party.Connections.Add(new PartyConn { Socket = socket, Send = send, Member = member });
            }
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
            + $" party_size={party.PartySize} boss_hp_max={party.Encounter.BossHpMax}");

        // 新人进场说给老人听，界面上的队友名单靠这条更新。不发给本人：
        // 本人已经在 Opened 里拿到整份名单，多发一条会把它的读序顶乱。
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
            // 连接层出了没想到的错，打出来再让它断，不能无声无息地停。
            Console.WriteLine($"conn_error=session={party.Encounter.SessionId} {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            Detach(party, member);
            Console.WriteLine($"session_close={party.Encounter.SessionId}");
        }
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

    void Detach(Party party, Member member)
    {
        Party? removed = null;
        lock (_gate)
        {
            party.Connections.RemoveAll(c => c.Member == member);
            party.Encounter.Leave(member);
            if (party.Connections.Count == 0)
            {
                // 最后一个人走了：散场，tick 停掉。
                _parties.TryRemove(party.Key, out _);
                removed = party;
            }
        }

        if (removed is not null)
        {
            removed.Encounter.Abandon();
            removed.Ticker.Cancel();
        }
    }

    async Task TickLoopAsync(Party party)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(party.Ticker.Token))
            {
                foreach (var message in party.Encounter.Tick())
                {
                    await BroadcastAsync(party, message, party.Ticker.Token);
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

    async Task ReceiveLoopAsync(
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
                    return;
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
                    return;
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
            }
        }
    }

    /// <summary>结算结果、队友状态、阶段和收尾发全场：技术架构写的是「把结果发给本里的人」。</summary>
    Task BroadcastAsync(Party party, ServerMessage message, CancellationToken token) =>
        BroadcastAsync(party, message, token, exceptSocket: null);

    async Task BroadcastAsync(Party party, ServerMessage message, CancellationToken token, WebSocket? exceptSocket)
    {
        PartyConn[] conns;
        lock (_gate)
        {
            conns = [.. party.Connections.Where(c => c.Socket != exceptSocket)];
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
