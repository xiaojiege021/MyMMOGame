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

// 14.8 D3a/D3b：BattleServer 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class BattleServer
{
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
}
