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
    IngotListingRepository ingots,
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

    // P1-5b：元宝寄售挂单。元宝没有背包格位，只带卖方角色、元宝数量与金币总价；
    // 归属沿用角色服的只读背包口复核，元宝是否够在成交时由角色服判定。
    public override async Task<ListIngotResponse> ListIngot(
        ListIngotRequest request, ServerCallContext context)
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

        if (request.IngotAmount < 1 || request.PriceGold < 1)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "listing_invalid"));
        }

        if (!await OwnsAsync(character, account, request.CharacterId, context))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        IngotListingRepository.Created created;
        try
        {
            created = await ingots.CreateAsync(
                (long)request.CharacterId, request.IngotAmount, request.PriceGold,
                txnId, context.CancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 同一笔挂单请求重放：list_txn 已经存在。
            return new ListIngotResponse { Ok = false, Reason = "duplicate", Duplicate = true };
        }

        logger.LogInformation(
            "ingot_list account={Account} seller={Seller} ingot={Ingot} price={Price} txn={Txn} duplicate={Duplicate}",
            account, request.CharacterId, request.IngotAmount, request.PriceGold,
            txnId, created.Duplicate);

        if (created.Duplicate)
        {
            return new ListIngotResponse { Ok = false, Reason = "duplicate", Duplicate = true };
        }

        return new ListIngotResponse
        {
            Ok = true,
            Reason = "",
            ListingId = (ulong)created.ListingId,
            SellerId = request.CharacterId,
            IngotAmount = request.IngotAmount,
            PriceGold = request.PriceGold,
            State = "listed",
        };
    }

    // P1-5b：买元宝。成交撮合转给角色服：元宝、金币、税率与到账都在那一笔事务里。
    public override async Task<BuyIngotResponse> BuyIngot(
        BuyIngotRequest request, ServerCallContext context)
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

        var settled = await character.SettleIngotAsync(
            new AuctionIngotSettleRequest
            {
                Account = account,
                CharacterId = request.CharacterId,
                ListingId = request.ListingId,
                TxnId = txnId,
            },
            deadline: DateTime.UtcNow.AddSeconds(5),
            cancellationToken: context.CancellationToken);

        logger.LogInformation(
            "ingot_buy account={Account} buyer={Buyer} listing={Listing} txn={Txn} "
            + "ok={Ok} reason={Reason} ingot={Ingot} price={Price} tax={Tax} net={Net}",
            account, request.CharacterId, request.ListingId, txnId,
            settled.Ok, settled.Reason, settled.IngotAmount, settled.PriceGold,
            settled.TaxGold, settled.NetGold);

        return new BuyIngotResponse
        {
            Ok = settled.Ok,
            Reason = settled.Reason,
            ListingId = settled.ListingId,
            IngotAmount = settled.IngotAmount,
            PriceGold = settled.PriceGold,
            TaxGold = settled.TaxGold,
            NetGold = settled.NetGold,
            BuyerGold = settled.BuyerGold,
            SellerGold = settled.SellerGold,
            BuyerIngot = settled.BuyerIngot,
            SellerIngot = settled.SellerIngot,
            TaxRatePct = settled.TaxRatePct,
            State = settled.State,
            Duplicate = settled.Duplicate,
        };
    }

    // 元宝寄售没有格位可查 CanTrade，用只读背包口复核归属：
    // 不是本人的角色返回 NotFound，转成 character_not_found。
    static async Task<bool> OwnsAsync(
        CharacterService.CharacterServiceClient client,
        string account, ulong characterId, ServerCallContext context)
    {
        try
        {
            await client.ListInventoryAsync(
                new ListInventoryRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: context.CancellationToken);
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }
}
