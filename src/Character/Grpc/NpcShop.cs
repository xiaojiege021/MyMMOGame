using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

    // ------------------------------------------------------------------ K2+Q13 NPC 商店

    /// <summary>
    /// 面板要的全部事实：货架（今日还剩多少 / 今天买了几件）、回收白名单、回购窗口、
    /// 修理单价与身上三件的耐久。一个数都不在这里算，全从配置与库里读。
    /// </summary>
    public override async Task<DescribeNpcShopResponse> DescribeNpcShop(
        DescribeNpcShopRequest request,
        ServerCallContext context)
    {
        var characterId = (long)request.CharacterId;
        if (characterId == 0 || !await inventory.ExistsAsync(characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        try
        {
            var view = await npcShops.DescribeAsync(
                characterId, (request.ShopId ?? "").Trim(), context.CancellationToken);

            var response = new DescribeNpcShopResponse
            {
                Ok = true,
                ShopId = view.ShopId,
                NpcId = view.NpcId,
                Currency = view.Currency,
                Refresh = view.Refresh,
                Balance = view.Balance,
                SellBalance = view.SellBalance,
                RepairPricePerPoint = view.RepairPricePerPoint,
                RepairCurrency = view.RepairCurrency,
            };

            foreach (var g in view.Goods)
            {
                response.Goods.Add(new NpcShopGoods
                {
                    ItemId = g.ItemId,
                    Name = g.Name,
                    Price = g.Price,
                    StockLeft = g.StockLeft,
                    Unlimited = g.Unlimited,
                    BoughtToday = g.BoughtToday,
                    LimitPerCharacter = g.LimitPerCharacter,
                    Enough = g.Enough,
                    SoldOut = g.SoldOut,
                    LimitReached = g.LimitReached,
                });
            }

            foreach (var line in view.Sell)
            {
                response.Sell.Add(new NpcShopSellLine
                {
                    ItemId = line.ItemId,
                    Name = line.Name,
                    Price = line.Price,
                });
            }

            foreach (var row in view.Buyback)
            {
                response.Buyback.Add(new NpcShopBuybackRow
                {
                    Id = row.Id,
                    ItemId = row.ItemId,
                    Name = row.Name,
                    Count = row.Count,
                    UnitPrice = row.UnitPrice,
                    SoldAtMs = row.SoldAt.ToUnixTimeMilliseconds(),
                });
            }

            foreach (var row in view.Worn)
            {
                response.Worn.Add(new NpcShopWornRow
                {
                    Slot = row.Slot,
                    ItemId = row.ItemId,
                    Name = row.Name,
                    Durability = row.Durability,
                    DurabilityMax = row.Max,
                });
            }

            return response;
        }
        catch (NpcShopException ex)
        {
            logger.LogInformation(
                "npc_shop_describe_reject id={Id} shop={Shop} reason={Reason}",
                characterId, request.ShopId, ex.Code);
            return new DescribeNpcShopResponse
            {
                Ok = false,
                Reason = ex.Code,
                ShopId = (request.ShopId ?? "").Trim(),
            };
        }
    }


    /// <summary>买：扣币、入包、库存与限购三件事在同一笔事务里，拒绝时三处都不动。</summary>
    public override async Task<BuyNpcShopItemResponse> BuyNpcShopItem(
        BuyNpcShopItemRequest request,
        ServerCallContext context)
    {
        var characterId = (long)request.CharacterId;
        var shopId = (request.ShopId ?? "").Trim();
        var itemId = (request.ItemId ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();

        if (characterId == 0 || !await inventory.ExistsAsync(characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        if (txnId.Length == 0 || txnId.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        try
        {
            var facts = await npcShops.BuyAsync(
                characterId, shopId, itemId, txnId, context.CancellationToken);

            logger.LogInformation(
                "npc_shop_buy id={Id} shop={Shop} item={Item} txn={Txn} price={Price}"
                + " balance={Balance} slot={Slot} duplicate={Dup}",
                characterId, facts.ShopId, facts.ItemId, txnId, facts.Price,
                facts.Balance, facts.BagSlot, facts.Duplicate);

            return new BuyNpcShopItemResponse
            {
                Ok = !facts.Duplicate,
                Reason = facts.Duplicate ? "duplicate" : "",
                ShopId = facts.ShopId,
                ItemId = facts.ItemId,
                Name = facts.Name,
                Currency = facts.Currency,
                Price = facts.Price,
                Balance = facts.Balance,
                StockLeft = facts.StockLeft,
                BoughtToday = facts.BoughtToday,
                LimitPerCharacter = facts.LimitPerCharacter,
                BagSlot = facts.Duplicate ? 0 : facts.BagSlot,
                Count = facts.Duplicate ? 0 : facts.Count,
                Bound = !facts.Duplicate && facts.Bound,
                Duplicate = facts.Duplicate,
            };
        }
        catch (NpcShopException ex)
        {
            logger.LogInformation(
                "npc_shop_buy_reject id={Id} shop={Shop} item={Item} reason={Reason}",
                characterId, shopId, itemId, ex.Code);
            return new BuyNpcShopItemResponse
            {
                Ok = false,
                Reason = ex.Code,
                ShopId = shopId,
                ItemId = itemId,
            };
        }
    }


    /// <summary>卖：白名单外的编号一律拒；删行与加币同一笔事务，拒绝时金币一分不动。</summary>
    public override async Task<SellNpcItemResponse> SellNpcItem(
        SellNpcItemRequest request,
        ServerCallContext context)
    {
        var characterId = (long)request.CharacterId;
        var shopId = (request.ShopId ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();

        if (characterId == 0 || !await inventory.ExistsAsync(characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        if (txnId.Length == 0 || txnId.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        try
        {
            var facts = await npcShops.SellAsync(characterId, shopId, request.BagSlot,
                request.Count, txnId, context.CancellationToken);

            logger.LogInformation(
                "npc_shop_sell id={Id} shop={Shop} item={Item} count={Count} gained={Gained}"
                + " balance={Balance} buyback={Buyback} duplicate={Dup}",
                characterId, facts.ShopId, facts.ItemId, facts.Count, facts.Gained,
                facts.Balance, facts.BuybackId, facts.Duplicate);

            return new SellNpcItemResponse
            {
                Ok = !facts.Duplicate,
                Reason = facts.Duplicate ? "duplicate" : "",
                ShopId = facts.ShopId,
                BuybackId = facts.BuybackId,
                ItemId = facts.ItemId,
                Name = facts.Name,
                Count = facts.Count,
                UnitPrice = facts.UnitPrice,
                Gained = facts.Duplicate ? 0 : facts.Gained,
                Currency = facts.Currency,
                Balance = facts.Balance,
                BagSlot = request.BagSlot,
                Duplicate = facts.Duplicate,
            };
        }
        catch (NpcShopException ex)
        {
            logger.LogInformation(
                "npc_shop_sell_reject id={Id} shop={Shop} slot={Slot} reason={Reason}",
                characterId, shopId, request.BagSlot, ex.Code);
            return new SellNpcItemResponse
            {
                Ok = false,
                Reason = ex.Code,
                ShopId = shopId,
                BagSlot = request.BagSlot,
            };
        }
    }


    /// <summary>回购：按卖出时落库的单价买回来，过期与已回购都算没有。</summary>
    public override async Task<BuybackNpcItemResponse> BuybackNpcItem(
        BuybackNpcItemRequest request,
        ServerCallContext context)
    {
        var characterId = (long)request.CharacterId;
        var shopId = (request.ShopId ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();

        if (characterId == 0 || !await inventory.ExistsAsync(characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        if (txnId.Length == 0 || txnId.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        try
        {
            var facts = await npcShops.BuybackAsync(
                characterId, shopId, request.BuybackId, txnId, context.CancellationToken);

            logger.LogInformation(
                "npc_shop_buyback id={Id} shop={Shop} buyback={Buyback} item={Item}"
                + " paid={Paid} balance={Balance} duplicate={Dup}",
                characterId, shopId, facts.BuybackId, facts.ItemId, facts.Paid,
                facts.Balance, facts.Duplicate);

            return new BuybackNpcItemResponse
            {
                Ok = !facts.Duplicate,
                Reason = facts.Duplicate ? "duplicate" : "",
                BuybackId = facts.BuybackId,
                ItemId = facts.ItemId,
                Name = facts.Name,
                Count = facts.Count,
                UnitPrice = facts.UnitPrice,
                Paid = facts.Duplicate ? 0 : facts.Paid,
                Currency = facts.Currency,
                Balance = facts.Balance,
                BagSlot = facts.BagSlot,
                Duplicate = facts.Duplicate,
            };
        }
        catch (NpcShopException ex)
        {
            logger.LogInformation(
                "npc_shop_buyback_reject id={Id} shop={Shop} buyback={Buyback} reason={Reason}",
                characterId, shopId, request.BuybackId, ex.Code);
            return new BuybackNpcItemResponse
            {
                Ok = false,
                Reason = ex.Code,
                BuybackId = request.BuybackId,
            };
        }
    }


    /// <summary>
    /// 修理：只修身上的三件，按缺失点数 × 单价扣金币，补满后耐久写回满（NULL）。
    /// 都不缺就拒，不白扣钱。
    /// </summary>
    public override async Task<RepairNpcEquipmentResponse> RepairNpcEquipment(
        RepairNpcEquipmentRequest request,
        ServerCallContext context)
    {
        var characterId = (long)request.CharacterId;
        var txnId = (request.TxnId ?? "").Trim();

        if (characterId == 0 || !await inventory.ExistsAsync(characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        if (txnId.Length == 0 || txnId.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        try
        {
            var facts = await npcShops.RepairAsync(characterId, txnId, context.CancellationToken);

            logger.LogInformation(
                "npc_shop_repair id={Id} txn={Txn} charged={Charged} balance={Balance}"
                + " rows={Rows} duplicate={Dup}",
                characterId, txnId, facts.Charged, facts.Balance, facts.Repaired.Count,
                facts.Duplicate);

            var response = new RepairNpcEquipmentResponse
            {
                Ok = !facts.Duplicate,
                Reason = facts.Duplicate ? "duplicate" : "",
                Charged = facts.Duplicate ? 0 : facts.Charged,
                Currency = facts.Currency,
                Balance = facts.Balance,
                Duplicate = facts.Duplicate,
            };

            foreach (var row in facts.Repaired)
            {
                response.Repaired.Add(new NpcRepairRow
                {
                    Slot = row.Slot,
                    ItemId = row.ItemId,
                    Before = row.Before,
                    After = row.After,
                    Max = row.Max,
                });
            }

            return response;
        }
        catch (NpcShopException ex)
        {
            logger.LogInformation(
                "npc_shop_repair_reject id={Id} reason={Reason}", characterId, ex.Code);
            return new RepairNpcEquipmentResponse
            {
                Ok = false,
                Reason = ex.Code,
            };
        }
    }
}
