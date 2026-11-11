using Npgsql;

namespace Global;

/// <summary>
/// 全局服务只写元宝寄售挂单：交易 schema 里的 auction_ingot_listing。
/// 元宝与金币的划转由角色服在成交时的一笔事务里写（角色服是角色数据唯一写入方），
/// 成交时角色服会锁这张表的行；两边不共用角色货币的行锁。
/// </summary>
public sealed class IngotListingRepository(NpgsqlDataSource dataSource)
{
    public sealed record Created(long ListingId, bool Duplicate);

    public async Task<Created> CreateAsync(
        long sellerId, long ingotAmount, long priceGold, string txnId,
        CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "insert into \"交易\".auction_ingot_listing "
            + "(list_txn, seller_id, ingot_amount, price_gold, state) "
            + "values ($1, $2, $3, $4, 'listed') "
            + "on conflict (list_txn) do nothing returning listing_id");
        command.Parameters.AddWithValue(txnId);
        command.Parameters.AddWithValue(sellerId);
        command.Parameters.AddWithValue(ingotAmount);
        command.Parameters.AddWithValue(priceGold);

        var value = await command.ExecuteScalarAsync(stopping);
        if (value is null || value is DBNull)
        {
            return new Created(0, true);
        }

        return new Created(Convert.ToInt64(value), false);
    }
}
