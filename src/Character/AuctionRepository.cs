using Npgsql;

namespace Character;

/// <summary>成交被业务规则挡下。Code 直接回给全局服务，转成 ok=false + reason。</summary>
public sealed class AuctionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record AuctionOutcome(
    bool Ok,
    string Reason,
    long ListingId,
    long PriceGold,
    long TaxGold,
    long NetGold,
    long BuyerGold,
    long SellerGold,
    string ItemId,
    int Count,
    string State,
    bool Duplicate);

/// <summary>只给核对用：从库里独立读回成交前后与全库金币分布。</summary>
public sealed record AuctionLedger(
    long BuyerGold,
    long SellerGold,
    long TotalGold,
    string ListingState,
    long Price,
    long Tax,
    long Net,
    int Settlements,
    int Txns,
    string Accounts,
    long BuyerItems,
    long SellerItems,
    long TotalItems);

/// <summary>
/// 拍卖成交的唯一写入点（技术架构第 3、5、6 节）。全局服务只管挂单与撮合，
/// 买方金币、卖方净额、物品交付、挂单状态与成交流水全在这一笔角色服事务里：
/// 要么全成，要么全退，不会把角色写成一半。
///
/// 税只体现在「卖方少加那一部分」：没有任何一条语句把 tax 写进任何角色的
/// 货币行、背包行或系统账户，net = price - tax，全库金币因此净减少 tax。
/// </summary>
public sealed class AuctionRepository(
    NpgsqlDataSource dataSource,
    AuctionTable table,
    ItemTable items,
    CharacterConfig config)
{
    public async Task<AuctionOutcome> SettleAsync(
        long buyerId, long listingId, string txnId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        try
        {
            // 1. 锁挂单行。标价、卖方、格位、数量全从这儿读，请求里带不进来。
            long sellerId = 0;
            var slot = 0;
            var count = 0;
            long price = 0;
            var state = "missing";
            var found = false;
            await using (var read = new NpgsqlCommand(
                "select seller_id, slot, count, price_gold, state from \"交易\".auction_listing "
                + "where listing_id = $1 for update",
                connection, transaction))
            {
                read.Parameters.AddWithValue(listingId);
                await using var reader = await read.ExecuteReaderAsync(stopping);
                if (await reader.ReadAsync(stopping))
                {
                    sellerId = reader.GetInt64(0);
                    slot = reader.GetInt16(1);
                    count = reader.GetInt32(2);
                    price = reader.GetInt64(3);
                    state = reader.GetString(4);
                    found = true;
                }
            }

            if (!found)
            {
                throw new AuctionException("listing_not_found");
            }

            if (state != "listed")
            {
                throw new AuctionException("listing_sold");
            }

            if (sellerId == buyerId)
            {
                throw new AuctionException("same_character");
            }

            if (slot < 1 || slot > config.BagSlots || count < 1 || price < 1)
            {
                throw new AuctionException("listing_invalid");
            }

            var tax = table.TaxOf(price);
            var net = price - tax;

            // 2. 抢事务号。撞主键说明这一笔已经结过，整笔退回。
            await using (var claim = new NpgsqlCommand(
                "insert into \"交易\".auction_txn (txn_id, listing_id) values ($1, $2) "
                + "on conflict (txn_id) do nothing returning txn_id",
                connection, transaction))
            {
                claim.Parameters.AddWithValue(txnId);
                claim.Parameters.AddWithValue(listingId);
                var claimed = await claim.ExecuteScalarAsync(stopping);
                if (claimed is null)
                {
                    await transaction.RollbackAsync(stopping);
                    return new AuctionOutcome(false, "duplicate", listingId, 0, 0, 0, 0, 0,
                        "", 0, "", true);
                }
            }

            // 3. 锁卖方这一格。绑定物品不能上架（P1-1b3），物品编号由库里读。
            var itemId = "";
            var sellerCount = 0;
            var bound = false;
            await using (var read = new NpgsqlCommand(
                "select item_id, count, bound from \"角色\".inventory "
                + "where character_id = $1 and slot = $2 for update",
                connection, transaction))
            {
                read.Parameters.AddWithValue(sellerId);
                read.Parameters.AddWithValue((short)slot);
                await using var reader = await read.ExecuteReaderAsync(stopping);
                if (await reader.ReadAsync(stopping))
                {
                    itemId = reader.GetString(0);
                    sellerCount = reader.GetInt32(1);
                    bound = reader.GetBoolean(2);
                }
            }

            if (itemId.Length == 0)
            {
                throw new AuctionException("item_missing");
            }

            if (bound)
            {
                throw new AuctionException("bound_item");
            }

            if (sellerCount < count)
            {
                throw new AuctionException("item_insufficient");
            }

            var item = items.Find(itemId) ?? throw new AuctionException("unknown_item");
            if (count > item.StackMax)
            {
                throw new AuctionException("count_invalid");
            }

            // 4. 卖方扣物品。扣光直接删行：inventory.count 有 CHECK(count > 0)。
            if (sellerCount == count)
            {
                await using var delete = new NpgsqlCommand(
                    "delete from \"角色\".inventory where character_id = $1 and slot = $2",
                    connection, transaction);
                delete.Parameters.AddWithValue(sellerId);
                delete.Parameters.AddWithValue((short)slot);
                await delete.ExecuteNonQueryAsync(stopping);
            }
            else
            {
                await using var update = new NpgsqlCommand(
                    "update \"角色\".inventory set count = count - $3 "
                    + "where character_id = $1 and slot = $2",
                    connection, transaction);
                update.Parameters.AddWithValue(sellerId);
                update.Parameters.AddWithValue((short)slot);
                update.Parameters.AddWithValue(count);
                await update.ExecuteNonQueryAsync(stopping);
            }

            // 5. 交给买方：优先叠回未堆满的同编号未绑格，否则取最小空格。
            var targetSlot = 0;
            await using (var read = new NpgsqlCommand(
                "select slot from \"角色\".inventory where character_id = $1 and item_id = $2 "
                + "and bound = false and count + $3 <= $4 order by slot limit 1 for update",
                connection, transaction))
            {
                read.Parameters.AddWithValue(buyerId);
                read.Parameters.AddWithValue(itemId);
                read.Parameters.AddWithValue(count);
                read.Parameters.AddWithValue(item.StackMax);
                await using var reader = await read.ExecuteReaderAsync(stopping);
                if (await reader.ReadAsync(stopping))
                {
                    targetSlot = reader.GetInt16(0);
                }
            }

            if (targetSlot > 0)
            {
                await using var update = new NpgsqlCommand(
                    "update \"角色\".inventory set count = count + $3 "
                    + "where character_id = $1 and slot = $2",
                    connection, transaction);
                update.Parameters.AddWithValue(buyerId);
                update.Parameters.AddWithValue((short)targetSlot);
                update.Parameters.AddWithValue(count);
                await update.ExecuteNonQueryAsync(stopping);
            }
            else
            {
                object? free = null;
                await using (var read = new NpgsqlCommand(
                    "select min(g.s) from generate_series(1, $2) as g(s) "
                    + "where not exists (select 1 from \"角色\".inventory i "
                    + "where i.character_id = $1 and i.slot = g.s)",
                    connection, transaction))
                {
                    read.Parameters.AddWithValue(buyerId);
                    read.Parameters.AddWithValue(config.BagSlots);
                    free = await read.ExecuteScalarAsync(stopping);
                }

                if (free is null || free is DBNull)
                {
                    throw new AuctionException("bag_full");
                }

                await using var insert = new NpgsqlCommand(
                    "insert into \"角色\".inventory "
                    + "(character_id, slot, item_id, count, bound, bound_at) "
                    + "values ($1, $2, $3, $4, false, null)",
                    connection, transaction);
                insert.Parameters.AddWithValue(buyerId);
                insert.Parameters.AddWithValue((short)Convert.ToInt32(free));
                insert.Parameters.AddWithValue(itemId);
                insert.Parameters.AddWithValue(count);
                await insert.ExecuteNonQueryAsync(stopping);
            }

            // 6. 扣买方标价。带余额条件的 update，扣不动整笔回滚。
            await using (var debit = new NpgsqlCommand(
                "update \"角色\".currency set amount = amount + $3, updated_at = now() "
                + "where character_id = $1 and kind = $2 and amount + $3 >= 0 returning amount",
                connection, transaction))
            {
                debit.Parameters.AddWithValue(buyerId);
                debit.Parameters.AddWithValue(table.GoldKind);
                debit.Parameters.AddWithValue(-price);
                var updated = await debit.ExecuteScalarAsync(stopping);
                if (updated is null)
                {
                    throw new AuctionException("gold_insufficient");
                }
            }

            // 7. 卖方只到账净额。税没有对应的 insert/update：它不进任何角色、
            // 不进系统账户，也不生成物品，全库金币因此净减少这一笔。
            await using (var credit = new NpgsqlCommand(
                "insert into \"角色\".currency (character_id, kind, amount) values ($1, $2, $3) "
                + "on conflict (character_id, kind) do update "
                + "set amount = \"角色\".currency.amount + excluded.amount, updated_at = now()",
                connection, transaction))
            {
                credit.Parameters.AddWithValue(sellerId);
                credit.Parameters.AddWithValue(table.GoldKind);
                credit.Parameters.AddWithValue(net);
                await credit.ExecuteNonQueryAsync(stopping);
            }

            // 8. 挂单状态与成交流水在同一笔事务里，和货币、物品一起提交。
            await using (var sold = new NpgsqlCommand(
                "update \"交易\".auction_listing set state = 'sold' "
                + "where listing_id = $1 and state = 'listed'",
                connection, transaction))
            {
                sold.Parameters.AddWithValue(listingId);
                await sold.ExecuteNonQueryAsync(stopping);
            }

            await using (var ledger = new NpgsqlCommand(
                "insert into \"交易\".auction_settlement "
                + "(txn_id, listing_id, buyer_id, seller_id, item_id, count, "
                + "price_gold, tax_gold, net_gold) "
                + "values ($1, $2, $3, $4, $5, $6, $7, $8, $9)",
                connection, transaction))
            {
                ledger.Parameters.AddWithValue(txnId);
                ledger.Parameters.AddWithValue(listingId);
                ledger.Parameters.AddWithValue(buyerId);
                ledger.Parameters.AddWithValue(sellerId);
                ledger.Parameters.AddWithValue(itemId);
                ledger.Parameters.AddWithValue(count);
                ledger.Parameters.AddWithValue(price);
                ledger.Parameters.AddWithValue(tax);
                ledger.Parameters.AddWithValue(net);
                await ledger.ExecuteNonQueryAsync(stopping);
            }

            var buyerGold = await GoldOfAsync(connection, transaction, buyerId, stopping);
            var sellerGold = await GoldOfAsync(connection, transaction, sellerId, stopping);
            await transaction.CommitAsync(stopping);

            return new AuctionOutcome(true, "", listingId, price, tax, net,
                buyerGold, sellerGold, itemId, count, "sold", false);
        }
        catch (AuctionException ex)
        {
            // 所有 reader 都在各自的 await using 块里关掉了，这里回滚不会撞 502。
            await transaction.RollbackAsync(stopping);
            return new AuctionOutcome(false, ex.Code, listingId, 0, 0, 0, 0, 0,
                "", 0, "", false);
        }
    }

    static async Task<long> GoldOfAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select amount from \"角色\".currency where character_id = $1 and kind = 'gold'",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>只给核对用：不看回包，从库里独立读回成交全貌与全库金币分布。</summary>
    public async Task<AuctionLedger> StateAsync(
        long buyerId, long sellerId, long listingId, string txnId, CancellationToken stopping)
    {
        var buyerGold = await SingleGoldAsync(buyerId, stopping);
        var sellerGold = await SingleGoldAsync(sellerId, stopping);

        long total = 0;
        var accounts = new List<string>();
        await using (var read = dataSource.CreateCommand(
            "select character_id, amount from \"角色\".currency where kind = 'gold' order by character_id"))
        {
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                var id = reader.GetInt64(0);
                var amount = reader.GetInt64(1);
                total += amount;
                accounts.Add(id + ":" + amount);
            }
        }

        var state = "missing";
        await using (var read = dataSource.CreateCommand(
            "select state from \"交易\".auction_listing where listing_id = $1"))
        {
            read.Parameters.AddWithValue(listingId);
            var value = await read.ExecuteScalarAsync(stopping);
            if (value is not null && value is not DBNull)
            {
                state = (string)value;
            }
        }

        long price = 0, tax = 0, net = 0;
        await using (var read = dataSource.CreateCommand(
            "select price_gold, tax_gold, net_gold from \"交易\".auction_settlement "
            + "where txn_id = $1"))
        {
            read.Parameters.AddWithValue(txnId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                price = reader.GetInt64(0);
                tax = reader.GetInt64(1);
                net = reader.GetInt64(2);
            }
        }

        var settlements = await CountAsync(
            "select count(*) from \"交易\".auction_settlement where listing_id = $1",
            listingId, stopping);
        var txns = await CountAsync(
            "select count(*) from \"交易\".auction_txn where txn_id = $1",
            txnId, stopping);

        var buyerItems = await ItemCountAsync(buyerId, stopping);
        var sellerItems = await ItemCountAsync(sellerId, stopping);
        long totalItems;
        await using (var read = dataSource.CreateCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory"))
        {
            totalItems = Convert.ToInt64(await read.ExecuteScalarAsync(stopping));
        }

        return new AuctionLedger(buyerGold, sellerGold, total, state,
            price, tax, net, settlements, txns, string.Join(",", accounts),
            buyerItems, sellerItems, totalItems);
    }

    async Task<long> ItemCountAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory where character_id = $1");
        command.Parameters.AddWithValue(characterId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(stopping));
    }

    async Task<long> SingleGoldAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select amount from \"角色\".currency where character_id = $1 and kind = 'gold'");
        command.Parameters.AddWithValue(characterId);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value);
    }

    async Task<int> CountAsync(string sql, object value, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(stopping));
    }
}
