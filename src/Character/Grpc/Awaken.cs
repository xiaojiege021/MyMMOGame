using Grpc.Core;
using Mmo.Proto.Character;

namespace Character;

// B6：觉醒。方案 8.2「满级后开放觉醒」；14.5 的三条完成标准是
// 满级后开放、不抬等级上限、不另开一套战斗规则。
// 所以这里只有一条写：把本分支那一条觉醒技的编号写进角色行，不加等级、
// 不推进境界、不动金币与背包。技能本体与数值在 config/skills.json。
public sealed partial class CharacterGrpcService
{
    /// <summary>
    /// 觉醒一次。请求里只有角色与分支：技能编号由 config/awaken.json 的分支映射现取，
    /// 等级、境界与金币都从库里的事实值判。业务拒绝一律 ok=false + reason，
    /// 参数错（角色号 0 / 分支空串）走 InvalidArgument。
    /// </summary>
    public override async Task<AwakenResponse> Awaken(
        AwakenRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var branchId = (request.BranchId ?? "").Trim();
        if (request.CharacterId == 0 || branchId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var outcome = await characters.AwakenAsync(
            (long)request.CharacterId, branchId, awakenTable, context.CancellationToken);

        Console.WriteLine("awaken account=" + account
            + " character=" + request.CharacterId
            + " branch=" + (outcome.Branch.Length == 0 ? "none" : outcome.Branch)
            + " skill=" + outcome.SkillId
            + " level=" + outcome.Level
            + " ok=" + (outcome.Reason.Length == 0 ? "true" : "false")
            + " reason=" + (outcome.Reason.Length == 0 ? "none" : outcome.Reason)
            + " duplicate=" + (outcome.Duplicate ? "true" : "false"));

        return new AwakenResponse
        {
            Ok = outcome.Reason.Length == 0,
            Reason = outcome.Reason,
            Branch = outcome.Branch,
            SkillId = outcome.SkillId,
            Level = outcome.Level,
            Duplicate = outcome.Duplicate,
        };
    }

    /// <summary>只读觉醒状态。不写库，也不回背包 / 货币；屏幕与核对都读这一处。</summary>
    public override async Task<DescribeAwakenResponse> DescribeAwaken(
        DescribeAwakenRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var state = await characters.AwakenStateAsync(
            (long)request.CharacterId, context.CancellationToken);
        if (state is null)
        {
            return new DescribeAwakenResponse { Found = false };
        }

        return new DescribeAwakenResponse
        {
            Found = true,
            Branch = state.Value.Branch,
            SkillId = state.Value.SkillId,
            Level = state.Value.Level,
            AwakenAtMs = state.Value.AwakenAt is { } at ? at.ToUnixTimeMilliseconds() : 0,
        };
    }
}
