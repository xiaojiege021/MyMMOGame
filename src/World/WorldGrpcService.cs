using Grpc.Core;
using Mmo.Proto.World;

namespace World;

/// <summary>
/// 表世界分线的 gRPC 入口。区域与人物编号全从配置读，请求里带不进分线号、
/// 人数或容量：客户端改包改不了自己被分到哪条线。
/// 这里不结算技能、不发奖励，也不写角色库（P1-3b 与战斗服各自负责）。
/// </summary>
public sealed class WorldGrpcService(LineRegistry registry, WorldCatalog catalog)
    : global::Mmo.Proto.World.WorldService.WorldServiceBase
{
    public override Task<EnterLineResponse> EnterLine(
        EnterLineRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var regionId = (request.RegionId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0 || regionId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var result = registry.Enter(account, request.CharacterId, regionId);
        Console.WriteLine("world_enter account=" + account
            + " character=" + request.CharacterId
            + " region=" + regionId
            + " entered=" + result.Entered
            + " reason=" + result.Reason
            + " line=" + result.LineId
            + " size=" + result.LineSize
            + " cap=" + result.Capacity
            + " duplicate=" + result.Duplicate);

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
            var region = catalog.Find(regionId);
            if (region is not null)
            {
                response.Neighbors.AddRange(region.Neighbors);
                response.DialogueIds.AddRange(catalog.DialogueIdsFor(regionId));
            }
        }

        return Task.FromResult(response);
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

    public override Task<ListRegionsResponse> ListRegions(
        ListRegionsRequest request, ServerCallContext context)
    {
        var response = new ListRegionsResponse { Version = (uint)catalog.Version };
        foreach (var region in catalog.Regions)
        {
            var info = new RegionInfo
            {
                Id = region.Id,
                Name = region.Name,
                Open = region.Open,
            };
            info.Chapters.AddRange(region.Chapters.Select(c => (uint)c));
            info.Neighbors.AddRange(region.Neighbors);
            info.DialogueIds.AddRange(catalog.DialogueIdsFor(region.Id));
            response.Regions.Add(info);
        }

        response.Speakers.AddRange(catalog.Speakers);
        response.Factions.AddRange(catalog.Factions);
        return Task.FromResult(response);
    }
}
