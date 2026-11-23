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
}
