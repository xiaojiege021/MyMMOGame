using Grpc.Core;
using Mmo.Proto.Character;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片；A9 属性加点在这里，
// 判定与回包形状与既有分片一致：业务拒绝不抛异常，回 ok=false + reason。
public sealed partial class CharacterGrpcService
{
    /// <summary>
    /// A9：只读加点状态——手上还剩多少点、四属性各加了多少。屏上「基础 + 加点」
    /// 由客户端按职业表相加（基础值仍只在 config/classes.json），角色服只回事实值。
    /// </summary>
    public override async Task<DescribeAttributePointsResponse> DescribeAttributePoints(
        DescribeAttributePointsRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var snapshot = await attrPoints.DescribeAsync(
            (long)request.CharacterId, context.CancellationToken);
        if (!snapshot.Found)
        {
            return new DescribeAttributePointsResponse { Ok = false };
        }

        return new DescribeAttributePointsResponse
        {
            Ok = true,
            ClassId = snapshot.ClassId,
            Level = snapshot.Level,
            AttrPoints = snapshot.Remaining,
            Strength = snapshot.Allocated.Strength,
            Agility = snapshot.Allocated.Agility,
            Spirit = snapshot.Allocated.Spirit,
            Vitality = snapshot.Allocated.Vitality,
        };
    }

    /// <summary>
    /// A9：加一次点。点数只在角色服算：点数够不够、单属性上限、事务号幂等
    /// 全在仓储的一笔事务里判；本版没有洗点，加错了也只能等以后开洗点卡。
    /// </summary>
    public override async Task<AllocateAttributePointsResponse> AllocateAttributePoints(
        AllocateAttributePointsRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var outcome = await attrPoints.AllocateAsync(
            (long)request.CharacterId, request.Account, request.TxnId ?? "",
            request.Attribute ?? "", request.Points, context.CancellationToken);

        return new AllocateAttributePointsResponse
        {
            Ok = outcome.Reason.Length == 0,
            Reason = outcome.Reason,
            Duplicate = outcome.Duplicate,
            ClassId = outcome.ClassId,
            Level = outcome.Level,
            AttrPoints = outcome.Remaining,
            Strength = outcome.Allocated.Strength,
            Agility = outcome.Allocated.Agility,
            Spirit = outcome.Allocated.Spirit,
            Vitality = outcome.Allocated.Vitality,
        };
    }
}
