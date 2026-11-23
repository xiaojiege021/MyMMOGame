using Grpc.Core;
using Mmo.Proto.Social;

namespace Social;

public sealed partial class SocialGrpcService
{
    // ---- C3：洞府摆放（只加内容和配置，不改上面任何一条宗门规则） ----
    // 三个 RPC 都只回答「谁的哪一格摆了哪一件」这份展示事实：不消耗背包、
    // 不改角色库、不发家园币、不加属性、不结算技能。业务前提是正常回包里的
    // ok=false + reason，不是 gRPC 错误码；身份与参数错由网关在 HTTP 层挡。
    public override async Task<HomesteadResponse> PlaceHomeDecor(
        HomesteadRequest request, ServerCallContext context)
    {
        // proto 的 string 在生成物里可空，先收成局部串再判空，避免把 null 传进下游。
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        var slotId = (request.SlotId ?? "").Trim();
        var itemId = (request.ItemId ?? "").Trim();

        if (homes.Table is null || homes.Repository is null)
        {
            return HomesteadFail("homestead_unavailable", request.CharacterId,
                slotId, itemId, txnId);
        }

        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || slotId.Length == 0 || itemId.Length == 0)
        {
            return HomesteadFail("request_missing", request.CharacterId,
                slotId, itemId, txnId);
        }

        var outcome = await homes.Repository.PlaceAsync(
            account, request.CharacterId, txnId, slotId, itemId,
            context.CancellationToken);
        return HomesteadBody(outcome);
    }

    public override async Task<HomesteadResponse> RemoveHomeDecor(
        HomesteadRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        var slotId = (request.SlotId ?? "").Trim();

        if (homes.Table is null || homes.Repository is null)
        {
            return HomesteadFail("homestead_unavailable", request.CharacterId,
                slotId, "", txnId);
        }

        if (request.CharacterId == 0 || txnId.Length == 0 || slotId.Length == 0)
        {
            return HomesteadFail("request_missing", request.CharacterId,
                slotId, "", txnId);
        }

        var outcome = await homes.Repository.RemoveAsync(
            request.CharacterId, txnId, slotId, context.CancellationToken);
        return HomesteadBody(outcome);
    }

    public override async Task<HomesteadResponse> DescribeHomestead(
        DescribeHomesteadRequest request, ServerCallContext context)
    {
        if (homes.Table is null || homes.Repository is null)
        {
            return HomesteadFail("homestead_unavailable", request.OwnerCharacterId,
                "", "", "");
        }

        if (request.OwnerCharacterId == 0)
        {
            return HomesteadFail("request_missing", 0, "", "", "");
        }

        // 访客可见：任何有效令牌（网关认过）都能按洞府主人角色号只读查看。
        // account 只作日志上下文，不参与判定；回包不改任何状态。
        var rows = await homes.Repository.DescribeAsync(
            (long)request.OwnerCharacterId, context.CancellationToken);
        return HomesteadBody(new HomesteadOutcome(true, "", false,
            (long)request.OwnerCharacterId, "", "", "", rows));
    }

    HomesteadResponse HomesteadBody(HomesteadOutcome o)
    {
        var response = new HomesteadResponse
        {
            Ok = o.Ok,
            Reason = o.Reason,
            Duplicate = o.Duplicate,
            CharacterId = (ulong)o.CharacterId,
            SlotId = o.SlotId,
            ItemId = o.ItemId,
            TxnId = o.TxnId,
            SlotCount = homes.Table?.Slots.Count ?? 0,
        };
        foreach (var placement in o.Placements)
        {
            response.Placements.Add(new HomesteadSlotFact
            {
                SlotId = placement.SlotId,
                ItemId = placement.ItemId,
            });
        }

        return response;
    }

    static HomesteadResponse HomesteadFail(
        string reason, ulong characterId, string slotId, string itemId, string txnId) =>
        new()
        {
            Ok = false,
            Reason = reason,
            CharacterId = characterId,
            SlotId = slotId ?? "",
            ItemId = itemId ?? "",
            TxnId = txnId ?? "",
        };

    public override async Task<HomesteadPlantResponse> PlantHomesteadCrop(
        HomesteadPlantRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        var slotId = (request.SlotId ?? "").Trim();
        var cropId = (request.CropId ?? "").Trim();

        if (plants.Table is null || plants.Repository is null)
        {
            return PlantFail("plant_unavailable", request.CharacterId, slotId, cropId, txnId);
        }

        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || slotId.Length == 0 || cropId.Length == 0)
        {
            return PlantFail("request_missing", request.CharacterId, slotId, cropId, txnId);
        }

        var outcome = await plants.Repository.PlantAsync(
            account, request.CharacterId, txnId, slotId, cropId, context.CancellationToken);
        return PlantBody(outcome);
    }

    public override async Task<HomesteadPlantResponse> HarvestHomesteadCrop(
        HomesteadPlantRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        var slotId = (request.SlotId ?? "").Trim();

        if (plants.Table is null || plants.Repository is null)
        {
            return PlantFail("plant_unavailable", request.CharacterId, slotId, "", txnId);
        }

        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || slotId.Length == 0)
        {
            return PlantFail("request_missing", request.CharacterId, slotId, "", txnId);
        }

        var outcome = await plants.Repository.HarvestAsync(
            account, request.CharacterId, txnId, slotId, context.CancellationToken);
        return PlantBody(outcome);
    }

    public override async Task<HomesteadPlantResponse> DescribeHomesteadPlants(
        DescribeHomesteadPlantsRequest request, ServerCallContext context)
    {
        if (plants.Table is null || plants.Repository is null)
        {
            return PlantFail("plant_unavailable", request.OwnerCharacterId, "", "", "");
        }

        if (request.OwnerCharacterId == 0)
        {
            return PlantFail("request_missing", 0, "", "", "");
        }

        var rows = await plants.Repository.DescribeAsync(
            (long)request.OwnerCharacterId, context.CancellationToken);
        return PlantBody(new HomesteadPlantOutcome(true, "", false,
            (long)request.OwnerCharacterId, "", "", "", rows, 0, 0));
    }

    HomesteadPlantResponse PlantBody(HomesteadPlantOutcome o)
    {
        var response = new HomesteadPlantResponse
        {
            Ok = o.Ok,
            Reason = o.Reason,
            Duplicate = o.Duplicate,
            CharacterId = (ulong)o.CharacterId,
            SlotId = o.SlotId,
            CropId = o.CropId,
            TxnId = o.TxnId,
            SlotCount = plants.Table?.Slots.Count ?? 0,
            GrantedHomeCoin = o.GrantedHomeCoin,
            HomeCoinBalance = o.HomeCoinBalance,
        };

        foreach (var row in o.Plants)
        {
            var def = plants.Table?.FindCrop(row.CropId);
            var ready = def is null
                ? row.PlantedAt.ToUnixTimeSeconds()
                : row.PlantedAt.AddSeconds(def.GrowSeconds).ToUnixTimeSeconds();
            response.Plants.Add(new HomesteadPlantFact
            {
                SlotId = row.SlotId,
                CropId = row.CropId,
                PlantedAtUnix = row.PlantedAt.ToUnixTimeSeconds(),
                ReadyAtUnix = ready,
                Mature = row.Mature,
            });
        }

        return response;
    }

    static HomesteadPlantResponse PlantFail(
        string reason, ulong characterId, string slotId, string cropId, string txnId) =>
        new()
        {
            Ok = false,
            Reason = reason,
            CharacterId = characterId,
            SlotId = slotId ?? "",
            CropId = cropId ?? "",
            TxnId = txnId ?? "",
        };
}
