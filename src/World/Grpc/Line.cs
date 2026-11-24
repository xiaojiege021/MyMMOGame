using System.Text;
using Grpc.Core;
using Mmo.Proto.World;

namespace World;

public sealed partial class WorldGrpcService
{
    public override async Task<EnterLineResponse> EnterLine(
        EnterLineRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var regionId = (request.RegionId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0 || regionId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        // 开放条件在这里先过 RegionGate：静态开放的地表不看剧情，旧天城区 / 焦原这种
        // 「第 11 场才开 + 立仙立魔分路」的由它去角色服问进度。分线注册表只认结论。
        var region = catalog.Find(regionId);
        var access = region is null
            ? new RegionAccess(false, "", 0, "", 0)
            : await regions.CheckAsync(account, request.CharacterId, region, context.CancellationToken);
        // 名字是网关复核归属后从角色服取的只读文本，客户端请求体里没有这个字段。
        var result = region is not null && !access.Ok
            ? new EnterResult(false, access.Reason, "", "", 0, 0, 0, false)
            : registry.Enter(account, request.CharacterId, regionId, request.DisplayName,
                unlocked: access.Ok);
        // B4：进线之后取一次坐骑加成写进分线注册表（世界服判地表位移上限要用它）。
        // 换坐骑后最迟在下一次进线 / 飞行 / 跨区时生效，不需要重连。
        var mountSpeedBps = result.Entered
            ? await flight.SyncMountSpeedAsync(account, request.CharacterId, context.CancellationToken)
            : 0;

        Console.WriteLine("world_enter account=" + account
            + " character=" + request.CharacterId
            + " region=" + regionId
            + " entered=" + result.Entered
            + " reason=" + result.Reason
            + " line=" + result.LineId
            + " size=" + result.LineSize
            + " cap=" + result.Capacity
            + " duplicate=" + result.Duplicate
            + " stage=" + access.MainStage
            + " unlock_stage=" + access.UnlockStage
            + " alignment=" + (access.Alignment.Length == 0 ? "none" : access.Alignment)
            + " mount_speed_bps=" + mountSpeedBps);

        var response = new EnterLineResponse
        {
            Entered = result.Entered,
            Reason = result.Reason,
            LineId = result.LineId,
            RegionId = result.RegionId,
            LineNo = (uint)result.LineNo,
            LineSize = (uint)result.LineSize,
            LineCapacity = (uint)result.Capacity,
            Duplicate = result.Duplicate,
        };

        if (result.Entered)
        {
            if (region is not null)
            {
                response.Neighbors.AddRange(region.Neighbors);
                response.DialogueIds.AddRange(catalog.DialogueIdsFor(regionId));
            }
        }

        return response;
    }

    public override Task<LeaveLineResponse> LeaveLine(
        LeaveLineRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var result = registry.Leave(account, request.CharacterId);
        Console.WriteLine("world_leave account=" + account
            + " character=" + request.CharacterId
            + " left=" + result.Left
            + " line=" + result.LineId
            + " size=" + result.LineSize);

        return Task.FromResult(new LeaveLineResponse
        {
            Left = result.Left,
            LineId = result.LineId,
            LineSize = (uint)result.LineSize,
        });
    }
}
