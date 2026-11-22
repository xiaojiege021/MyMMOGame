using Mmo.Proto.Character;
using Npgsql;

namespace Character;

/// <summary>NPC 商店被业务规则挡下。Code 原样回到回包的 reason，不抛 gRPC 错误码。</summary>
public sealed class NpcShopException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>只读：一个货架条目今天还剩多少、这个人今天买了几件。</summary>
public sealed record NpcGoodsView(
    string ItemId, string Name, long Price, int StockLeft, bool Unlimited, int BoughtToday,
    int LimitPerCharacter, bool Enough, bool SoldOut, bool LimitReached);

/// <summary>只读：一笔可回购的记录。</summary>
public sealed record NpcBuybackRow(
    long Id, string ItemId, string Name, int Count, long UnitPrice, DateTimeOffset SoldAt);

/// <summary>只读：面板要的全部事实。价格、库存、上限、余额都在这里，界面不自己算。</summary>
public sealed record NpcShopView(
    string ShopId, string NpcId, string Currency, string Refresh, long Balance,
    IReadOnlyList<NpcGoodsView> Goods,
    IReadOnlyList<(string ItemId, string Name, long Price)> Sell,
    long SellBalance,
    IReadOnlyList<NpcBuybackRow> Buyback,
    long RepairPricePerPoint, string RepairCurrency,
    IReadOnlyList<(string Slot, string ItemId, string Name, int Durability, int Max)> Worn);

/// <summary>一次买入的回包事实。Duplicate=true 表示这个事务号用过，一分钱没扣。</summary>
public sealed record NpcBuyFacts(
    bool Duplicate, string ShopId, string ItemId, string Name, string Currency,
    long Price, long Balance, int StockLeft, int BoughtToday, int LimitPerCharacter,
    int BagSlot, int Count, bool Bound);

/// <summary>一次卖出的回包事实。BuybackId 是随后回购要用的号。</summary>
public sealed record NpcSellFacts(
    bool Duplicate, string ShopId, long BuybackId, string ItemId, string Name,
    int Count, long UnitPrice, long Gained, string Currency, long Balance, int BagSlot);

/// <summary>一次回购的回包事实。</summary>
public sealed record NpcBuybackFacts(
    bool Duplicate, long BuybackId, string ItemId, string Name, int Count,
    long UnitPrice, long Paid, string Currency, long Balance, int BagSlot);

/// <summary>一次修理的回包事实。每件装备的修前 / 上限都照实回，收费只在服务端算。</summary>
public sealed record NpcRepairFacts(
    bool Duplicate, long Charged, string Currency, long Balance,
    IReadOnlyList<(string Slot, string ItemId, int Before, int After, int Max)> Repaired);

