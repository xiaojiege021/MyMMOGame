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
    /// 建组与入场：副本 / 竞技场（练习场也走这里）。原 HandleAsync 的后半段，
    /// 参数由路由段传进来，拒绝码与写入顺序一字未改。
    /// </summary>
    async Task JoinPartyAsync(HttpContext context, IQueryCollection query,
        ulong charId, string classId, ClassDef cls)
    {
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
            // B15：这一场按副本表认到的内容首领（dungeons.json 的 boss_id）。场次血量与
            // 出手仍按 config/battle.json 的 boss 建，这里只把「打的是哪个内容 Boss」打出来，
            // 供核对与真机取证；不属于协议字段，客户端不读。
            + $" dungeon={dungeon?.Id ?? "none"} dungeon_boss={dungeon?.BossId ?? "none"}"
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
}
