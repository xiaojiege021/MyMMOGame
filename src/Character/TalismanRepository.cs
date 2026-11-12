using Npgsql;

namespace Character;

/// <summary>用掉一张符的结果。Reason 为空、Ok 为真表示真扣掉了。</summary>
public sealed record TalismanSpend(
    bool Ok,
    string Reason,
    string ItemId,
    long CountLeft,
    int Slot,
    bool Duplicate);

/// <summary>
/// 在战斗里用掉一张红符时，角色服这边只做一件事：把背包里的那一张扣掉。
///
/// 生命不在这里——当前血量是战斗进程里的战斗态，角色库不存。所以这一笔
/// 不改血、不改属性、不碰货币，只扣物品；回复值与冷却由配置回包带回去，
/// 战斗服照着加。
///
///   1. 抢事务号（复用 P1-1b5 的 grant_txn）：同一个号重放整笔不做，
///      同一个号不会把一张符扣成两次。
///   2. 锁住那一格再判断：count &gt; 1 就减一，等于 1 就直接删行——
///      表上有 CHECK (count &gt; 0)，留一行 0 是插不进去也留不住的。
///   3. 扣完读回这个物品的总张数一起提交，调用方拿到的是这一笔之后的事实值。
/// </summary>
public sealed class TalismanRepository(NpgsqlDataSource dataSource)
{
    public const int MaxTxnLength = 64;

    public async Task<TalismanSpend> SpendAsync(
        long characterId,
        string itemId,
        string txnId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 抢号。冲突就说明这个号用过了，整笔不做。
        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".grant_txn (txn_id, character_id) values ($1, $2) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            var claimed = await claim.ExecuteScalarAsync(stopping);
            if (claimed is null)
            {
                await transaction.RollbackAsync(stopping);
                var replayLeft = await CountAsync(characterId, itemId, stopping);
                return new TalismanSpend(true, "", itemId, replayLeft, 0, true);
            }
        }

        // 2. 锁住一格再判断。红符是可叠的消耗品，正常只有一格；
        //    多格是历史数据，按格子编号取第一格扣。
        int slot;
        int count;
        await using (var read = new NpgsqlCommand(
            "select slot, count from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2 order by slot limit 1 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue(itemId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                // 先关 reader 再回滚：没读完就 rollback 会让这一路回包变成 502。
                await reader.DisposeAsync();
                await transaction.RollbackAsync(stopping);
                return new TalismanSpend(false, "no_talisman", itemId, 0, 0, false);
            }

            slot = reader.GetInt16(0);
            count = reader.GetInt32(1);
        }

        await using (var take = new NpgsqlCommand(
            count > 1
                ? "update \"角色\".inventory set count = count - 1 "
                  + "where character_id = $1 and slot = $2 and count > 1"
                : "delete from \"角色\".inventory "
                  + "where character_id = $1 and slot = $2 and count = 1",
            connection, transaction))
        {
            take.Parameters.AddWithValue(characterId);
            take.Parameters.AddWithValue((short)slot);
            var changed = await take.ExecuteNonQueryAsync(stopping);
            if (changed != 1)
            {
                // 并发把这一格改掉了：整笔退回，不给「扣了但没扣到」的中间态。
                await transaction.RollbackAsync(stopping);
                return new TalismanSpend(false, "no_talisman", itemId, 0, 0, false);
            }
        }

        // 3. 扣完读回总张数一起提交。
        long left;
        await using (var sum = new NpgsqlCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2",
            connection, transaction))
        {
            sum.Parameters.AddWithValue(characterId);
            sum.Parameters.AddWithValue(itemId);
            left = Convert.ToInt64(await sum.ExecuteScalarAsync(stopping) ?? 0L);
        }

        await transaction.CommitAsync(stopping);
        return new TalismanSpend(true, "", itemId, left, slot, false);
    }

    /// <summary>只读：这个角色背包里有几张这个物品。拒绝路径与只读入口都用它。</summary>
    public async Task<long> CountAsync(long characterId, string itemId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(itemId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(stopping) ?? 0L);
    }
}
