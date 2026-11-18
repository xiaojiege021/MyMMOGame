using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Battle;

/// <summary>
/// 一场通关之后，把「这一笔等级经验」交给角色服结算。战斗服只提供事务号、
/// 来源、这一场的进本人数，以及这一场所在副本与击杀数，不带任何数值：
/// 基础经验、组队加成、升级曲线全在角色服的 config/exp.json，历练数值全在
/// config/dungeons.json，角色服是持久数据的唯一写入方。
/// 等级经验与历练走同一笔（开发架构「结算离开」）：同一个事务号，角色服在
/// 同一事务里写经验与历练，重放时两样一起回滚。表世界/竞技场没有副本编号，
/// 按方案 8.1 不计历练。活动、任务等来源也调角色服同一个接口，不另写加成。
/// </summary>
public interface IExpSettlement : IDisposable
{
    /// <summary>
    /// Q8/H2：结算前问一次这一场按哪种口径分掉落。返回的是服务端事实
    /// （社交服的队伍规则经角色服转出）；拿不到就按 personal。
    /// </summary>
    LootScope ResolveLootScope(IReadOnlyCollection<ulong> memberIds);

    /// <summary>
    /// 逐人入包。dropsByMember 是这一场分配之后**每个人各自应得**的那几行：
    /// personal 时每人是整份，need_greed 时只有赢家拿到该行。
    /// </summary>
    void SettleParty(
        string settlementId, string source, int partySize, IReadOnlyCollection<ulong> memberIds,
        string dungeonId, int qualifiedKills,
        IReadOnlyDictionary<ulong, IReadOnlyList<DropLineDef>> dropsByMember);
}

/// <summary>
/// 没配 exp_upstream 时走这里：P0 的本机结算口径，只写本地结算记录，
/// 不改任何角色数据。老核对与不带副本的练习场都走这一条，行为与 P0 完全一致。
/// </summary>
public sealed class NoExpSettlement : IExpSettlement
{
    public LootScope ResolveLootScope(IReadOnlyCollection<ulong> memberIds) =>
        LootScope.Personal(LootAllocator.Normalize([.. memberIds]));

    public void SettleParty(
        string settlementId, string source, int partySize, IReadOnlyCollection<ulong> memberIds,
        string dungeonId, int qualifiedKills,
        IReadOnlyDictionary<ulong, IReadOnlyList<DropLineDef>> dropsByMember)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// 服间调用实现。每人这一场一份个人经验，事务号 = 结算号 + 角色编号，
/// 同一个号再提交由角色服的幂等表挡住。结算失败只记日志，不影响本地结算记录
/// 与回包——角色服没起来时，P0 那一场仍然能正常打完。
/// </summary>
public sealed class GrpcExpSettlement : IExpSettlement, IDisposable
{
    readonly GrpcChannel _channel;
    readonly CharacterService.CharacterServiceClient _client;

    public GrpcExpSettlement(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new CharacterService.CharacterServiceClient(_channel);
    }

    public LootScope ResolveLootScope(IReadOnlyCollection<ulong> memberIds)
    {
        var members = LootAllocator.Normalize([.. memberIds]);
        // 用本场第一个成员问：队伍是同一个，问谁结果都一样。
        var self = members.Count > 0 ? members[0] : 0UL;
        if (self == 0)
        {
            return LootScope.Personal(members);
        }

        try
        {
            var reply = _client.DescribeLootScope(
                new DescribeLootScopeRequest { CharacterId = self },
                deadline: DateTime.UtcNow.AddSeconds(3));

            Console.WriteLine("loot_scope=ok character=" + self
                + " mode=" + reply.LootMode
                + " roll=" + reply.LootRoll
                + " min=" + reply.RollMin
                + " max=" + reply.RollMax
                + " party=" + (reply.InParty ? "true" : "false")
                + " members=" + reply.MemberIds.Count);

            return new LootScope(
                string.IsNullOrWhiteSpace(reply.LootMode) ? PartyLoot.Personal : reply.LootMode,
                string.IsNullOrWhiteSpace(reply.LootRoll) ? PartyLoot.RollNone : reply.LootRoll,
                reply.RollMin, reply.RollMax, [.. reply.MemberIds]);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            // 角色服 / 社交服不可达：按 personal 结算。这是默认口径，
            // 不改成别的模式（改小会让玩家白丢件，改大等于超发）。
            Console.WriteLine("loot_scope=error character=" + self
                + " " + ex.Message.Replace('\n', ' '));
            return LootScope.Personal(members);
        }
    }

    public void SettleParty(
        string settlementId, string source, int partySize, IReadOnlyCollection<ulong> memberIds,
        string dungeonId, int qualifiedKills,
        IReadOnlyDictionary<ulong, IReadOnlyList<DropLineDef>> dropsByMember)
    {
        foreach (var characterId in memberIds)
        {
            var drops = dropsByMember.TryGetValue(characterId, out var mine)
                ? mine
                : (IReadOnlyList<DropLineDef>)[];
            var txnId = settlementId + ":" + characterId.ToString("x16");
            try
            {
                var request = new SettleExpRequest
                {
                    CharacterId = characterId,
                    TxnId = txnId,
                    Source = source,
                    PartySize = (uint)partySize,
                    // P2-1c：把整队成员交给角色服判同宗门；战斗服不解析谁同门。
                    MemberIds = { memberIds },
                    // P3-1a2 补正：这一场的副本与有资格击杀数。历练数值由角色服
                    // 按 config/dungeons.json 算，战斗服只报个数，不报数值。
                    DungeonId = dungeonId,
                    QualifiedKills = (uint)Math.Max(0, qualifiedKills),
                };
                foreach (var drop in drops)
                {
                    request.Drops.Add(new SettleDropLine { ItemId = drop.ItemId, Count = drop.Count });
                }

                var reply = _client.SettleExp(request, deadline: DateTime.UtcNow.AddSeconds(5));

                Console.WriteLine("exp_settle=ok txn=" + txnId
                    + " character=" + characterId
                    + " party=" + partySize
                    + " gain=" + reply.ExpGain
                    + " bonus=" + reply.PartyBonus
                    + " same_sect=" + reply.SameSectBonus
                    + " level=" + reply.Level
                    + " duplicate=" + reply.Duplicate
                    + " dungeon=" + (dungeonId.Length == 0 ? "none" : dungeonId)
                    + " kills=" + qualifiedKills
                    + " drill=" + reply.DrillGain
                    + " drill_after=" + reply.Drill
                    + " drill_reason=" + reply.DrillReason
                    + " drops=" + drops.Count
                    // P3-5a：来源只追加在串尾，老断言（gain= / bonus= 的顺序）一字不动。
                    + " source=" + source);
            }
            catch (Exception ex) when (ex is RpcException or InvalidOperationException)
            {
                Console.WriteLine("exp_settle=error txn=" + txnId
                    + " character=" + characterId
                    + " " + ex.Message.Replace('\n', ' '));
            }
        }
    }

    public void Dispose() => _channel.Dispose();
}
