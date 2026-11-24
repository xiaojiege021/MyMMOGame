using Grpc.Core;
using Mmo.Proto.World;

namespace World;

/// <summary>
/// B5 摆摊在世界服的四条口。世界服只保存显示用的摘要：摊主、地表、坐标、招牌与
/// 在售件数；不读角色库、不动背包、不结算任何交易。挂单与成交都在角色服，
/// 网关拿到角色服的结果之后才来这里登记 / 刷新 / 注销。
/// </summary>
public sealed partial class WorldGrpcService
{
    public override Task<WorldStallResponse> OpenStall(
        WorldStallOpenRequest request, ServerCallContext context)
    {
        var stallId = (request.StallId ?? "").Trim();
        var regionId = (request.RegionId ?? "").Trim();
        if (stallId.Length == 0)
        {
            return Task.FromResult(new WorldStallResponse { Ok = false, Reason = "stall_missing" });
        }

        if (regionId.Length == 0 || catalog.Regions.All(r => r.Id != regionId))
        {
            return Task.FromResult(new WorldStallResponse
            {
                Ok = false,
                Reason = "region_missing",
                StallId = stallId,
            });
        }

        stallStalls.Open(new StallRegistry.Entry(
            stallId, request.OwnerId, request.OwnerName ?? "", regionId,
            request.PosX, request.PosY, request.Title ?? "", request.LiveListings));

        Console.WriteLine("world_stall_open stall=" + stallId
            + " owner=" + request.OwnerId
            + " region=" + regionId
            + " live=" + request.LiveListings
            + " count=" + stallStalls.Count);

        return Task.FromResult(new WorldStallResponse
        {
            Ok = true,
            StallId = stallId,
            Count = (uint)stallStalls.Count,
        });
    }

    public override Task<WorldStallResponse> RenewStall(
        WorldStallRenewRequest request, ServerCallContext context)
    {
        var stallId = (request.StallId ?? "").Trim();
        if (stallId.Length == 0)
        {
            return Task.FromResult(new WorldStallResponse { Ok = false, Reason = "stall_missing" });
        }

        // 摊子已经关了 / 世界服重启过：只回 ok=false，不改任何东西，也不补一条假摊。
        var ok = stallStalls.Renew(stallId, request.LiveListings);
        return Task.FromResult(new WorldStallResponse
        {
            Ok = ok,
            Reason = ok ? "" : "stall_not_found",
            StallId = stallId,
            Count = (uint)stallStalls.Count,
        });
    }

    public override Task<WorldStallResponse> CloseStall(
        WorldStallCloseRequest request, ServerCallContext context)
    {
        var stallId = (request.StallId ?? "").Trim();
        if (stallId.Length == 0)
        {
            return Task.FromResult(new WorldStallResponse { Ok = false, Reason = "stall_missing" });
        }

        var ok = stallStalls.Close(stallId);
        Console.WriteLine("world_stall_close stall=" + stallId
            + " ok=" + (ok ? "true" : "false")
            + " count=" + stallStalls.Count);

        return Task.FromResult(new WorldStallResponse
        {
            Ok = ok,
            Reason = ok ? "" : "stall_not_found",
            StallId = stallId,
            Count = (uint)stallStalls.Count,
        });
    }

    public override Task<ListStallsResponse> ListStalls(
        ListStallsRequest request, ServerCallContext context)
    {
        var regionId = (request.RegionId ?? "").Trim();
        var stalls = stallStalls.List(regionId);
        var response = new ListStallsResponse { Count = (uint)stalls.Count };
        foreach (var entry in stalls)
        {
            response.Stalls.Add(new StallInfo
            {
                StallId = entry.StallId,
                OwnerId = entry.OwnerId,
                OwnerName = entry.OwnerName,
                RegionId = entry.RegionId,
                PosX = entry.PosX,
                PosY = entry.PosY,
                Title = entry.Title,
                LiveListings = entry.LiveListings,
            });
        }

        Console.WriteLine("world_stalls region=" + (regionId.Length == 0 ? "all" : regionId)
            + " count=" + stalls.Count);
        return Task.FromResult(response);
    }
}
