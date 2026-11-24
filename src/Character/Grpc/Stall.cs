using Grpc.Core;
using Mmo.Proto.Character;

namespace Character;

/// <summary>
/// B5 摆摊的四条口：开摊 / 关摊 / 上架 / 看摊。成交不在这里——它仍走
/// SettleAuction（同一笔角色服事务），税按挂单的 channel 取配置。
/// 摊位在世界服显示，世界服读不到角色库，只拿这四条回包里的摘要。
/// </summary>
public sealed partial class CharacterGrpcService
{
    public override async Task<OpenStallResponse> OpenStall(
        OpenStallRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        var stallId = (request.StallId ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        if (txnId.Length == 0 || txnId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        if (stallId.Length == 0 || stallId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "stall_missing"));
        }

        if (!await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await stalls.OpenAsync(
            (long)request.CharacterId, request.RegionId, request.PosX, request.PosY,
            request.Title, stallId, context.CancellationToken);

        logger.LogInformation(
            "stall_open account={Account} id={Id} stall={Stall} region={Region} txn={Txn} "
            + "ok={Ok} reason={Reason}",
            account, request.CharacterId, stallId, request.RegionId, txnId,
            outcome.Ok, outcome.Reason);

        return new OpenStallResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            StallId = outcome.StallId,
            OwnerName = outcome.OwnerName,
            RegionId = outcome.RegionId,
            PosX = outcome.PosX,
            PosY = outcome.PosY,
            Title = outcome.Title,
            LiveListings = outcome.LiveListings,
            Slots = outcome.Slots,
            MinLevel = outcome.MinLevel,
            TaxRatePct = outcome.TaxRatePct,
        };
    }

    public override async Task<CloseStallResponse> CloseStall(
        CloseStallRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        var stallId = (request.StallId ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0 || stallId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "stall_missing"));
        }

        if (txnId.Length == 0 || txnId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        if (!await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await stalls.CloseAsync(
            (long)request.CharacterId, stallId, context.CancellationToken);

        logger.LogInformation(
            "stall_close account={Account} id={Id} stall={Stall} txn={Txn} ok={Ok} "
            + "reason={Reason} cancelled={Cancelled}",
            account, request.CharacterId, stallId, txnId, outcome.Ok,
            outcome.Reason, outcome.CancelledListings);

        return new CloseStallResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            StallId = outcome.StallId,
            CancelledListings = outcome.CancelledListings,
        };
    }

    public override async Task<ListStallItemResponse> ListStallItem(
        ListStallItemRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        var stallId = (request.StallId ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0 || stallId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "stall_missing"));
        }

        if (txnId.Length == 0 || txnId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        if (!await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await stalls.ListAsync(
            (long)request.CharacterId, stallId, (int)request.Slot, (int)request.Count,
            request.PriceGold, txnId, context.CancellationToken);

        logger.LogInformation(
            "stall_list account={Account} id={Id} stall={Stall} slot={Slot} count={Count} "
            + "price={Price} txn={Txn} ok={Ok} reason={Reason} listing={Listing} live={Live}",
            account, request.CharacterId, stallId, request.Slot, request.Count,
            request.PriceGold, txnId, outcome.Ok, outcome.Reason, outcome.ListingId,
            outcome.LiveListings);

        return new ListStallItemResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            ListingId = (ulong)outcome.ListingId,
            StallId = outcome.StallId,
            Slot = (uint)outcome.Slot,
            ItemId = outcome.ItemId,
            Count = (uint)outcome.Count,
            PriceGold = outcome.PriceGold,
            LiveListings = outcome.LiveListings,
            Slots = outcome.Slots,
        };
    }

    public override async Task<ViewStallResponse> ViewStall(
        ViewStallRequest request, ServerCallContext context)
    {
        var stallId = (request.StallId ?? "").Trim();
        if (stallId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "stall_missing"));
        }

        var snapshot = await stalls.ViewAsync(stallId, context.CancellationToken);
        var response = new ViewStallResponse
        {
            Found = snapshot.Found,
            StallId = snapshot.StallId,
            OwnerId = (ulong)snapshot.OwnerId,
            OwnerName = snapshot.OwnerName,
            RegionId = snapshot.RegionId,
            PosX = snapshot.PosX,
            PosY = snapshot.PosY,
            Title = snapshot.Title,
            State = snapshot.State,
            LiveListings = snapshot.LiveListings,
            Slots = snapshot.Slots,
        };

        foreach (var row in snapshot.Listings)
        {
            response.Listings.Add(new StallListingFact
            {
                ListingId = (ulong)row.ListingId,
                Slot = (uint)row.Slot,
                ItemId = row.ItemId,
                ItemName = row.ItemName,
                Count = (uint)row.Count,
                PriceGold = row.PriceGold,
                Bound = row.Bound,
            });
        }

        logger.LogInformation(
            "stall_view stall={Stall} found={Found} live={Live} state={State}",
            stallId, snapshot.Found, snapshot.LiveListings, snapshot.State);

        return response;
    }

    // 摊位税率与格数也放在这一处回给核对脚本；改配置重启生效。
    public StallTable Stalls => stallTable;
}
