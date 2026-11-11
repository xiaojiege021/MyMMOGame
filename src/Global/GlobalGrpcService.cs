using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Trade;
using Npgsql;

namespace Global;

/// <summary>
/// 拍卖行对外入口。挂单登记与查询在全局服务，成交撮合转给角色服：
/// 税率、税额、标价、卖方、物品与到账都在角色服从库和配置读，
/// 客户端和全局服务都带不进这些数值。
/// </summary>
public sealed class GlobalGrpcService(
    AuctionListingRepository listings,
    CharacterService.CharacterServiceClient character,
    ILogger<GlobalGrpcService> logger) : TradeService.TradeServiceBase
{
    public override async Task<ListAuctionResponse> ListAuction(
        ListAuctionRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
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

        if (request.Slot < 1 || request.Slot > 200 || request.Count < 1 || request.PriceGold < 1)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "listing_invalid"));
        }

        // 复核归属与绑定：沿用 P1-1b3 的 CanTrade，不为拍卖另立一套身份数据。
        var check = await character.CanTradeAsync(
            new CanTradeRequest
            {
                Account = account,
                CharacterId = request.CharacterId,
                Slot = request.Slot,
            },
            deadline: DateTime.UtcNow.AddSeconds(5),
            cancellationToken: context.CancellationToken);

        if (!check.Tradable)
        {
            return new ListAuctionResponse { Ok = false, Reason = check.Reason };
        }

        AuctionListingRepository.Created created;
        try
        {
            created = await listings.CreateAsync(
                (long)request.CharacterId, (int)request.Slot, (int)request.Count,
                request.PriceGold, txnId, context.CancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 同一卖方的同一格已经挂着一单。
            return new ListAuctionResponse { Ok = false, Reason = "slot_listed" };
        }

        logger.LogInformation(
            "auction_list account={Account} seller={Seller} slot={Slot} count={Count} "
            + "price={Price} txn={Txn} duplicate={Duplicate}",
            account, request.CharacterId, request.Slot, request.Count,
            request.PriceGold, txnId, created.Duplicate);

        if (created.Duplicate)
        {
            return new ListAuctionResponse { Ok = false, Reason = "duplicate", Duplicate = true };
        }

        return new ListAuctionResponse
        {
            Ok = true,
            Reason = "",
            ListingId = (ulong)created.ListingId,
            SellerId = request.CharacterId,
            Slot = request.Slot,
            Count = request.Count,
            PriceGold = request.PriceGold,
            State = "listed",
        };
    }

    public override async Task<BuyAuctionResponse> BuyAuction(
        BuyAuctionRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0 || request.ListingId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        if (txnId.Length == 0 || txnId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        var settled = await character.SettleAuctionAsync(
            new AuctionSettleRequest
            {
                Account = account,
                CharacterId = request.CharacterId,
                ListingId = request.ListingId,
                TxnId = txnId,
            },
            deadline: DateTime.UtcNow.AddSeconds(5),
            cancellationToken: context.CancellationToken);

        logger.LogInformation(
            "auction_buy account={Account} buyer={Buyer} listing={Listing} txn={Txn} "
            + "ok={Ok} reason={Reason} price={Price} tax={Tax} net={Net}",
            account, request.CharacterId, request.ListingId, txnId,
            settled.Ok, settled.Reason, settled.PriceGold, settled.TaxGold, settled.NetGold);

        return new BuyAuctionResponse
        {
            Ok = settled.Ok,
            Reason = settled.Reason,
            ListingId = settled.ListingId,
            PriceGold = settled.PriceGold,
            TaxGold = settled.TaxGold,
            NetGold = settled.NetGold,
            BuyerGold = settled.BuyerGold,
            SellerGold = settled.SellerGold,
            TaxRatePct = settled.TaxRatePct,
            ItemId = settled.ItemId,
            Count = settled.Count,
            State = settled.State,
            Duplicate = settled.Duplicate,
        };
    }
}
