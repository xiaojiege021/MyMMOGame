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

    // ---- P2-1a3：传位只给副掌门 / 退出宗门 / 只读查掌门状态 ----
    // 掌门权威、席位计数都只在社交服判定，请求里带不进来；回包都是只读事实值。
    // 业务拒绝是正常回包里的 ok=false + reason，未生效数值给 0。
    public override async Task<LeadershipResponse> TransferLeadership(
        LeadershipRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        if (!LeadershipFieldsOk(request, txnId) || request.TargetCharacterId == 0)
        {
            return LeadershipFail("request_missing", request.SectId, txnId);
        }

        var outcome = await sects.TransferAsync(
            (long)request.SectId, (long)request.CharacterId,
            (long)request.TargetCharacterId, txnId, context.CancellationToken);
        return LeadershipBody(outcome, txnId);
    }

    public override async Task<LeadershipResponse> LeaveSect(
        LeaveSectRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || request.SectId == 0)
        {
            return LeadershipFail("request_missing", request.SectId, txnId);
        }

        var outcome = await sects.LeaveAsync(
            (long)request.SectId, (long)request.CharacterId, context.CancellationToken);
        return LeadershipBody(outcome, txnId);
    }

    public override async Task<LeadershipResponse> DescribeLeadership(
        LeadershipRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        if (!LeadershipFieldsOk(request, txnId))
        {
            return LeadershipFail("request_missing", request.SectId, txnId);
        }

        var state = await sects.LeadershipStateAsync(
            (long)request.SectId, context.CancellationToken);
        if (!state.Found)
        {
            return LeadershipFail("sect_not_found", request.SectId, txnId);
        }

        return new LeadershipResponse
        {
            Ok = true,
            SectId = (ulong)state.SectId,
            OldMaster = (ulong)state.LastTransferOldMaster,
            NewMaster = (ulong)state.MasterCharacterId,
            MasterCount = state.MasterCount,
            ViceMasterCount = state.ViceMasterCount,
            MemberCount = state.MemberCount,
            TxnId = txnId,
        };
    }

    static bool LeadershipFieldsOk(LeadershipRequest r, string txnId) =>
        (r.Account ?? "").Trim().Length > 0 && r.CharacterId != 0 && txnId.Length > 0
        && r.SectId != 0;

    static LeadershipResponse LeadershipBody(LeadershipOutcome o, string txnId) =>
        new()
        {
            Ok = o.Ok,
            Reason = o.Reason,
            SectId = (ulong)o.SectId,
            OldMaster = o.Ok ? (ulong)o.OldMaster : 0,
            NewMaster = o.Ok ? (ulong)o.NewMaster : 0,
            MasterCount = o.Ok ? o.MasterCount : 0,
            ViceMasterCount = o.Ok ? o.ViceMasterCount : 0,
            MemberCount = o.Ok ? o.MemberCount : 0,
            TxnId = txnId,
        };

    static LeadershipResponse LeadershipFail(string reason, ulong sectId, string txnId) =>
        new() { Ok = false, Reason = reason, SectId = sectId, TxnId = txnId };

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

    // ---- P2-1a4：升级只耗建设 / 只读查升级事实 ----
    // 升级消耗、目标等级、名册上限都只由社交服按 config/sect.json 与库里的事实判定，
    // 请求里带不进来；回包都是只读事实值，业务拒绝时未生效数值给 0。
    public override async Task<UpgradeResponse> UpgradeSect(
        UpgradeSectRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || request.SectId == 0)
        {
            return UpgradeFail("request_missing", request.SectId, txnId);
        }

        var outcome = await sects.UpgradeAsync(
            (long)request.SectId, (long)request.CharacterId, txnId, context.CancellationToken);
        return UpgradeBody(outcome, txnId);
    }

    public override async Task<UpgradeResponse> DescribeUpgrade(
        UpgradeSectRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || request.SectId == 0)
        {
            return UpgradeFail("request_missing", request.SectId, txnId);
        }

        var state = await sects.UpgradeStateAsync(
            (long)request.SectId, context.CancellationToken);
        if (!state.Found)
        {
            return UpgradeFail("sect_not_found", request.SectId, txnId);
        }

        // 只读：old/new 都填当前等级，建设前后都填当前值，cost 填下一档消耗（5 级为 0）。
        return new UpgradeResponse
        {
            Ok = true,
            SectId = (ulong)state.SectId,
            OldLevel = state.Level,
            NewLevel = state.Level,
            MemberCap = state.MemberCap,
            ConstructionBefore = state.Construction,
            ConstructionAfter = state.Construction,
            UpgradeCost = state.NextCost,
            TxnId = txnId,
        };
    }

    static UpgradeResponse UpgradeBody(UpgradeOutcome o, string txnId) =>
        new()
        {
            Ok = o.Ok,
            Reason = o.Reason,
            SectId = (ulong)o.SectId,
            OldLevel = o.Ok ? o.OldLevel : 0,
            NewLevel = o.Ok ? o.NewLevel : 0,
            MemberCap = o.Ok ? o.MemberCap : 0,
            ConstructionBefore = o.Ok ? o.ConstructionBefore : 0,
            ConstructionAfter = o.Ok ? o.ConstructionAfter : 0,
            UpgradeCost = o.Ok ? o.UpgradeCost : 0,
            TxnId = txnId,
        };

    static UpgradeResponse UpgradeFail(string reason, ulong sectId, string txnId) =>
        new() { Ok = false, Reason = reason, SectId = sectId, TxnId = txnId };

    static SectResponse Fail(string reason) => new() { Ok = false, Reason = reason };
}
