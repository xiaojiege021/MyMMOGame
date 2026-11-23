using Grpc.Core;
using Mmo.Proto.Social;

namespace Social;

public sealed partial class SocialGrpcService
{
    public override async Task<SectResponse> CreateSect(
        CreateSectRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var name = (request.Name ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || name.Length == 0)
        {
            return Fail("request_missing");
        }

        // 创建金币：经角色服一笔 grant_txn 事务，社交服不碰角色 schema。
        // founder_gold_cost 待定（方案未给数字），为 0 时这条链路不扣款。
        var charge = await founderGrant.ChargeAsync(
            account, request.CharacterId, txnId, sectTable, context.CancellationToken);
        if (!charge.Ok)
        {
            return Fail(charge.Reason);
        }

        if (charge.Duplicate)
        {
            return Fail("duplicate");
        }

        var created = await sects.CreateAsync(
            name, (long)request.CharacterId, txnId, context.CancellationToken);
        return new SectResponse
        {
            Ok = created.Ok,
            Reason = created.Reason,
            SectId = (ulong)created.SectId,
            Level = created.Level,
            MemberCap = created.MemberCap,
            MemberCount = created.MemberCount,
            Name = name,
            TxnId = txnId,
        };
    }

    public override async Task<SectResponse> JoinSect(
        JoinSectRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || request.SectId == 0)
        {
            return Fail("request_missing");
        }

        // 事务号由网关生成，只作核对；重复入册由 character_id 唯一约束挡。
        var joined = await sects.JoinAsync(
            (long)request.SectId, (long)request.CharacterId, context.CancellationToken);
        return new SectResponse
        {
            Ok = joined.Ok,
            Reason = joined.Reason,
            SectId = (ulong)joined.SectId,
            Level = joined.Level,
            MemberCap = joined.MemberCap,
            MemberCount = joined.MemberCount,
            TxnId = txnId,
        };
    }

    public override async Task<SectResponse> DescribeSect(
        DescribeSectRequest request, ServerCallContext context)
    {
        var state = request.SectId != 0
            ? await sects.StateAsync((long)request.SectId, context.CancellationToken)
            : await sects.StateByNameAsync((request.Name ?? "").Trim(), context.CancellationToken);

        if (!state.Found)
        {
            return new SectResponse { Ok = false, Reason = "sect_not_found" };
        }

        return new SectResponse
        {
            Ok = true,
            SectId = (ulong)state.SectId,
            Name = state.Name,
            Level = state.Level,
            MemberCap = state.MemberCap,
            MemberCount = state.MemberCount,
        };
    }

    // ---- P2-1c：只读查一个角色归属的宗门编号 ----
    // 建宗门的人不进名册，判定看 sect_member.sect_id 与 sect.master_character_id；
    // 两条都查不到才算没宗门。只回宗门编号，不写库。角色服据此判整队是否同宗门。
    public override async Task<DescribeCharacterSectResponse> DescribeCharacterSect(
        DescribeCharacterSectRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            return new DescribeCharacterSectResponse
            {
                Ok = false, Reason = "request_missing", Found = false, SectId = 0,
            };
        }

        var sect = await sects.CharacterSectAsync(
            (long)request.CharacterId, context.CancellationToken);
        return new DescribeCharacterSectResponse
        {
            Ok = true,
            Reason = sect.Found ? "" : "not_in_sect",
            Found = sect.Found,
            SectId = (ulong)sect.SectId,
        };
    }

    // ---- N2：名册逐条只读 ----
    // 只回角色号和职位。不任免、不改人数上限、不写库。
    public override async Task<DescribeSectRosterResponse> DescribeSectRoster(
        DescribeSectRosterRequest request, ServerCallContext context)
    {
        if (request.SectId == 0)
        {
            return new DescribeSectRosterResponse
            {
                Ok = false, Reason = "request_missing", SectId = 0,
            };
        }

        var roster = await sects.RosterAsync((long)request.SectId, context.CancellationToken);
        var reply = new DescribeSectRosterResponse
        {
            Ok = roster.Found,
            Reason = roster.Found ? "" : "sect_not_found",
            SectId = (ulong)roster.SectId,
        };
        foreach (var member in roster.Members)
        {
            reply.Members.Add(new SectRosterMember
            {
                CharacterId = (ulong)member.CharacterId,
                Position = member.Position ?? "",
            });
        }

        return reply;
    }

    // ---- P3-3a3：只读查一个角色所属宗门的科技等级 ----
    // 战斗服开城战时来读；社交服只回等级事实值，不算伤害、不给加成系数、不写库。
    // 没宗门 / 没科技行都给 0，上游不可达由战斗服降级处理，这里不抛 gRPC 错误。
    public override async Task<DescribeSectTechResponse> DescribeSectTech(
        DescribeSectTechRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            return new DescribeSectTechResponse
            {
                Ok = false, Reason = "request_missing", Found = false, SectId = 0, TechLevel = 0,
            };
        }

        var state = await sects.SectTechAsync(
            (long)request.CharacterId, context.CancellationToken);
        return new DescribeSectTechResponse
        {
            Ok = true,
            Reason = state.Found ? "" : "not_in_sect",
            Found = state.Found,
            SectId = (ulong)state.SectId,
            TechLevel = state.TechLevel,
        };
    }
}
