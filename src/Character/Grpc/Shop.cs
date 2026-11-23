using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

    /// <summary>
    /// 买一件商城商品。三种走法：
    ///   · 清单外 → not_in_list，标价与扣款都是 0；
    ///   · 清单内但还没接发货 → delivery_pending，一分不扣（P1-5c1 那十七种仍是这条）；
    ///   · 已接发货 → 同一笔事务里扣元宝 + 入包 + 落绑定（P1-5c2 起，本卡只有保护券）。
    ///
    /// 扣款与发货必须同一笔事务：扣了钱没东西、或东西发了钱没扣，都是不能接受的中间态。
    /// 事务号写进 grant_txn，同一个号重放整笔不做。
    /// </summary>
    public override async Task<BuyShopItemResponse> BuyShopItem(
        BuyShopItemRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var itemId = (request.ItemId ?? "").Trim();
        if (itemId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "item_missing"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        // 余额是库里的事实值，用于展示与「够不够买」；真正的扣款由事务里带条件的 update 把关。
        var balances = await currency.AllAsync(characterId, context.CancellationToken);
        var balance = balances.GetValueOrDefault(shopTable.Currency, 0L);

        var item = shopTable.Find(itemId);
        if (item is null)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} reason=not_in_list balance={Balance}",
                account, characterId, itemId, balance);

            // 清单外：标价、数量这些未生效的数值一律给 0，不拿清单里的任何数顶。
            return new BuyShopItemResponse
            {
                Ok = false,
                Reason = "not_in_list",
                ItemId = itemId,
                Currency = shopTable.Currency,
                Balance = balance,
                Enough = false,
                ChargedYuanbao = 0,
                Delivered = false,
                State = "not_in_list",
            };
        }

        // K1：分发只看配置里的 delivery.mode，不再按种类字符串猜。
        // season_pass / monthly_bundle / second_slot 是三种状态发货（买下不往背包放东西），
        // 各进自己的仓储；none 是还没接发货，一分不扣；item 走下面的通用扣款 + 落包。
        switch (item.Mode)
        {
            case DeliveryMode.SeasonPass:
                return await BuySeasonPassAsync(account, characterId, item, request.TxnId ?? "", context);

            case DeliveryMode.MonthlyBundle:
                return await BuyMonthlyBundleAsync(account, characterId, item, request.TxnId ?? "", context);

            case DeliveryMode.SecondSlot:
                return await BuySecondSlotAsync(account, characterId, item, request.TxnId ?? "", context);

            case DeliveryMode.BagExpand:
                return await BuyBagExpandAsync(account, characterId, item, request.TxnId ?? "", context);
        }

        // 未上架是运营开关（配置里的 on_sale），与「还没接发货」分开报。
        if (!item.OnSale)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} reason=not_on_sale balance={Balance}",
                account, characterId, item.Id, balance);
            return ShopRejected("not_on_sale", item, balance);
        }

        if (!item.Deliverable)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} kind={Kind} reason=delivery_pending"
                + " price={Price} balance={Balance}",
                account, characterId, item.Id, item.Kind, item.PriceYuanbao, balance);

            return ShopRejected("delivery_pending", item, balance);
        }

        // B7+B8：方案 12.1「到达金仙才能买金仙外观」。境界是角色服读出来的事实值，
        // 客户端带不进来；门槛段序号只从 shop.json 的 require_realm 读。
        if (item.RealmGated)
        {
            var facts = await inventory.RealmFactsAsync(account, characterId, context.CancellationToken);
            var rank = realms.Rank(facts?.Realm ?? "");
            if (rank < item.RequireRealmIndex)
            {
                logger.LogInformation(
                    "shop_buy_reject account={Account} id={Id} item={Item} kind={Kind}"
                    + " reason=realm_required rank={Rank} require={Require} balance={Balance}",
                    account, characterId, item.Id, item.Kind, rank, item.RequireRealmIndex, balance);
                return ShopRejected("realm_required", item, balance);
            }
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        var goods = items.Find(item.DeliveryItemId);
        if (goods is null)
        {
            // 配置校验要求发货物必须真在物品表里，走到这里说明两张表不一致，按没接发货拒绝。
            logger.LogError(
                "shop_buy_reject account={Account} id={Id} item={Item} reason=goods_missing goods={Goods}",
                account, characterId, item.Id, item.DeliveryItemId);
            return ShopRejected("delivery_pending", item, balance);
        }

        var slots = await inventory.ListAsync(characterId, context.CancellationToken);
        var slot = FindDeliverySlot(slots, goods, item.DeliveryCount,
            await bagExpands.SlotsAsync(characterId, context.CancellationToken));
        if (slot == 0)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} reason=bag_full balance={Balance}",
                account, characterId, item.Id, balance);
            return ShopRejected("bag_full", item, balance);
        }

        if (balance < item.PriceYuanbao)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} reason=not_enough_yuanbao"
                + " price={Price} balance={Balance}",
                account, characterId, item.Id, item.PriceYuanbao, balance);
            return ShopRejected("not_enough_yuanbao", item, balance);
        }

        try
        {
            // 一笔事务：先扣元宝，再把券放进格子里。缺一样整笔退回。
            var outcome = await grants.ApplyAsync(
                characterId,
                txnId,
                new List<GrantEntry>
                {
                    new()
                    {
                        Currency = new CurrencyDelta
                        {
                            Kind = shopTable.Currency,
                            Delta = -item.PriceYuanbao,
                        },
                    },
                    new()
                    {
                        Item = new ItemDelta
                        {
                            Slot = (uint)slot,
                            ItemId = goods.Id,
                            Count = item.DeliveryCount,
                        },
                    },
                },
                context.CancellationToken);

            var after = outcome.Balances.GetValueOrDefault(shopTable.Currency, 0L);

            if (outcome.Duplicate)
            {
                // 同一个事务号又来了：整笔不做，钱和东西都不动，只回当前事实值。
                logger.LogInformation(
                    "shop_buy_replay account={Account} id={Id} item={Item} txn={Txn} balance={Balance}",
                    account, characterId, item.Id, txnId, after);

                return new BuyShopItemResponse
                {
                    Ok = false,
                    Reason = "duplicate",
                    ItemId = item.Id,
                    Kind = item.Kind,
                    Name = item.Name,
                    PriceYuanbao = item.PriceYuanbao,
                    Currency = shopTable.Currency,
                    Balance = after,
                    Enough = true,
                    ChargedYuanbao = 0,
                    Delivered = false,
                    State = "duplicate",
                    DeliveryItemId = goods.Id,
                    BagSlot = 0,
                    Bound = false,
                    Duplicate = true,
                };
            }

            logger.LogInformation(
                "shop_buy account={Account} id={Id} item={Item} kind={Kind} txn={Txn}"
                + " price={Price} charged={Charged} goods={Goods} slot={Slot} bound={Bound} balance={Balance}",
                account, characterId, item.Id, item.Kind, txnId, item.PriceYuanbao,
                item.PriceYuanbao, goods.Id, slot, goods.BindsOnArrival, after);

            return new BuyShopItemResponse
            {
                Ok = true,
                Reason = "",
                ItemId = item.Id,
                Kind = item.Kind,
                Name = item.Name,
                PriceYuanbao = item.PriceYuanbao,
                Currency = shopTable.Currency,
                Balance = after,
                Enough = true,
                ChargedYuanbao = item.PriceYuanbao,
                Delivered = true,
                State = "delivered",
                DeliveryItemId = goods.Id,
                BagSlot = (uint)slot,
                Bound = goods.BindsOnArrival,
                Duplicate = false,
            };
        }
        catch (GrantException ex)
        {
            // 事务已经整笔退回去了：余额与背包都停在原处。
            var reason = ex.Code switch
            {
                "insufficient" => "not_enough_yuanbao",
                "slot_occupied" => "bag_full",
                "stack_overflow" => "bag_full",
                _ => ex.Code,
            };

            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} reason={Reason} balance={Balance}",
                account, characterId, item.Id, reason, balance);

            return ShopRejected(reason, item, balance);
        }
    }


    public override async Task<BuyHomeCoinShopResponse> BuyHomeCoinShopItem(
        BuyHomeCoinShopRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var itemId = (request.ItemId ?? "").Trim();
        if (itemId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "item_missing"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var balances = await currency.AllAsync(characterId, context.CancellationToken);
        var balance = balances.GetValueOrDefault(HomeCoinShopTable.WrittenCurrency, 0L);
        var item = homeCoinShopTable.Find(itemId);
        if (item is null)
        {
            return new BuyHomeCoinShopResponse
            {
                Ok = false,
                Reason = "not_in_list",
                ItemId = itemId,
                Currency = HomeCoinShopTable.WrittenCurrency,
                Balance = balance,
                Enough = false,
            };
        }

        // K1：上架开关与「还没接发货」分开报；家园币商城只有 none / item 两种发货方式。
        if (!item.OnSale)
        {
            return HomeCoinShopRejected("not_on_sale", item, balance);
        }

        if (!item.Deliverable)
        {
            return HomeCoinShopRejected("delivery_pending", item, balance);
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        var goods = items.Find(item.DeliveryItemId);
        if (goods is null)
        {
            return HomeCoinShopRejected("delivery_pending", item, balance);
        }

        var slots = await inventory.ListAsync(characterId, context.CancellationToken);
        var slot = FindDeliverySlot(slots, goods, item.DeliveryCount,
            await bagExpands.SlotsAsync(characterId, context.CancellationToken));
        if (slot == 0)
        {
            return HomeCoinShopRejected("bag_full", item, balance);
        }

        if (balance < item.PriceHomeCoin)
        {
            return HomeCoinShopRejected("not_enough_home_coin", item, balance);
        }

        try
        {
            var outcome = await grants.ApplyAsync(
                characterId,
                txnId,
                new List<GrantEntry>
                {
                    new()
                    {
                        Currency = new CurrencyDelta
                        {
                            Kind = HomeCoinShopTable.WrittenCurrency,
                            Delta = -item.PriceHomeCoin,
                        },
                    },
                    new()
                    {
                        Item = new ItemDelta
                        {
                            Slot = (uint)slot,
                            ItemId = goods.Id,
                            Count = item.DeliveryCount,
                        },
                    },
                },
                context.CancellationToken);

            var after = outcome.Balances.GetValueOrDefault(HomeCoinShopTable.WrittenCurrency, 0L);
            if (outcome.Duplicate)
            {
                return new BuyHomeCoinShopResponse
                {
                    Ok = false,
                    Reason = "duplicate",
                    ItemId = item.Id,
                    Kind = item.Kind,
                    Name = item.Name,
                    PriceHomeCoin = item.PriceHomeCoin,
                    Currency = HomeCoinShopTable.WrittenCurrency,
                    Balance = after,
                    Enough = true,
                    Duplicate = true,
                };
            }

            return new BuyHomeCoinShopResponse
            {
                Ok = true,
                ItemId = item.Id,
                Kind = item.Kind,
                Name = item.Name,
                PriceHomeCoin = item.PriceHomeCoin,
                Currency = HomeCoinShopTable.WrittenCurrency,
                Balance = after,
                Enough = true,
                ChargedHomeCoin = item.PriceHomeCoin,
                Delivered = true,
                State = "delivered",
                DeliveryItemId = goods.Id,
                BagSlot = (uint)slot,
                Bound = goods.BindsOnArrival,
            };
        }
        catch (GrantException ex)
        {
            var reason = ex.Code switch
            {
                "insufficient" => "not_enough_home_coin",
                "slot_occupied" => "bag_full",
                "stack_overflow" => "bag_full",
                _ => ex.Code,
            };

            return HomeCoinShopRejected(reason, item, balance);
        }
    }


    BuyHomeCoinShopResponse HomeCoinShopRejected(
        string reason, HomeCoinShopTable.Item item, long balance) =>
        new()
        {
            Ok = false,
            Reason = reason,
            ItemId = item.Id,
            Kind = item.Kind,
            Name = item.Name,
            PriceHomeCoin = item.PriceHomeCoin,
            Currency = HomeCoinShopTable.WrittenCurrency,
            Balance = balance,
            Enough = balance >= item.PriceHomeCoin,
        };


    /// <summary>没成交的回包：扣款与发货类数值一律 0，余额给库里的事实值。</summary>
    BuyShopItemResponse ShopRejected(string reason, ShopTable.Item item, long balance) =>
        new()
        {
            Ok = false,
            Reason = reason,
            ItemId = item.Id,
            Kind = item.Kind,
            Name = item.Name,
            PriceYuanbao = item.PriceYuanbao,
            Currency = shopTable.Currency,
            Balance = balance,
            Enough = balance >= item.PriceYuanbao,
            ChargedYuanbao = 0,
            Delivered = false,
            State = reason,
            DeliveryItemId = item.DeliveryItemId,
            BagSlot = 0,
            Bound = false,
            Duplicate = false,
        };


    /// <summary>
    /// 挑一个能放这批货的格子：先叠到已有同物品且没满的格，再找空格。
    /// 找不到回 0（调用方回 bag_full）。绑定态不在这里定——入包时按物品表的绑定规则落。
    /// </summary>
    static int FindDeliverySlot(
        IReadOnlyList<BagSlot> slots, ItemDef goods, int count, int bagSlots)
    {
        if (count < 1 || count > goods.StackMax)
        {
            return 0;
        }

        for (var i = 0; i < slots.Count; i++)
        {
            if (string.Equals(slots[i].ItemId, goods.Id, StringComparison.Ordinal)
                && slots[i].Count + count <= goods.StackMax)
            {
                return slots[i].Slot;
            }
        }

        var used = new HashSet<int>();
        for (var i = 0; i < slots.Count; i++)
        {
            used.Add(slots[i].Slot);
        }

        for (var slot = 1; slot <= bagSlots; slot++)
        {
            if (!used.Contains(slot))
            {
                return slot;
            }
        }

        return 0;
    }
}
