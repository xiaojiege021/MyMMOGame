using Npgsql;

namespace Character;

/// <summary>元宝寄售成交被业务规则挡下。Code 直接回给全局服务，转成 ok=false + reason。</summary>
public sealed class IngotException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record IngotOutcome(
    bool Ok,
    string Reason,
    long ListingId,
    long IngotAmount,
    long PriceGold,
    long TaxGold,
    long NetGold,
    long BuyerGold,
    long SellerGold,
    long BuyerIngot,
    long SellerIngot,
    string State,
    bool Duplicate);

/// <summary>只给核对用：从库里独立读回成交前后双方元宝、金币与全库总量。</summary>
public sealed record IngotLedger(
    long BuyerGold,
    long SellerGold,
    long TotalGold,
    long BuyerIngot,
    long SellerIngot,
    long TotalIngot,
    string ListingState,
    long Price,
    long Tax,
    long Net,
    int Settlements,
    int Txns,
    string Accounts);

/// <summary>
/// 元宝寄售成交的唯一写入点（技术架构第 3、5、6 节）。全局服务只管挂单与撮合，
/// 买方金币、卖方净额、双方元宝与成交状态全在这一笔角色服事务里：
/// 要么全成，要么全退。
///
/// 元宝等量转移：卖方 -ingot_amount、买方 +ingot_amount，全库元宝总量不变。
/// 税只体现在「卖方少加那一部分」：没有任何一条语句把 tax 写进任何角色的
/// 货币行或系统账户，net = price - tax，全库金币因此净减少 tax。
/// </summary>
public sealed class IngotRepository(
    NpgsqlDataSource dataSource,
    AuctionTable table)
{
    public async Task<IngotOutcome> SettleAsync(
        long buyerId, long listingId, string txnId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        try
        {
            // 1. 锁挂单行。元宝数量、标价、卖方全从这儿读，请求里带不进来。
            long sellerId = 0;
            long ingotAmount = 0;
            long price = 0;
            var state = "missing";
            var found = false;
            await using (var read = new NpgsqlCommand(
                "select seller_id, ingot_amount, price_gold, state "
                + "from \"交易\".auction_ingot_listing where listing_id = $1 for update",
                connection, transaction))
            {
                read.Parameters.AddWithValue(listingId);
                await using var reader = await read.ExecuteReaderAsync(stopping);
                if (await reader.ReadAsync(stopping))
                {
                    sellerId = reader.GetInt64(0);
                    ingotAmount = reader.GetInt64(1);
                    price = reader.GetInt64(2);
                    state = reader.GetString(3);
                    found = true;
                }
            }

            if (!found)
            {
                throw new IngotException("listing_not_found");
            }

            if (state != "listed")
            {
                throw new IngotException("listing_sold");
            }

            if (sellerId == buyerId)
            {
                throw new IngotException("same_character");
            }

            if (ingotAmount < 1 || price < 1)
            {
                throw new IngotException("listing_invalid");
            }

            var tax = table.TaxOf(price);
            var net = price - tax;

            // 2. 抢事务号。撞主键说明这一笔已经结过，整笔退回。
            await using (var claim = new NpgsqlCommand(
                "insert into \"交易\".auction_ingot_txn (txn_id, listing_id) values ($1, $2) "
                + "on conflict (txn_id) do nothing returning txn_id",
                connection, transaction))
            {
                claim.Parameters.AddWithValue(txnId);
                claim.Parameters.AddWithValue(listingId);
                var claimed = await claim.ExecuteScalarAsync(stopping);
                if (claimed is null)
                {
                    await transaction.RollbackAsync(stopping);
                    return new IngotOutcome(false, "duplicate", listingId, 0, 0, 0, 0,
                        0, 0, 0, 0, "", true);
                }
            }

            // 3. 卖方扣元宝。挂单时没有托管，成交时再验一次：卖方把元宝花掉了就整笔退。
            await using (var debit = new NpgsqlCommand(
                "update \"角色\".currency set amount = amount + $3, updated_at = now() "
                + "where character_id = $1 and kind = $2 and amount + $3 >= 0 returning amount",
                connection, transaction))
            {
                debit.Parameters.AddWithValue(sellerId);
                debit.Parameters.AddWithValue(table.IngotKind);
                debit.Parameters.AddWithValue(-ingotAmount);
                var updated = await debit.ExecuteScalarAsync(stopping);
                if (updated is null)
                {
                    throw new IngotException("ingot_insufficient");
                }
            }

            // 4. 扣买方标价金币。带余额条件的 update，扣不动整笔回滚。
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
                    throw new IngotException("gold_insufficient");
                }
            }

            // 5. 买方得到元宝。等量转移，不销毁也不生成。
            await using (var credit = new NpgsqlCommand(
                "insert into \"角色\".currency (character_id, kind, amount) values ($1, $2, $3) "
                + "on conflict (character_id, kind) do update "
                + "set amount = \"角色\".currency.amount + excluded.amount, updated_at = now()",
                connection, transaction))
            {
                credit.Parameters.AddWithValue(buyerId);
                credit.Parameters.AddWithValue(table.IngotKind);
                credit.Parameters.AddWithValue(ingotAmount);
                await credit.ExecuteNonQueryAsync(stopping);
            }

            // 6. 卖方只到账净额。税没有对应的 insert/update：它不进任何角色、
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

            // 7. 挂单状态与成交流水在同一笔事务里，和货币一起提交。
            await using (var sold = new NpgsqlCommand(
                "update \"交易\".auction_ingot_listing set state = 'sold' "
                + "where listing_id = $1 and state = 'listed'",
                connection, transaction))
            {
                sold.Parameters.AddWithValue(listingId);
                await sold.ExecuteNonQueryAsync(stopping);
            }

            await using (var ledger = new NpgsqlCommand(
                "insert into \"交易\".auction_ingot_settlement "
                + "(txn_id, listing_id, buyer_id, seller_id, ingot_amount, price_gold, "
                + "tax_gold, net_gold) values ($1, $2, $3, $4, $5, $6, $7, $8)",
                connection, transaction))
            {
                ledger.Parameters.AddWithValue(txnId);
                ledger.Parameters.AddWithValue(listingId);
                ledger.Parameters.AddWithValue(buyerId);
                ledger.Parameters.AddWithValue(sellerId);
                ledger.Parameters.AddWithValue(ingotAmount);
                ledger.Parameters.AddWithValue(price);
                ledger.Parameters.AddWithValue(tax);
                ledger.Parameters.AddWithValue(net);
                await ledger.ExecuteNonQueryAsync(stopping);
            }

            var buyerGold = await AmountOfAsync(connection, transaction, buyerId, table.GoldKind, stopping);
            var sellerGold = await AmountOfAsync(connection, transaction, sellerId, table.GoldKind, stopping);
            var buyerIngot = await AmountOfAsync(connection, transaction, buyerId, table.IngotKind, stopping);
            var sellerIngot = await AmountOfAsync(connection, transaction, sellerId, table.IngotKind, stopping);
            await transaction.CommitAsync(stopping);

            return new IngotOutcome(true, "", listingId, ingotAmount, price, tax, net,
                buyerGold, sellerGold, buyerIngot, sellerIngot, "sold", false);
        }
        catch (IngotException ex)
        {
            // 所有 reader 都在各自的 await using 块里关掉了，这里回滚不会撞 502。
            await transaction.RollbackAsync(stopping);
            return new IngotOutcome(false, ex.Code, listingId, 0, 0, 0, 0,
                0, 0, 0, 0, "", false);
        }
    }

    /// <summary>只给核对用：不看回包，从库里独立读回成交全貌与全库总量分布。</summary>
    public async Task<IngotLedger> StateAsync(
        long buyerId, long sellerId, long listingId, string txnId, CancellationToken stopping)
    {
        var buyerGold = await SingleAsync(buyerId, table.GoldKind, stopping);
        var sellerGold = await SingleAsync(sellerId, table.GoldKind, stopping);
        var buyerIngot = await SingleAsync(buyerId, table.IngotKind, stopping);
        var sellerIngot = await SingleAsync(sellerId, table.IngotKind, stopping);

        long totalGold = 0;
        var accounts = new List<string>();
        await using (var read = dataSource.CreateCommand(
            "select character_id, amount from \"角色\".currency where kind = $1 order by character_id"))
        {
            read.Parameters.AddWithValue(table.GoldKind);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                var id = reader.GetInt64(0);
                var amount = reader.GetInt64(1);
                totalGold += amount;
                accounts.Add(id + ":" + amount);
            }
        }

        long totalIngot;
        await using (var read = dataSource.CreateCommand(
            "select coalesce(sum(amount), 0) from \"角色\".currency where kind = $1"))
        {
            read.Parameters.AddWithValue(table.IngotKind);
            totalIngot = Convert.ToInt64(await read.ExecuteScalarAsync(stopping));
        }

        var state = "missing";
        await using (var read = dataSource.CreateCommand(
            "select state from \"交易\".auction_ingot_listing where listing_id = $1"))
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
            "select price_gold, tax_gold, net_gold from \"交易\".auction_ingot_settlement "
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
            "select count(*) from \"交易\".auction_ingot_settlement where listing_id = $1",
            listingId, stopping);
        var txns = await CountAsync(
            "select count(*) from \"交易\".auction_ingot_txn where txn_id = $1",
            txnId, stopping);

        return new IngotLedger(buyerGold, sellerGold, totalGold,
            buyerIngot, sellerIngot, totalIngot, state,
            price, tax, net, settlements, txns, string.Join(",", accounts));
    }

    static async Task<long> AmountOfAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, string kind, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select amount from \"角色\".currency where character_id = $1 and kind = $2",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(kind);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value);
    }

    async Task<long> SingleAsync(long characterId, string kind, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select amount from \"角色\".currency where character_id = $1 and kind = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(kind);
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
