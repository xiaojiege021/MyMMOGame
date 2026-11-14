using Npgsql;

namespace Global;

/// <summary>
/// 全局服务只写灵石挂单：交易 schema 里的 stone_listing。
/// 灵石是「角色」.currency 的货币行，不在背包，所以挂单只有档位与数量，没有格位；
/// 成交时角色服在同一笔事务里转移双方灵石、扣买方金币、给卖方净额并销毁税，
/// 全局服务不碰角色货币的行锁。
/// 同一卖方的同一档同时只能挂一单，由库里的部分唯一索引保证。
/// </summary>
public sealed class StoneListingRepository(NpgsqlDataSource dataSource)
{
    public sealed record Created(long ListingId, bool Duplicate);

    public async Task<Created> CreateAsync(
        long sellerId, string stoneKind, long stoneAmount, long priceGold, string txnId,
        CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "insert into \"交易\".stone_listing "
            + "(list_txn, seller_id, stone_kind, stone_amount, price_gold, state) "
            + "values ($1, $2, $3, $4, $5, 'listed') "
            + "on conflict (list_txn) do nothing returning listing_id");
        command.Parameters.AddWithValue(txnId);
        command.Parameters.AddWithValue(sellerId);
        command.Parameters.AddWithValue(stoneKind);
        command.Parameters.AddWithValue(stoneAmount);
        command.Parameters.AddWithValue(priceGold);

        var value = await command.ExecuteScalarAsync(ct);
        if (value is null || value is DBNull)
        {
            return new Created(0, true);
        }

        return new Created(Convert.ToInt64(value), false);
    }
}
