using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

    /// <summary>
    /// 货币加减。金币、灵石五档、元宝都走这里：扣成负数直接拒绝，
    /// 不做「扣到 0 为止」那种夹取——夹住会让调用方以为扣成功了。
    /// </summary>
    public override async Task<CurrencyBalance> AddCurrency(
        AddCurrencyRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var kind = (request.Kind ?? "").Trim();
        if (currencies.Find(kind) is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown_currency"));
        }

        if (request.Delta == 0)
        {
            // 0 不改变任何东西，但也别写一行「0 元宝」出来。
            throw new RpcException(new Status(StatusCode.InvalidArgument, "delta_zero"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var amount = await currency.AddAsync(characterId, kind, request.Delta, context.CancellationToken);
        if (amount is null)
        {
            logger.LogInformation(
                "currency_reject account={Account} id={Id} kind={Kind} delta={Delta} reason=insufficient",
                account, characterId, kind, request.Delta);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "insufficient"));
        }

        logger.LogInformation(
            "currency account={Account} id={Id} kind={Kind} delta={Delta} amount={Amount}",
            account, characterId, kind, request.Delta, amount.Value);

        return new CurrencyBalance { Kind = kind, Amount = amount.Value };
    }


    public override async Task<GetCurrencyResponse> GetCurrency(
        GetCurrencyRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var balances = await currency.AllAsync(characterId, context.CancellationToken);

        // 按配置顺序全部返回，没记过账的按 0。
        var response = new GetCurrencyResponse();
        foreach (var def in currencies.All)
        {
            response.Balances.Add(new CurrencyBalance
            {
                Kind = def.Id,
                Amount = balances.GetValueOrDefault(def.Id, 0L),
            });
        }

        return response;
    }


    /// <summary>
    /// 发一笔奖。整笔在一个数据库事务里：事务号重复就整笔不做（duplicate），
    /// 中间任何一条不满足条件就整笔回退，不会出现「钱扣了东西没给」。
    /// </summary>
    public override async Task<ApplyGrantResponse> ApplyGrant(
        ApplyGrantRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_id_invalid"));
        }

        if (request.Entries.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "entries_empty"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        GrantResult result;
        try
        {
            result = await grants.ApplyAsync(
                characterId, txnId, request.Entries, context.CancellationToken);
        }
        catch (GrantException ex)
        {
            logger.LogInformation(
                "grant_reject account={Account} id={Id} txn={Txn} code={Code}",
                account, characterId, txnId, ex.Code);

            // 业务前提不满足（钱不够、格子占着）走 FailedPrecondition，与参数错分开。
            var status = ex.Code is "insufficient" or "stack_overflow" or "slot_occupied"
                ? StatusCode.FailedPrecondition
                : StatusCode.InvalidArgument;
            throw new RpcException(new Status(status, ex.Code));
        }

        logger.LogInformation(
            "grant account={Account} id={Id} txn={Txn} duplicate={Duplicate} entries={Entries}",
            account, characterId, txnId, result.Duplicate, request.Entries.Count);

        var response = new ApplyGrantResponse { Duplicate = result.Duplicate };
        foreach (var def in currencies.All)
        {
            response.Balances.Add(new CurrencyBalance
            {
                Kind = def.Id,
                Amount = result.Balances.GetValueOrDefault(def.Id, 0L),
            });
        }

        foreach (var slot in result.Slots)
        {
            response.Slots.Add(new InventorySlot
            {
                Slot = (uint)slot.Slot,
                ItemId = slot.ItemId,
                Count = slot.Count,
                Bound = slot.Bound,
            });
        }

        return response;
    }


    /// <summary>
    /// 拍卖成交结算（P1-5a）。全局服务转过来时只带买方角色、挂单号与事务号；
    /// 标价、卖方、物品、税率与到账全由 AuctionRepository 从库和配置读。
    /// 业务前提不满足是 ok=false + reason，不是 gRPC 错误码。
    /// </summary>
    public override async Task<AuctionSettleResponse> SettleAuction(
        AuctionSettleRequest request, ServerCallContext context)
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

        if (!await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await auctions.SettleAsync(
            (long)request.CharacterId, (long)request.ListingId, txnId, context.CancellationToken);

        logger.LogInformation(
            "auction_settle account={Account} buyer={Buyer} listing={Listing} txn={Txn} "
            + "ok={Ok} reason={Reason} price={Price} tax={Tax} net={Net} duplicate={Duplicate}",
            account, request.CharacterId, request.ListingId, txnId, outcome.Ok,
            outcome.Reason, outcome.PriceGold, outcome.TaxGold, outcome.NetGold,
            outcome.Duplicate);

        if (!outcome.Ok)
        {
            // 拒绝与重放都不带「这一手生效了」的数值；税率只是配置里的展示值。
            return new AuctionSettleResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                ListingId = (ulong)outcome.ListingId,
                // B5：摆摊挂单按 stall.json 的税率回；拍卖仍按 auction.json。
                TaxRatePct = outcome.TaxRatePct > 0 ? outcome.TaxRatePct : auctionTable.TaxRatePct,
                StallId = outcome.StallId,
                LiveListings = outcome.LiveListings,
                Duplicate = outcome.Duplicate,
            };
        }

        return new AuctionSettleResponse
        {
            Ok = true,
            Reason = "",
            ListingId = (ulong)outcome.ListingId,
            PriceGold = outcome.PriceGold,
            TaxGold = outcome.TaxGold,
            NetGold = outcome.NetGold,
            BuyerGold = outcome.BuyerGold,
            SellerGold = outcome.SellerGold,
            TaxRatePct = outcome.TaxRatePct > 0 ? outcome.TaxRatePct : auctionTable.TaxRatePct,
            ItemId = outcome.ItemId,
            Count = (uint)outcome.Count,
            State = outcome.State,
            StallId = outcome.StallId,
            LiveListings = outcome.LiveListings,
        };
    }


    /// <summary>
    /// 元宝寄售成交结算（P1-5b）。全局服务转过来时只带买方角色、挂单号与事务号；
    /// 元宝数量、标价、卖方、税率与到账全由 IngotRepository 从库和配置读。
    /// 业务前提不满足是 ok=false + reason，不是 gRPC 错误码。
    /// </summary>
    public override async Task<AuctionIngotSettleResponse> SettleIngot(
        AuctionIngotSettleRequest request, ServerCallContext context)
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

        if (!await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await ingots.SettleAsync(
            (long)request.CharacterId, (long)request.ListingId, txnId, context.CancellationToken);

        logger.LogInformation(
            "ingot_settle account={Account} buyer={Buyer} listing={Listing} txn={Txn} "
            + "ok={Ok} reason={Reason} ingot={Ingot} price={Price} tax={Tax} net={Net} duplicate={Duplicate}",
            account, request.CharacterId, request.ListingId, txnId, outcome.Ok,
            outcome.Reason, outcome.IngotAmount, outcome.PriceGold, outcome.TaxGold,
            outcome.NetGold, outcome.Duplicate);

        if (!outcome.Ok)
        {
            // 拒绝与重放都不带「这一手生效了」的数值；税率只是配置里的展示值。
            return new AuctionIngotSettleResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                ListingId = (ulong)outcome.ListingId,
                TaxRatePct = auctionTable.TaxRatePct,
                Duplicate = outcome.Duplicate,
            };
        }

        return new AuctionIngotSettleResponse
        {
            Ok = true,
            Reason = "",
            ListingId = (ulong)outcome.ListingId,
            IngotAmount = outcome.IngotAmount,
            PriceGold = outcome.PriceGold,
            TaxGold = outcome.TaxGold,
            NetGold = outcome.NetGold,
            BuyerGold = outcome.BuyerGold,
            SellerGold = outcome.SellerGold,
            BuyerIngot = outcome.BuyerIngot,
            SellerIngot = outcome.SellerIngot,
            TaxRatePct = auctionTable.TaxRatePct,
            State = outcome.State,
        };
    }


    /// <summary>
    /// P3-2a：灵石相邻档兑换。请求只带角色、低档币种与事务号；档位顺序、比例与
    /// 产出全在 StoneExchangeRepository 按配置判定，不经过拍卖。归属/账号校验沿用
    /// AddCurrency 的写法，业务前提不满足回 ok=false + reason。
    /// </summary>
    public override async Task<ExchangeStoneResponse> ExchangeStone(
        ExchangeStoneRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        var fromKind = (request.FromKind ?? "").Trim();
        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await stoneExchanges.ExchangeAsync(
            characterId, fromKind, txnId, context.CancellationToken);

        logger.LogInformation(
            "stone_exchange account={Account} id={Id} from={From} txn={Txn} "
            + "ok={Ok} reason={Reason} to={To} from_cost={FromCost} to_gain={ToGain} "
            + "from_amount={FromAmount} to_amount={ToAmount} duplicate={Duplicate}",
            account, characterId, fromKind, txnId, outcome.Ok, outcome.Reason,
            outcome.ToKind, outcome.FromCost, outcome.ToGain,
            outcome.FromAmount, outcome.ToAmount, outcome.Duplicate);

        // 逐字段把仓储的事实值填进回包；拒绝时不带任何「这一手生效了」的数值。
        return new ExchangeStoneResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            FromKind = outcome.FromKind,
            ToKind = outcome.ToKind,
            FromCost = outcome.FromCost,
            ToGain = outcome.ToGain,
            FromAmount = outcome.FromAmount,
            ToAmount = outcome.ToAmount,
            Ratio = outcome.Ratio,
            HighestName = outcome.HighestName,
            Duplicate = outcome.Duplicate,
        };
    }


    /// <summary>
    /// P3-2a：任务发放的灵石成交结算。全局服务转过来时只带买方角色、挂单号与
    /// 事务号；档位、数量、标价、税率与税额全由 StoneAuctionRepository 从库和
    /// 配置读。税仍直接销毁，不进任何角色。业务前提不满足是 ok=false + reason。
    /// </summary>
    public override async Task<StoneAuctionSettleResponse> SettleStoneAuction(
        StoneAuctionSettleRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0 || request.ListingId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await stoneAuctions.SettleAsync(
            characterId, (long)request.ListingId, txnId, context.CancellationToken);

        logger.LogInformation(
            "stone_auction_settle account={Account} buyer={Buyer} listing={Listing} txn={Txn} "
            + "ok={Ok} reason={Reason} kind={Kind} amount={Amount} price={Price} "
            + "tax={Tax} net={Net} duplicate={Duplicate}",
            account, characterId, request.ListingId, txnId, outcome.Ok, outcome.Reason,
            outcome.StoneKind, outcome.StoneAmount, outcome.PriceGold,
            outcome.TaxGold, outcome.NetGold, outcome.Duplicate);

        // outcome 与回包一一对应，逐字段填进去。
        return new StoneAuctionSettleResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            ListingId = (ulong)outcome.ListingId,
            StoneKind = outcome.StoneKind,
            StoneAmount = outcome.StoneAmount,
            PriceGold = outcome.PriceGold,
            TaxGold = outcome.TaxGold,
            NetGold = outcome.NetGold,
            BuyerGold = outcome.BuyerGold,
            SellerGold = outcome.SellerGold,
            BuyerStone = outcome.BuyerStone,
            SellerStone = outcome.SellerStone,
            TaxRatePct = outcome.TaxRatePct,
            State = outcome.State,
            Duplicate = outcome.Duplicate,
        };
    }


    /// <summary>
    /// P1-5d：建一张内部充值订单。P0 不接真实支付渠道，订单与结算都在本服务。
    /// 客户端只带商品编号，元宝数由配置决定；订单号由服务端生成，客户端带不进来。
    /// </summary>
    public override async Task<CreateRechargeOrderResponse> CreateRechargeOrder(
        CreateRechargeOrderRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var productId = (request.ProductId ?? "").Trim();
        if (request.CharacterId == 0 || productId.Length == 0 || account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var product = rechargeTable.Find(productId);
        if (product is null)
        {
            logger.LogInformation(
                "recharge_create_reject account={Account} id={Id} product={Product} reason=product_not_found",
                account, characterId, productId);
            return new CreateRechargeOrderResponse
            {
                Ok = false,
                Reason = "product_not_found",
                ProductId = productId,
                Status = "",
            };
        }

        // 订单号在服务端生成：回调按这个号认，客户端改不了它。
        var orderId = "rc_" + Guid.NewGuid().ToString("N");
        if (!await recharges.CreateAsync(
                orderId, characterId, product.Id, product.Yuanbao, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "recharge_create account={Account} id={Id} order={Order} product={Product} yuanbao={Yuanbao}",
            account, characterId, orderId, product.Id, product.Yuanbao);

        return new CreateRechargeOrderResponse
        {
            Ok = true,
            Reason = "",
            OrderId = orderId,
            ProductId = product.Id,
            Yuanbao = product.Yuanbao,
            Status = "pending",
        };
    }


    /// <summary>
    /// P1-5d：结算一笔内部充值订单（P0 用它模拟支付渠道回调）。同一笔重复调用只发一次；
    /// 第二次回 duplicate，元宝余额不变。
    /// </summary>
    public override async Task<SettleRechargeOrderResponse> SettleRechargeOrder(
        SettleRechargeOrderRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var orderId = (request.OrderId ?? "").Trim();
        if (orderId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        var outcome = await recharges.SettleAsync(
            orderId, account.Length > 0 ? account : null, context.CancellationToken);

        if (!outcome.Found)
        {
            // 不存在与「不是你的订单」都回同一个 order_not_found，不泄露别人的订单。
            logger.LogInformation(
                "recharge_settle_reject account={Account} order={Order} reason=order_not_found",
                account, orderId);
            return new SettleRechargeOrderResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                OrderId = orderId,
                Status = "",
            };
        }

        if (outcome.Reason.Length > 0)
        {
            logger.LogInformation(
                "recharge_settle_reject account={Account} order={Order} reason={Reason}",
                account, orderId, outcome.Reason);
            return new SettleRechargeOrderResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                OrderId = orderId,
                Status = outcome.Status,
                Yuanbao = outcome.Yuanbao,
                Balance = outcome.Balance,
                Duplicate = outcome.Duplicate,
            };
        }

        logger.LogInformation(
            "recharge_settle account={Account} order={Order} yuanbao={Yuanbao} balance={Balance}",
            account, orderId, outcome.Yuanbao, outcome.Balance);

        return new SettleRechargeOrderResponse
        {
            Ok = true,
            Reason = "",
            OrderId = orderId,
            Status = "paid",
            Yuanbao = outcome.Yuanbao,
            Balance = outcome.Balance,
            PaidAtMs = outcome.PaidAtMs,
            Duplicate = false,
        };
    }
}
