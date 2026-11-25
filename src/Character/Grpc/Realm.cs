using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

    /// <summary>
    /// P2-4a：二转。只把 15 级选定的那条分支往前推一步：45 级才允许，
    /// 目标必须就是已经写下的那条，改去另一条分支一律 branch_locked。
    /// 分支归属（是不是这个职业的分支）在这里按配置先验，库里的状态由仓储一笔事务说了算。
    /// </summary>
    public override async Task<AdvanceBranchResponse> AdvanceBranch(
        AdvanceBranchRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var branchId = (request.BranchId ?? "").Trim();
        if (branchId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "branch_missing"));
        }

        var identity = await characters.BattleIdentityAsync(
            (long)request.CharacterId, context.CancellationToken);
        if (identity is null)
        {
            return new AdvanceBranchResponse { Ok = false, Reason = "character_not_found" };
        }

        if (!branches.BelongsTo(branchId, identity.Value.ClassId))
        {
            return new AdvanceBranchResponse
            {
                Ok = false,
                Reason = "branch_not_found",
                Branch = identity.Value.Branch,
                BranchStep = identity.Value.BranchStep,
                Level = identity.Value.Level,
            };
        }

        var outcome = await characters.AdvanceBranchAsync(
            (long)request.CharacterId, branchId, context.CancellationToken);
        return new AdvanceBranchResponse
        {
            Ok = outcome.Reason.Length == 0,
            Reason = outcome.Reason,
            Branch = outcome.Branch,
            BranchStep = outcome.Step,
            Level = outcome.Level,
            Duplicate = outcome.Duplicate,
        };
    }


    /// <summary>
    /// P3-1a2：突破。修为就是当前境界，历练、炼心（主线 + 必要支线）与灵石
    /// 全部由仓储在库里读；版本开关复用 P3-1a1 的 RealmTable.CheckAdvance。
    /// 条件不满足时 ok=false + reason，历练与灵石都不动；掷过了才扣，
    /// 失败时修为停在原境界。
    /// </summary>
    public override async Task<AdvanceRealmResponse> AdvanceRealm(
        AdvanceRealmRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > RealmAdvanceRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_id_invalid"));
        }

        var account = (request.Account ?? "").Trim();
        var outcome = await realmAdvances.AdvanceAsync(
            (long)request.CharacterId,
            account.Length == 0 ? null : account,
            txnId,
            context.CancellationToken);

        var response = new AdvanceRealmResponse
        {
            Ok = outcome.Executed,
            Success = outcome.Success,
            Reason = outcome.Reason,
            FromRealm = outcome.From,
            NextRealm = outcome.Next,
            Realm = outcome.Realm,
            DrillBefore = outcome.DrillBefore,
            DrillRequired = outcome.DrillRequired,
            DrillCost = outcome.DrillCost,
            StoneKind = outcome.StoneKind,
            StoneCost = outcome.StoneCost,
            Drill = outcome.DrillAfter,
            StoneLeft = outcome.StoneLeft,
            MainStage = outcome.MainStage,
            MainStageRequired = outcome.MainStageRequired,
            SuccessPct = outcome.SuccessPct,
            Duplicate = outcome.Duplicate,
            // B1：境界丹——配置里的编号与枚数，加上扣完的余额（事实值）。
            PillKind = outcome.PillKind,
            PillCount = outcome.PillCount,
            PillLeft = outcome.PillLeft,
        };
        response.SideQuestsMissing.AddRange(outcome.MissingQuests);
        return response;
    }


    /// <summary>
    /// P3-1a2：历练结算。只统计已经能进入的副本：写了 realm 的新副本仍按
    /// 「这一段开没开、个人境界够不够」判，旧副本没有境界门槛。给多少历练
    /// 由 config/dungeons.json 的 drill 决定，请求里带不进来。
    /// </summary>
    public override async Task<SettleDrillResponse> SettleDrill(
        SettleDrillRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var dungeonId = (request.DungeonId ?? "").Trim();
        if (dungeonId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "dungeon_missing"));
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > RealmAdvanceRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_id_invalid"));
        }

        var dungeon = dungeons.Find(dungeonId);
        if (dungeon is null)
        {
            return new SettleDrillResponse
            {
                Ok = false,
                Reason = "unknown_dungeon",
                DungeonId = dungeonId,
            };
        }

        var account = (request.Account ?? "").Trim();
        var outcome = await realmAdvances.SettleDrillAsync(
            (long)request.CharacterId,
            account.Length == 0 ? null : account,
            dungeon.Id,
            dungeon.Realm,
            dungeon.Drill,
            txnId,
            context.CancellationToken);

        return new SettleDrillResponse
        {
            Ok = outcome.Found && outcome.Reason.Length == 0 && !outcome.Duplicate,
            Reason = outcome.Reason,
            DungeonId = outcome.DungeonId,
            DrillGain = outcome.Gain,
            Drill = outcome.Drill,
            Duplicate = outcome.Duplicate,
        };
    }


    public override async Task<PromoteRealmPhaseResponse> PromoteRealmPhase(
        PromoteRealmPhaseRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > RealmPhaseRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_id_invalid"));
        }

        var account = (request.Account ?? "").Trim();
        var outcome = await realmPhases.PromoteAsync(
            (long)request.CharacterId,
            account.Length == 0 ? null : account,
            txnId,
            context.CancellationToken);

        return new PromoteRealmPhaseResponse
        {
            Ok = outcome.Executed,
            Reason = outcome.Reason,
            FromPhase = outcome.FromPhase,
            ToPhase = outcome.ToPhase,
            PhaseName = outcome.PhaseName,
            PassivePctBps = outcome.PassivePctBps,
            Level = outcome.Level,
            LevelRequired = outcome.LevelRequired,
            Duplicate = outcome.Duplicate,
            PointsAwarded = outcome.PointsAwarded,
            AttrPoints = outcome.AttrPoints,
        };
    }


    public override async Task<DescribeRealmPassiveResponse> DescribeRealmPassive(
        DescribeRealmPassiveRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var snap = await realmPhases.DescribePassiveAsync(
            (long)request.CharacterId, context.CancellationToken);
        if (!snap.Found)
        {
            return new DescribeRealmPassiveResponse { Ok = false };
        }

        return new DescribeRealmPassiveResponse
        {
            Ok = true,
            Realm = snap.Realm,
            RealmPhase = snap.Phase,
            PhaseName = snap.PhaseName,
            PassivePctBps = snap.PassivePctBps,
            AttrPoints = snap.AttrPoints,
        };
    }
}
