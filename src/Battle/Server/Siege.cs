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
}
