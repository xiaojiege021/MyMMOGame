using System.Text;
using Grpc.Core;
using Mmo.Proto.World;

namespace World;

public sealed partial class WorldGrpcService
{
    /// <summary>
    /// 飞行请求。客户端只给想不想飞，账号从网关来、境界由世界服问角色服；
    /// 权限不够就 ok=false + flight_forbidden，并把 y 拉回地面高度。
    /// </summary>
    public override async Task<FlyResponse> Fly(FlyRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var decision = await flight.FlyAsync(
            account, request.CharacterId, request.WantFly, context.CancellationToken);

        Console.WriteLine("world_fly account=" + account
            + " character=" + request.CharacterId
            + " want=" + request.WantFly
            + " ok=" + decision.Ok
            + " reason=" + decision.Reason
            + " realm=" + decision.Realm
            + " required=" + decision.RequiredRealm
            + " flying=" + decision.Flying
            + " can_fly=" + decision.CanFly
            + " y=" + decision.Y);

        return new FlyResponse
        {
            Ok = decision.Ok,
            Reason = decision.Reason,
            Flying = decision.Flying,
            CanFly = decision.CanFly,
            Realm = decision.Realm,
            RequiredRealm = decision.RequiredRealm,
            Y = decision.Y,
        };
    }

    /// <summary>
    /// 跨区请求。当前区域由分线注册表给，相连、开放与境界门槛都在服务端判；
    /// 被拉回时回包里的 region_id / line_id 仍是原来那一块、那一条。
    /// </summary>
    public override async Task<CrossRegionResponse> CrossRegion(
        CrossRegionRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var target = (request.TargetRegion ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0 || target.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var decision = await flight.CrossAsync(
            account, request.CharacterId, target, context.CancellationToken);

        Console.WriteLine("world_cross account=" + account
            + " character=" + request.CharacterId
            + " target=" + target
            + " ok=" + decision.Ok
            + " reason=" + decision.Reason
            + " realm=" + decision.Realm
            + " required=" + decision.RequiredRealm
            + " region=" + (decision.Line?.RegionId ?? "")
            + " line=" + (decision.Line?.LineId ?? ""));

        return new CrossRegionResponse
        {
            Ok = decision.Ok,
            Reason = decision.Reason,
            RegionId = decision.Line?.RegionId ?? "",
            LineId = decision.Line?.LineId ?? "",
            LineNo = (uint)(decision.Line?.LineNo ?? 0),
            Realm = decision.Realm,
            RequiredRealm = decision.RequiredRealm,
        };
    }
}
