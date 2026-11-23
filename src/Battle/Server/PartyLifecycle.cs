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
}
