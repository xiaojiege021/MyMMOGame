using Grpc.Core;
using Mmo.Proto.Social;

namespace Social;

/// <summary>
/// 社交服 gRPC（P2-1a1）。校验都在服务端：人数上限、等级、人数都从
/// config/sect.json 读，请求里带不进来。业务前提不满足是正常回包里的
/// ok=false + reason，不是 gRPC 错误码；身份与参数错由网关在 HTTP 层挡。
/// </summary>
public sealed class SocialGrpcService(
    SectTable sectTable,
    SectRepository sects,
    SectFounderGrant founderGrant) : SocialService.SocialServiceBase
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

    // ---- P2-1a2：任命 / 免职 / 只读查席位 ----
    // 席位数字只从 config/sect.json 读，请求里带不进来；回包都是只读事实值。
    public override async Task<PositionStateResponse> AppointPosition(
        PositionRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        if (!PositionFieldsOk(request, txnId))
        {
            return PositionFail("request_missing", request, txnId);
        }

        var outcome = await sects.AppointAsync(
            (long)request.SectId, (long)request.CharacterId,
            (long)request.TargetCharacterId, (request.Position ?? "").Trim(),
            context.CancellationToken);
        return PositionBody(outcome, (request.Position ?? "").Trim(), txnId);
    }

    public override async Task<PositionStateResponse> DismissPosition(
        PositionRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        if (!PositionFieldsOk(request, txnId))
        {
            return PositionFail("request_missing", request, txnId);
        }

        var outcome = await sects.DismissAsync(
            (long)request.SectId, (long)request.CharacterId,
            (long)request.TargetCharacterId, (request.Position ?? "").Trim(),
            context.CancellationToken);
        return PositionBody(outcome, (request.Position ?? "").Trim(), txnId);
    }

    public override async Task<PositionStateResponse> DescribePositions(
        PositionRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || request.SectId == 0)
        {
            return new PositionStateResponse
            {
                Ok = false, Reason = "request_missing", SectId = request.SectId, TxnId = txnId,
            };
        }

        var state = await sects.PositionStateAsync((long)request.SectId, context.CancellationToken);
        if (!state.Found)
        {
            return new PositionStateResponse
            {
                Ok = false, Reason = "sect_not_found", SectId = (ulong)state.SectId, TxnId = txnId,
            };
        }

        return new PositionStateResponse
        {
            Ok = true,
            SectId = (ulong)state.SectId,
            TxnId = txnId,
            MasterCount = state.Master,
            ViceMasterCount = state.ViceMaster,
            ElderCount = state.Elder,
            StewardCount = state.Steward,
            DiscipleCount = state.Disciple,
        };
    }

    static bool PositionFieldsOk(PositionRequest r, string txnId) =>
        (r.Account ?? "").Trim().Length > 0 && r.CharacterId != 0 && txnId.Length > 0
        && r.SectId != 0 && r.TargetCharacterId != 0 && (r.Position ?? "").Trim().Length > 0;

    static PositionStateResponse PositionBody(PositionOutcome o, string requested, string txnId) =>
        new()
        {
            Ok = o.Ok,
            Reason = o.Reason,
            SectId = (ulong)o.SectId,
            Position = o.Ok ? o.Position : requested,
            SeatCap = o.SeatCap,
            SeatCount = o.SeatCount,
            TxnId = txnId,
        };

    static PositionStateResponse PositionFail(string reason, PositionRequest r, string txnId) =>
        new()
        {
            Ok = false, Reason = reason, SectId = r.SectId,
            Position = (r.Position ?? "").Trim(), TxnId = txnId,
        };

    static SectResponse Fail(string reason) => new() { Ok = false, Reason = reason };
}
