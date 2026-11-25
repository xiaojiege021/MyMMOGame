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
        // A9：加点先并进职业属性再折算，之后的生命 / 蓝 / 攻击 / 治疗 / 仇恨与战力全部按加了点的算。
        var bonus = profile.Bonus;
        var rawAttack = (long)Math.Round(realClass.Attack(bonus)) + level * table.AttackPerLevel;
        var rawHp = realClass.HpMax(config.Classes.HpPerVitality, bonus) + level * table.HpPerLevel;
        var rawMana = realClass.ManaMax(bonus) + level * table.ManaPerLevel;
        var rawHeal = (long)Math.Round(realClass.HealPower(bonus))
                      + level * table.HealPerLevel;
        var rawThreat = realClass.ThreatStability(bonus);
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
}
