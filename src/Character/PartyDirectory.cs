using Grpc.Core;
using Mmo.Proto.Social;

namespace Character;

/// <summary>
/// 队伍事实的只读入口（Q7/H1）。角色服在**进场**与**带账号的结算**两处问社交服
/// 「这个人此刻在不在队伍里、队里有几个人、是不是全员已准备」，拿不到就按 1 人。
///
/// 口径（与 P2-1c 的降级一致）：社交服不可达只可能**少给**组队那一截加成，
/// 绝不超发，也不阻塞结算本身；客户端自报的人数与 member_ids 一律只作提示。
/// 队伍状态只在社交服内存，这里不缓存——缓存会把「已经散了」读成还在。
/// </summary>
public static class PartyDirectory
{
    /// <summary>队伍事实。不在队伍时 Size 固定 1、MemberIds 只有自己。</summary>
    public sealed record Fact(
        bool InParty, ulong PartyId, ulong LeaderCharacterId, int Size,
        bool AllReady, IReadOnlyList<ulong> MemberIds,
        string LootMode = PartyLoot.Personal, string LootRoll = PartyLoot.RollNone,
        int RollMin = 0, int RollMax = 0)
    {
        /// <summary>
        /// 不在队伍 / 社交服不可达：按 personal 结算（每人各发整份，默认口径）。
        /// 区间给 0——personal 不掷点，区间只有 need_greed 才有意义，
        /// 这里也不写死 1..100（数字只在 config/party.json）。
        /// </summary>
        public static Fact Single(ulong characterId) =>
            new(false, 0, 0, 1, false, [characterId]);
    }

    public static async Task<Fact> DescribeAsync(
        SocialService.SocialServiceClient social, ulong characterId, CancellationToken stopping)
    {
        if (characterId == 0)
        {
            return Fact.Single(0);
        }

        PartyResponse reply;
        try
        {
            // 只读、只带角色号：队伍编号与人数都由社交服按内存事实给。
            reply = await social.DescribePartyAsync(
                new PartyRequest { CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(3),
                cancellationToken: stopping);
        }
        catch (RpcException)
        {
            // 社交服不可达：按单人结算，只少给不多给（与同门加成同一个降级口径）。
            return Fact.Single(characterId);
        }

        if (!reply.Ok || !reply.InParty || reply.Members.Count == 0)
        {
            return Fact.Single(characterId);
        }

        var ids = new List<ulong>(reply.Members.Count);
        foreach (var m in reply.Members)
        {
            if (m.CharacterId != 0)
            {
                ids.Add(m.CharacterId);
            }
        }

        if (ids.Count == 0)
        {
            return Fact.Single(characterId);
        }

        return new Fact(true, reply.PartyId, reply.LeaderCharacterId, ids.Count,
            reply.AllReady, ids,
            // Q8：分配口径按社交服回的事实值；老社交服二进制没有这两个字段时
            // 回空串——按 personal 兜底（默认口径，绝不因为缺字段改成分份更少的模式）。
            string.IsNullOrWhiteSpace(reply.LootMode) ? PartyLoot.Personal : reply.LootMode,
            string.IsNullOrWhiteSpace(reply.LootRoll) ? PartyLoot.RollNone : reply.LootRoll,
            reply.RollMin, reply.RollMax);
    }
}
