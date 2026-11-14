using Npgsql;

namespace Character;

/// <summary>灵石成交结算的结果，字段与 StoneAuctionSettleResponse 一一对应。</summary>
public sealed record StoneAuctionOutcome(
    bool Ok,
    string Reason,
    long ListingId,
    string StoneKind,
    long StoneAmount,
    long PriceGold,
    long TaxGold,
    long NetGold,
    long BuyerGold,
    long SellerGold,
    long BuyerStone,
    long SellerStone,
    int TaxRatePct,
    string State,
    bool Duplicate);

/// <summary>灵石成交被业务规则挡下。Code 直接回给调用方，转成 ok=false + reason。</summary>
public sealed class StoneAuctionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// 任务发放的灵石成交结算的唯一写入点（P3-2a）。全局服务只管挂单与撮合，
/// 双方灵石、买方金币、卖方净额、挂单状态与成交流水全在这一笔角色服事务里。
///
/// 税复用 P1-5a 的 AuctionTable.TaxOf：net = price - tax，没有任何一条语句把 tax
/// 写进任何角色或系统账户，全库金币净减少这一笔。城池税收是另一张账本，
/// 与这里的销毁税分开记账。任何拒绝整笔回滚。
/// </summary>
public sealed class StoneAuctionRepository(
    NpgsqlDataSource dataSource,
    AuctionTable auction,
    StoneExchangeTable stones,
    CharacterConfig config)
{
    // 启动配置随仓储一起注入，保持与角色服其它仓储同一个形状；这里不另起一套数值。
    public CharacterConfig Config { get; } = config;

    public async Task<StoneAuctionOutcome> SettleAsync(
        long buyerId, long listingId, string txnId, CancellationToken stopping)
    {
        var txn = (txnId ?? "").Trim();
        var stoneKind = "";

        if (txn.Length == 0)
        {
            return Rejected("txn_missing", listingId, stoneKind, false);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        try
        {
            // 1. 锁挂单行。档位、数量、标价、卖方全从这儿读，请求里带不进来。
            long sellerId = 0;
            long stoneAmount = 0;
            long price = 0;
            var state = "missing";
            var found = false;
            await using (var read = new NpgsqlCommand(
                "select seller_id, stone_kind, stone_amount, price_gold, state "
                + "from \"交易\".stone_listing where listing_id = $1 for update",
                connection, transaction))
            {
                read.Parameters.AddWithValue(listingId);
                await using var reader = await read.ExecuteReaderAsync(stopping);
                if (await reader.ReadAsync(stopping))
                {
                    sellerId = reader.GetInt64(0);
                    stoneKind = reader.GetString(1);
                    stoneAmount = reader.GetInt64(2);
                    price = reader.GetInt64(3);
                    state = reader.GetString(4);
                    found = true;
                }
            }

            if (!found)
            {
                throw new StoneAuctionException("listing_not_found");
            }

            if (state != "listed")
            {
                throw new StoneAuctionException("listing_sold");
            }

            if (sellerId == buyerId)
            {
                throw new StoneAuctionException("same_character");
            }

            if (stoneAmount < 1 || price < 1)
            {
                throw new StoneAuctionException("listing_invalid");
            }

            if (stones.IndexOf(stoneKind) < 0)
            {
                throw new StoneAuctionException("unknown_stone");
            }

            // 2. 抢事务号。撞主键说明这一笔已经结过，整笔退回。
            await using (var claim = new NpgsqlCommand(
                "insert into \"交易\".stone_auction_txn (txn_id, listing_id) values ($1, $2) "
                + "on conflict (txn_id) do nothing returning txn_id",
                connection, transaction))
            {
                claim.Parameters.AddWithValue(txn);
                claim.Parameters.AddWithValue(listingId);
                if (await claim.ExecuteScalarAsync(stopping) is null)
                {
                    await transaction.RollbackAsync(stopping);
                    return Rejected("duplicate", listingId, stoneKind, true);
                }
            }

            // 3. 税与净额复用 P1-5a 的销毁税判定，不另写税率。
            var tax = auction.TaxOf(price);
            var net = price - tax;

            // 4. 扣卖方该档灵石，带余额条件的 update，扣不动整笔回滚。
            await using (var debit = new NpgsqlCommand(
                "update \"角色\".currency set amount = amount + $3, updated_at = now() "
                + "where character_id = $1 and kind = $2 and amount + $3 >= 0 returning amount",
                connection, transaction))
            {
                debit.Parameters.AddWithValue(sellerId);
                debit.Parameters.AddWithValue(stoneKind);
                debit.Parameters.AddWithValue(-stoneAmount);
                if (await debit.ExecuteScalarAsync(stopping) is null)
                {
                    throw new StoneAuctionException("stone_insufficient");
                }
            }

            // 5. upsert 给买方加该档灵石（等量转移，不进背包）。
            await using (var credit = new NpgsqlCommand(
                "insert into \"角色\".currency (character_id, kind, amount) values ($1, $2, $3) "
                + "on conflict (character_id, kind) do update "
                + "set amount = \"角色\".currency.amount + excluded.amount, updated_at = now()",
                connection, transaction))
            {
                credit.Parameters.AddWithValue(buyerId);
                credit.Parameters.AddWithValue(stoneKind);
                credit.Parameters.AddWithValue(stoneAmount);
                await credit.ExecuteNonQueryAsync(stopping);
            }

            // 6. 扣买方标价金币。
            await using (var debit = new NpgsqlCommand(
                "update \"角色\".currency set amount = amount + $3, updated_at = now() "
                + "where character_id = $1 and kind = $2 and amount + $3 >= 0 returning amount",
                connection, transaction))
            {
                debit.Parameters.AddWithValue(buyerId);
                debit.Parameters.AddWithValue(auction.GoldKind);
                debit.Parameters.AddWithValue(-price);
                if (await debit.ExecuteScalarAsync(stopping) is null)
                {
                    throw new StoneAuctionException("gold_insufficient");
                }
            }

            // 7. 卖方只到账净额。税没有对应的 insert/update：它不进任何角色，
            //    也不进系统账户，全库金币因此净减少这一笔。
            await using (var credit = new NpgsqlCommand(
                "insert into \"角色\".currency (character_id, kind, amount) values ($1, $2, $3) "
                + "on conflict (character_id, kind) do update "
                + "set amount = \"角色\".currency.amount + excluded.amount, updated_at = now()",
                connection, transaction))
            {
                credit.Parameters.AddWithValue(sellerId);
                credit.Parameters.AddWithValue(auction.GoldKind);
                credit.Parameters.AddWithValue(net);
                await credit.ExecuteNonQueryAsync(stopping);
            }

            // 8. 挂单状态与成交流水在同一笔事务里，和货币一起提交。
            await using (var sold = new NpgsqlCommand(
                "update \"交易\".stone_listing set state = 'sold' "
                + "where listing_id = $1 and state = 'listed'",
                connection, transaction))
            {
                sold.Parameters.AddWithValue(listingId);
                await sold.ExecuteNonQueryAsync(stopping);
            }

            await using (var ledger = new NpgsqlCommand(
                "insert into \"交易\".stone_settlement "
                + "(txn_id, listing_id, buyer_id, seller_id, stone_kind, stone_amount, "
                + "price_gold, tax_gold, net_gold) "
                + "values ($1, $2, $3, $4, $5, $6, $7, $8, $9)",
                connection, transaction))
            {
                ledger.Parameters.AddWithValue(txn);
                ledger.Parameters.AddWithValue(listingId);
                ledger.Parameters.AddWithValue(buyerId);
                ledger.Parameters.AddWithValue(sellerId);
                ledger.Parameters.AddWithValue(stoneKind);
                ledger.Parameters.AddWithValue(stoneAmount);
                ledger.Parameters.AddWithValue(price);
                ledger.Parameters.AddWithValue(tax);
                ledger.Parameters.AddWithValue(net);
                await ledger.ExecuteNonQueryAsync(stopping);
            }

            // 9. 事务内读回四个余额（提交之前，仍在同一笔事务里）。
            var buyerGold = await AmountOfAsync(
                connection, transaction, buyerId, auction.GoldKind, stopping);
            var sellerGold = await AmountOfAsync(
                connection, transaction, sellerId, auction.GoldKind, stopping);
            var buyerStone = await AmountOfAsync(
                connection, transaction, buyerId, stoneKind, stopping);
            var sellerStone = await AmountOfAsync(
                connection, transaction, sellerId, stoneKind, stopping);

            await transaction.CommitAsync(stopping);

            return new StoneAuctionOutcome(true, "", listingId, stoneKind, stoneAmount,
                price, tax, net, buyerGold, sellerGold, buyerStone, sellerStone,
                auction.TaxRatePct, "sold", false);
        }
        catch (StoneAuctionException ex)
        {
            // 所有 reader 都在各自的 await using 块里关掉了；这里只回滚、不读库。
            await transaction.RollbackAsync(stopping);
            return Rejected(ex.Code, listingId, stoneKind, false);
        }
    }

    StoneAuctionOutcome Rejected(string reason, long listingId, string kind, bool duplicate) =>
        new(false, reason, listingId, kind, 0, 0, 0, 0, 0, 0, 0, 0,
            auction.TaxRatePct, "", duplicate);

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
}
