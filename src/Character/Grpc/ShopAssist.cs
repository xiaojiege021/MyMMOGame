using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

    // ------------------------------------------------------------------ P3-4a2 通行证

    /// <summary>
    /// 商城里的通行证：扣元宝与开本季付费轨在同一笔事务里（缺一样整笔退回）。
    /// 季向全局服务确认——客户端表达不了「买哪一季」，标价只从清单读。
    /// 买下不往背包放东西，所以回包 delivered 恒为 false，state 用 pass_activated 区分。
    /// </summary>
    async Task<BuyShopItemResponse> BuySeasonPassAsync(
        string account, long characterId, ShopTable.Item item, string txnId,
        ServerCallContext context)
    {
        var txn = (txnId ?? "").Trim();
        if (txn.Length == 0 || txn.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        var season = await seasonSource.CurrentSeasonAsync(context.CancellationToken);
        if (season is null)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} kind={Kind} reason=season_unavailable",
                account, characterId, item.Id, item.Kind);
            return new BuyShopItemResponse
            {
                Ok = false,
                Reason = "season_unavailable",
                ItemId = item.Id,
                Kind = item.Kind,
                Name = item.Name,
                PriceYuanbao = item.PriceYuanbao,
                Currency = ShopTable.WrittenCurrency,
                ChargedYuanbao = 0,
                Delivered = false,
                State = "season_unavailable",
            };
        }

        var outcome = await seasonPasses.BuyAsync(
            characterId, season, txn, context.CancellationToken);

        if (!outcome.Ok)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} kind={Kind} reason={Reason}"
                + " season={Season} price={Price} balance={Balance}",
                account, characterId, item.Id, item.Kind, outcome.Reason, season,
                item.PriceYuanbao, outcome.Balance);

            return new BuyShopItemResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                ItemId = item.Id,
                Kind = item.Kind,
                Name = item.Name,
                PriceYuanbao = item.PriceYuanbao,
                Currency = ShopTable.WrittenCurrency,
                Balance = outcome.Balance,
                Enough = outcome.Balance >= item.PriceYuanbao,
                ChargedYuanbao = 0,
                Delivered = false,
                State = outcome.Reason,
                SeasonId = season,
                PassOwned = outcome.Paid,
                Duplicate = outcome.Duplicate,
            };
        }

        logger.LogInformation(
            "shop_buy account={Account} id={Id} item={Item} kind={Kind} txn={Txn} price={Price}"
            + " charged={Charged} season={Season} pass_owned={Owned} balance={Balance}",
            account, characterId, item.Id, item.Kind, txn, item.PriceYuanbao,
            item.PriceYuanbao, season, outcome.Paid, outcome.Balance);

        return new BuyShopItemResponse
        {
            Ok = true,
            Reason = "",
            ItemId = item.Id,
            Kind = item.Kind,
            Name = item.Name,
            PriceYuanbao = item.PriceYuanbao,
            Currency = ShopTable.WrittenCurrency,
            Balance = outcome.Balance,
            Enough = true,
            ChargedYuanbao = item.PriceYuanbao,
            Delivered = false,
            State = "pass_activated",
            SeasonId = season,
            PassOwned = outcome.Paid,
            Duplicate = false,
        };
    }


    // ------------------------------------------------------------------ P3-4b 月叠

    /// <summary>
    /// 商城里的月叠：扣元宝与「两种符一次到账」在同一笔事务里（缺一样整笔退回）。
    /// 发什么、每种几张只从 config/monthly_bundle.json 读（每天几张 × 天数），
    /// 数量跨过堆叠上限就占多格。回包里 delivery_item_id / bag_slot 给的是**第一摞**，
    /// 全部摞数打在日志里，界面与核对以 `/inventory` 的事实为准。
    /// </summary>
    async Task<BuyShopItemResponse> BuyMonthlyBundleAsync(
        string account, long characterId, ShopTable.Item item, string txnId,
        ServerCallContext context)
    {
        var outcome = await monthlyBundles.BuyAsync(characterId, txnId, context.CancellationToken);
        var first = outcome.Stacks.Count > 0 ? outcome.Stacks[0] : null;

        if (!outcome.Ok)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} kind={Kind} reason={Reason}"
                + " price={Price} balance={Balance}",
                account, characterId, item.Id, item.Kind, outcome.Reason,
                item.PriceYuanbao, outcome.Balance);

            return new BuyShopItemResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                ItemId = item.Id,
                Kind = item.Kind,
                Name = item.Name,
                PriceYuanbao = item.PriceYuanbao,
                Currency = ShopTable.WrittenCurrency,
                Balance = outcome.Balance,
                Enough = outcome.Balance >= item.PriceYuanbao,
                ChargedYuanbao = 0,
                Delivered = false,
                State = outcome.Reason,
                Duplicate = outcome.Duplicate,
            };
        }

        logger.LogInformation(
            "shop_buy account={Account} id={Id} item={Item} kind={Kind} txn={Txn} price={Price}"
            + " charged={Charged} days={Days} stacks={Stacks} balance={Balance}",
            account, characterId, item.Id, item.Kind, txnId, item.PriceYuanbao,
            item.PriceYuanbao, monthlyBundleTable.Days,
            string.Join("+", outcome.Stacks.Select(x => x.ItemId + "x" + x.Count + "@" + x.Slot)),
            outcome.Balance);

        return new BuyShopItemResponse
        {
            Ok = true,
            Reason = "",
            ItemId = item.Id,
            Kind = item.Kind,
            Name = item.Name,
            PriceYuanbao = item.PriceYuanbao,
            Currency = ShopTable.WrittenCurrency,
            Balance = outcome.Balance,
            Enough = true,
            ChargedYuanbao = item.PriceYuanbao,
            Delivered = true,
            State = "bundle_delivered",
            DeliveryItemId = first?.ItemId ?? "",
            BagSlot = (uint)(first?.Slot ?? 0),
            Bound = true,
            Duplicate = false,
        };
    }


    // ------------------------------------------------------------------ P3-4b 第二角色栏

    /// <summary>
    /// 商城里的第二角色栏：账号级状态发货——买下给这个账号加一个角色位，不往背包放东西，
    /// 所以回包 delivered 恒为 false，state 用 slot_added 区分；名额用 character_slots 带回。
    /// 名额上限、基础位与标价全在配置与清单里，客户端带不进来。
    /// </summary>
    async Task<BuyShopItemResponse> BuySecondSlotAsync(
        string account, long characterId, ShopTable.Item item, string txnId,
        ServerCallContext context)
    {
        var outcome = await characterSlots.BuyAsync(
            account, characterId, txnId, context.CancellationToken);

        if (!outcome.Ok)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} kind={Kind} reason={Reason}"
                + " price={Price} slots={Slots} balance={Balance}",
                account, characterId, item.Id, item.Kind, outcome.Reason,
                item.PriceYuanbao, outcome.Slots, outcome.Balance);

            return new BuyShopItemResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                ItemId = item.Id,
                Kind = item.Kind,
                Name = item.Name,
                PriceYuanbao = item.PriceYuanbao,
                Currency = ShopTable.WrittenCurrency,
                Balance = outcome.Balance,
                Enough = outcome.Balance >= item.PriceYuanbao,
                ChargedYuanbao = 0,
                Delivered = false,
                State = outcome.Reason,
                CharacterSlots = outcome.Slots,
                Duplicate = outcome.Duplicate,
            };
        }

        logger.LogInformation(
            "shop_buy account={Account} id={Id} item={Item} kind={Kind} txn={Txn} price={Price}"
            + " charged={Charged} slots={Slots} balance={Balance}",
            account, characterId, item.Id, item.Kind, txnId, item.PriceYuanbao,
            item.PriceYuanbao, outcome.Slots, outcome.Balance);

        return new BuyShopItemResponse
        {
            Ok = true,
            Reason = "",
            ItemId = item.Id,
            Kind = item.Kind,
            Name = item.Name,
            PriceYuanbao = item.PriceYuanbao,
            Currency = ShopTable.WrittenCurrency,
            Balance = outcome.Balance,
            Enough = true,
            ChargedYuanbao = item.PriceYuanbao,
            Delivered = false,
            State = "slot_added",
            CharacterSlots = outcome.Slots,
            Duplicate = false,
        };
    }


    // ------------------------------------------------------------------ B7+B8 背包扩容

    /// <summary>
    /// 商城里的背包扩容：状态发货——买下给这个角色加 step 格、到 max_slots 封顶，
    /// 不往背包放东西，所以回包 delivered 恒为 false，state 用 bag_expanded 区分；
    /// 扩容后的格数用 bag_slots 带回。步长与上限只从 config/character.json 读。
    /// </summary>
    async Task<BuyShopItemResponse> BuyBagExpandAsync(
        string account, long characterId, ShopTable.Item item, string txnId,
        ServerCallContext context)
    {
        var outcome = await bagExpands.BuyAsync(
            characterId, txnId, item.PriceYuanbao, context.CancellationToken);

        if (!outcome.Ok)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} kind={Kind} reason={Reason}"
                + " count={Count} slots={Slots} balance={Balance}",
                account, characterId, item.Id, item.Kind, outcome.Reason, outcome.Count,
                outcome.Slots, outcome.Balance);

            return new BuyShopItemResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                ItemId = item.Id,
                Kind = item.Kind,
                Name = item.Name,
                PriceYuanbao = item.PriceYuanbao,
                Currency = ShopTable.WrittenCurrency,
                Balance = outcome.Balance,
                Enough = outcome.Balance >= item.PriceYuanbao,
                ChargedYuanbao = 0,
                Delivered = false,
                State = outcome.Reason,
                BagSlots = (uint)outcome.Slots,
                Duplicate = outcome.Duplicate,
            };
        }

        logger.LogInformation(
            "shop_buy account={Account} id={Id} item={Item} kind={Kind} txn={Txn} price={Price}"
            + " charged={Charged} expands={Count} slots={Slots} balance={Balance}",
            account, characterId, item.Id, item.Kind, txnId, item.PriceYuanbao,
            item.PriceYuanbao, outcome.Count, outcome.Slots, outcome.Balance);

        return new BuyShopItemResponse
        {
            Ok = true,
            Reason = "",
            ItemId = item.Id,
            Kind = item.Kind,
            Name = item.Name,
            PriceYuanbao = item.PriceYuanbao,
            Currency = ShopTable.WrittenCurrency,
            Balance = outcome.Balance,
            Enough = true,
            ChargedYuanbao = item.PriceYuanbao,
            Delivered = false,
            State = "bag_expanded",
            BagSlots = (uint)outcome.Slots,
            Duplicate = false,
        };
    }
}