/// <summary>
/// NPC 商店的唯一写入点（K2+Q13，开发架构 14.7 顺序 6）。
///
/// 四件事各一笔事务，全部「先抢号、后动钱与背包」，任何一步不满足整笔回滚：
///   buy     抢号 → 当日限购 +1（带条件的 upsert）→ 当日库存 +1（带条件的 upsert）→
///           扣币 → 入包 → 提交。计数与钱、货同一条事务，拒绝时三处都不动。
///   sell    抢号 → 锁背包行 → 白名单 / 穿戴 / 数量 → 删行或减数 → 加币 → 记回购流水。
///   buyback 抢号 → 锁回购行（过期 / 已回购都算没有）→ 扣币 → 入包 → 写回购时刻。
///   repair  抢号 → 锁穿戴行 → 按缺失点数计价 → 扣币 → 耐久写回满（NULL）。
///
/// 钱只走 GrantRepository 的那一条 SQL，入包也只走它的那一条；本类不另开第二套。
/// </summary>
public sealed class NpcShopRepository(
    NpgsqlDataSource dataSource,
    NpcShopTable table,
    ItemTable items,
    CharacterConfig config,
    GrantRepository grants,
    CalendarTable calendar)
{
    // ------------------------------------------------------------------ 只读

    public async Task<NpcShopView> DescribeAsync(
        long characterId, string shopId, CancellationToken stopping)
    {
        var shop = table.FindShop(shopId)
            ?? throw new NpcShopException("shop_unknown");

        var day = calendar.DayKey(DateTimeOffset.UtcNow);
        await using var connection = await dataSource.OpenConnectionAsync(stopping);

        var balances = await ReadBalancesAsync(connection, characterId, stopping);
        var balance = balances.GetValueOrDefault(shop.Currency, 0L);
        var sellBalance = balances.GetValueOrDefault(table.Sell.Currency, 0L);

        var sold = await ReadDayCountersAsync(connection, shop.Id, day, stopping);
        var bought = await ReadBoughtCountersAsync(connection, characterId, shop.Id, day, stopping);

        var goods = new List<NpcGoodsView>(shop.Items.Count);
        foreach (var g in shop.Items)
        {
            var def = items.Find(g.ItemId);
            var soldToday = sold.GetValueOrDefault(g.ItemId, 0);
            var boughtToday = bought.GetValueOrDefault(g.ItemId, 0);
            var stockLeft = g.StockPerDay == 0 ? 0 : Math.Max(0, g.StockPerDay - soldToday);
            var soldOut = g.StockPerDay != 0 && stockLeft == 0;
            var limitReached = g.LimitPerCharacter != 0 && boughtToday >= g.LimitPerCharacter;
            goods.Add(new NpcGoodsView(
                g.ItemId, def?.Name ?? g.ItemId, g.Price, stockLeft, g.StockPerDay == 0,
                boughtToday, g.LimitPerCharacter, balance >= g.Price, soldOut, limitReached));
        }

        var sell = new List<(string, string, long)>(table.Sell.Items.Count);
        foreach (var line in table.Sell.Items)
        {
            sell.Add((line.ItemId, items.Find(line.ItemId)?.Name ?? line.ItemId, line.Price));
        }

        var buyback = await ReadBuybackAsync(connection, characterId, shop.Id, stopping);
        var worn = await ReadWornAsync(connection, characterId, stopping);

        return new NpcShopView(shop.Id, shop.NpcId, shop.Currency, shop.Refresh, balance,
            goods, sell, sellBalance, buyback,
            table.Repair.PricePerPoint, table.Repair.Currency, worn);
    }

    // ------------------------------------------------------------------ 买

    public async Task<NpcBuyFacts> BuyAsync(
        long characterId, string shopId, string itemId, string txnId, CancellationToken stopping)
    {
        var shop = table.FindShop(shopId)
            ?? throw new NpcShopException("shop_unknown");

        var goods = table.FindGoods(shop, itemId)
            ?? throw new NpcShopException("item_not_in_shop");

        var def = items.Find(goods.ItemId)
            ?? throw new NpcShopException("item_not_in_shop");

        var day = calendar.DayKey(DateTimeOffset.UtcNow);

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await grants.TryClaimTxnInTransactionAsync(
                connection, transaction, characterId, txnId, stopping))
        {
            await transaction.RollbackAsync(stopping);
            var after = await CurrentBalanceAsync(characterId, shop.Currency, stopping);
            return new NpcBuyFacts(true, shop.Id, goods.ItemId, def.Name, shop.Currency,
                goods.Price, after, 0, 0, goods.LimitPerCharacter, 0, 0, false);
        }

        var boughtToday = await BumpAsync(
            connection, transaction,
            "insert into \"角色\".npc_shop_purchase "
            + "(character_id, shop_id, item_id, day_key, bought) values ($1, $2, $3, $4, 1) "
            + "on conflict (character_id, shop_id, item_id, day_key) do update "
            + "set bought = npc_shop_purchase.bought + 1 "
            + "where $5 = 0 or npc_shop_purchase.bought + 1 <= $5 "
            + "returning bought",
            stopping, characterId, shop.Id, goods.ItemId, day, goods.LimitPerCharacter);

        if (boughtToday == 0)
        {
            await transaction.RollbackAsync(stopping);
            throw new NpcShopException("limit_reached");
        }

        var soldToday = await BumpAsync(
            connection, transaction,
            "insert into \"角色\".npc_shop_day (shop_id, item_id, day_key, sold) "
            + "values ($1, $2, $3, 1) "
            + "on conflict (shop_id, item_id, day_key) do update "
            + "set sold = npc_shop_day.sold + 1 "
            + "where $4 = 0 or npc_shop_day.sold + 1 <= $4 "
            + "returning sold",
            stopping, shop.Id, goods.ItemId, day, goods.StockPerDay);

        if (soldToday == 0)
        {
            await transaction.RollbackAsync(stopping);
            throw new NpcShopException("stock_out");
        }

        var slots = await grants.ReadSlotsForTransactionAsync(
            connection, transaction, characterId, stopping);
        var slot = GrantRepository.FindDeliverySlot(slots, def, 1,
            await BagExpandRepository.SlotsAsync(
                connection, transaction, characterId, config, stopping));
        if (slot == 0)
        {
            await transaction.RollbackAsync(stopping);
            throw new NpcShopException("bag_full");
        }

        try
        {
            await grants.ApplyCurrencyDeltaInTransactionAsync(
                connection, transaction, characterId, shop.Currency, -goods.Price, stopping);
        }
        catch (GrantException ex) when (string.Equals(ex.Code, "insufficient", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(stopping);
            throw new NpcShopException("not_enough_gold");
        }

        await grants.ApplyItemDeltasInTransactionAsync(
            connection, transaction, characterId,
            new List<ItemDelta>
            {
                new() { Slot = (uint)slot, ItemId = goods.ItemId, Count = 1 },
            },
            stopping);

        await transaction.CommitAsync(stopping);

        var rest = goods.StockPerDay == 0 ? 0 : Math.Max(0, goods.StockPerDay - soldToday);
        var balance = await CurrentBalanceAsync(characterId, shop.Currency, stopping);

        return new NpcBuyFacts(false, shop.Id, goods.ItemId, def.Name, shop.Currency,
            goods.Price, balance, rest, boughtToday, goods.LimitPerCharacter,
            slot, 1, def.BindsOnArrival);
    }

    // ------------------------------------------------------------------ 卖

    public async Task<NpcSellFacts> SellAsync(
        long characterId, string shopId, int bagSlot, int count, string txnId,
        CancellationToken stopping)
    {
        _ = table.FindShop(shopId) ?? throw new NpcShopException("shop_unknown");

        if (count < 1)
        {
            throw new NpcShopException("count_invalid");
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await grants.TryClaimTxnInTransactionAsync(
                connection, transaction, characterId, txnId, stopping))
        {
            await transaction.RollbackAsync(stopping);
            var after = await CurrentBalanceAsync(characterId, table.Sell.Currency, stopping);
            return new NpcSellFacts(true, shopId, 0, "", "", 0, 0, 0,
                table.Sell.Currency, after, bagSlot);
        }

        // 锁这一行再判：数量、穿戴、白名单都对着库里的现状看，不信请求里带的。
        long rowId;
        string itemId;
        int rowCount;
        await using (var read = new NpgsqlCommand(
            "select id, item_id, count from \"角色\".inventory "
            + "where character_id = $1 and slot = $2 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue((short)bagSlot);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                await reader.DisposeAsync();
                await transaction.RollbackAsync(stopping);
                throw new NpcShopException("item_not_in_bag");
            }

            rowId = reader.GetInt64(0);
            itemId = reader.GetString(1);
            rowCount = reader.GetInt32(2);
            await reader.DisposeAsync();
        }

        var line = table.FindSell(itemId)
            ?? throw await RolledBackAsync(transaction, "not_sellable", stopping);

        if (count > rowCount)
        {
            throw await RolledBackAsync(transaction, "count_invalid", stopping);
        }

        // 穿着的行不许卖：穿戴记录挂在背包行上，删行会把装备一起 CASCADE 掉。
        await using (var worn = new NpgsqlCommand(
            "select 1 from \"角色\".character_equip where inventory_id = $1",
            connection, transaction))
        {
            worn.Parameters.AddWithValue(rowId);
            if (await worn.ExecuteScalarAsync(stopping) is not null)
            {
                await transaction.RollbackAsync(stopping);
                throw new NpcShopException("equipped");
            }
        }

        var left = rowCount - count;
        await using (var write = new NpgsqlCommand(
            left == 0
                ? "delete from \"角色\".inventory where id = $1"
                : "update \"角色\".inventory set count = $2 where id = $1",
            connection, transaction))
        {
            write.Parameters.AddWithValue(rowId);
            if (left > 0)
            {
                write.Parameters.AddWithValue(left);
            }

            await write.ExecuteNonQueryAsync(stopping);
        }

        var gained = line.Price * count;
        var unitPrice = table.BuybackPriceOf(line.Price);
        if (gained > 0)
        {
            await grants.ApplyCurrencyDeltaInTransactionAsync(
                connection, transaction, characterId, table.Sell.Currency, gained, stopping);
        }

        long buybackId;
        await using (var insert = new NpgsqlCommand(
            "insert into \"角色\".npc_shop_buyback "
            + "(character_id, shop_id, item_id, count, unit_price) "
            + "values ($1, $2, $3, $4, $5) returning id",
            connection, transaction))
        {
            insert.Parameters.AddWithValue(characterId);
            insert.Parameters.AddWithValue(shopId);
            insert.Parameters.AddWithValue(itemId);
            insert.Parameters.AddWithValue(count);
            insert.Parameters.AddWithValue(unitPrice);
            buybackId = Convert.ToInt64(await insert.ExecuteScalarAsync(stopping));
        }

        await transaction.CommitAsync(stopping);

        var balance = await CurrentBalanceAsync(characterId, table.Sell.Currency, stopping);
        return new NpcSellFacts(false, shopId, buybackId, itemId,
            items.Find(itemId)?.Name ?? itemId, count, unitPrice, gained,
            table.Sell.Currency, balance, bagSlot);
    }

    // ------------------------------------------------------------------ 回购

    public async Task<NpcBuybackFacts> BuybackAsync(
        long characterId, string shopId, long buybackId, string txnId,
        CancellationToken stopping)
    {
        var shop = table.FindShop(shopId)
            ?? throw new NpcShopException("shop_unknown");

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await grants.TryClaimTxnInTransactionAsync(
                connection, transaction, characterId, txnId, stopping))
        {
            await transaction.RollbackAsync(stopping);
            var after = await CurrentBalanceAsync(characterId, table.Sell.Currency, stopping);
            return new NpcBuybackFacts(true, buybackId, "", "", 0, 0, 0,
                table.Sell.Currency, after, 0);
        }

        string itemId;
        int count;
        long unitPrice;
        await using (var read = new NpgsqlCommand(
            "select item_id, count, unit_price from \"角色\".npc_shop_buyback "
            + "where id = $1 and character_id = $2 and shop_id = $3 "
            + "and bought_back_at is null "
            + "and sold_at >= now() - make_interval(days => $4) for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(buybackId);
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue(shop.Id);
            read.Parameters.AddWithValue(table.Buyback.ExpireDays);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                await reader.DisposeAsync();
                await transaction.RollbackAsync(stopping);
                throw new NpcShopException("buyback_empty");
            }

            itemId = reader.GetString(0);
            count = reader.GetInt32(1);
            unitPrice = reader.GetInt64(2);
            await reader.DisposeAsync();
        }

        var def = items.Find(itemId)
            ?? throw new NpcShopException("item_not_in_shop");

        var slots = await grants.ReadSlotsForTransactionAsync(
            connection, transaction, characterId, stopping);
        var slot = GrantRepository.FindDeliverySlot(slots, def, count,
            await BagExpandRepository.SlotsAsync(
                connection, transaction, characterId, config, stopping));
        if (slot == 0)
        {
            await transaction.RollbackAsync(stopping);
            throw new NpcShopException("bag_full");
        }

        var paid = unitPrice * count;
        if (paid > 0)
        {
            try
            {
                await grants.ApplyCurrencyDeltaInTransactionAsync(
                    connection, transaction, characterId, table.Sell.Currency, -paid, stopping);
            }
            catch (GrantException ex) when (string.Equals(ex.Code, "insufficient", StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(stopping);
                throw new NpcShopException("not_enough_gold");
            }
        }

        await grants.ApplyItemDeltasInTransactionAsync(
            connection, transaction, characterId,
            new List<ItemDelta>
            {
                new() { Slot = (uint)slot, ItemId = itemId, Count = count },
            },
            stopping);

        await using (var mark = new NpgsqlCommand(
            "update \"角色\".npc_shop_buyback set bought_back_at = now() where id = $1",
            connection, transaction))
        {
            mark.Parameters.AddWithValue(buybackId);
            await mark.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);

        var balance = await CurrentBalanceAsync(characterId, table.Sell.Currency, stopping);
        return new NpcBuybackFacts(false, buybackId, itemId, def.Name, count, unitPrice, paid,
            table.Sell.Currency, balance, slot);
    }

    // ------------------------------------------------------------------ 修理

    public async Task<NpcRepairFacts> RepairAsync(
        long characterId, string txnId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await grants.TryClaimTxnInTransactionAsync(
                connection, transaction, characterId, txnId, stopping))
        {
            await transaction.RollbackAsync(stopping);
            var after = await CurrentBalanceAsync(characterId, table.Repair.Currency, stopping);
            return new NpcRepairFacts(true, 0, table.Repair.Currency, after,
                Array.Empty<(string, string, int, int, int)>());
        }

        // 锁住穿戴的三行：修的是身上的装备，不修背包里躺着的。
        var worn = new List<(string Slot, long RowId, string ItemId, int Before, int Max)>();
        await using (var read = new NpgsqlCommand(
            "select e.slot, i.id, i.item_id, i.durability "
            + "from \"角色\".character_equip e "
            + "join \"角色\".inventory i on i.id = e.inventory_id "
            + "where e.character_id = $1 order by e.slot for update of i",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                var itemId = reader.GetString(2);
                var max = table.DurabilityMaxOf(items.Find(itemId)?.Grade ?? "");
                var before = reader.IsDBNull(3) ? max : reader.GetInt32(3);
                worn.Add((reader.GetString(0), reader.GetInt64(1), itemId,
                    Math.Min(before, max), max));
            }
        }

        if (worn.Count == 0)
        {
            await transaction.RollbackAsync(stopping);
            throw new NpcShopException("not_equipment");
        }

        var missing = 0;
        foreach (var row in worn)
        {
            missing += Math.Max(0, row.Max - row.Before);
        }

        if (missing == 0)
        {
            await transaction.RollbackAsync(stopping);
            throw new NpcShopException("durability_full");
        }

        var charged = missing * table.Repair.PricePerPoint;
        try
        {
            await grants.ApplyCurrencyDeltaInTransactionAsync(
                connection, transaction, characterId, table.Repair.Currency, -charged, stopping);
        }
        catch (GrantException ex) when (string.Equals(ex.Code, "insufficient", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(stopping);
            throw new NpcShopException("not_enough_gold");
        }

        foreach (var row in worn)
        {
            if (row.Before >= row.Max)
            {
                continue;
            }

            await using var write = new NpgsqlCommand(
                "update \"角色\".inventory set durability = null where id = $1",
                connection, transaction);
            write.Parameters.AddWithValue(row.RowId);
            await write.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);

        var balance = await CurrentBalanceAsync(characterId, table.Repair.Currency, stopping);
        var repaired = worn
            .Select(r => (r.Slot, r.ItemId, r.Before, r.Max, r.Max))
            .ToList();

        return new NpcRepairFacts(false, charged, table.Repair.Currency, balance, repaired);
    }

    // ------------------------------------------------------------------ 内部

    /// <summary>
    /// 带条件的自增：返回新值表示这一格真的加了，返回 0 表示上限挡下了。
    /// 一条语句既判上限又写入，两个并发请求不会都看到「还没满」。
    /// </summary>
    static async Task<int> BumpAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        CancellationToken stopping, params object[] args)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var arg in args)
        {
            command.Parameters.AddWithValue(arg);
        }

        var value = await command.ExecuteScalarAsync(stopping);
        return value is null ? 0 : Convert.ToInt32(value);
    }

    static async Task<NpcShopException> RolledBackAsync(
        NpgsqlTransaction transaction, string code, CancellationToken stopping)
    {
        await transaction.RollbackAsync(stopping);
        return new NpcShopException(code);
    }

    async Task<long> CurrentBalanceAsync(long characterId, string currency, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select amount from \"角色\".currency where character_id = $1 and kind = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(currency);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is null ? 0L : Convert.ToInt64(value);
    }

    static async Task<Dictionary<string, long>> ReadBalancesAsync(
        NpgsqlConnection connection, long characterId, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select kind, amount from \"角色\".currency where character_id = $1",
            connection);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(stopping))
        {
            map[reader.GetString(0)] = reader.GetInt64(1);
        }

        return map;
    }

    static async Task<Dictionary<string, int>> ReadDayCountersAsync(
        NpgsqlConnection connection, string shopId, DateOnly day, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select item_id, sold from \"角色\".npc_shop_day where shop_id = $1 and day_key = $2",
            connection);
        command.Parameters.AddWithValue(shopId);
        command.Parameters.AddWithValue(day);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(stopping))
        {
            map[reader.GetString(0)] = reader.GetInt32(1);
        }

        return map;
    }

    static async Task<Dictionary<string, int>> ReadBoughtCountersAsync(
        NpgsqlConnection connection, long characterId, string shopId, DateOnly day,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select item_id, bought from \"角色\".npc_shop_purchase "
            + "where character_id = $1 and shop_id = $2 and day_key = $3",
            connection);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(shopId);
        command.Parameters.AddWithValue(day);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(stopping))
        {
            map[reader.GetString(0)] = reader.GetInt32(1);
        }

        return map;
    }

    async Task<List<NpcBuybackRow>> ReadBuybackAsync(
        NpgsqlConnection connection, long characterId, string shopId, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select id, item_id, count, unit_price, sold_at from \"角色\".npc_shop_buyback "
            + "where character_id = $1 and shop_id = $2 and bought_back_at is null "
            + "and sold_at >= now() - make_interval(days => $3) "
            + "order by sold_at desc limit $4",
            connection);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(shopId);
        command.Parameters.AddWithValue(table.Buyback.ExpireDays);
        command.Parameters.AddWithValue(table.Buyback.Keep);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        var rows = new List<NpcBuybackRow>();
        while (await reader.ReadAsync(stopping))
        {
            var itemId = reader.GetString(1);
            rows.Add(new NpcBuybackRow(reader.GetInt64(0), itemId,
                items.Find(itemId)?.Name ?? itemId, reader.GetInt32(2), reader.GetInt64(3),
                reader.GetFieldValue<DateTime>(4)));
        }

        return rows;
    }

    async Task<List<(string, string, string, int, int)>> ReadWornAsync(
        NpgsqlConnection connection, long characterId, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select e.slot, i.item_id, i.durability from \"角色\".character_equip e "
            + "join \"角色\".inventory i on i.id = e.inventory_id "
            + "where e.character_id = $1 order by e.slot",
            connection);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        var rows = new List<(string, string, string, int, int)>();
        while (await reader.ReadAsync(stopping))
        {
            var itemId = reader.GetString(1);
            var def = items.Find(itemId);
            var max = table.DurabilityMaxOf(def?.Grade ?? "");
            var value = reader.IsDBNull(2) ? max : Math.Min(reader.GetInt32(2), max);
            rows.Add((reader.GetString(0), itemId, def?.Name ?? itemId, value, max));
        }

        return rows;
    }
}
