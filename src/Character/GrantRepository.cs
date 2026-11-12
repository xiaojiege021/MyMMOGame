using Mmo.Proto.Character;
using Npgsql;

namespace Character;

/// <summary>发奖被业务规则挡下。Code 直接对应 gRPC 错误详情。</summary>
public sealed class GrantException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record GrantResult(
    bool Duplicate,
    Dictionary<string, long> Balances,
    List<BagSlot> Slots);

/// <summary>
/// 发一笔奖：事务号 + 若干条变更，整笔在一个数据库事务里。
///
///   1. 先抢事务号。这个号已经用过就整笔不做，回 duplicate——不发第二次。
///   2. 逐条变更：货币加减、物品入格。任何一条不满足条件就抛 GrantException，
///      事务回滚，前面已经改的也跟着退回去（全成或全退）。
///   3. 扣款用带余额条件的 update，并发两笔各扣一笔的时候只有一笔能成。
/// </summary>
public sealed class GrantRepository(
    NpgsqlDataSource dataSource,
    ItemTable items,
    CurrencyTable currencies,
    CharacterConfig config)
{
    public const int MaxTxnLength = 64;

    public async Task<GrantResult> ApplyAsync(
        long characterId,
        string txnId,
        IReadOnlyList<GrantEntry> entries,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 抢号。冲突就说明这个号用过了。
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
                return new GrantResult(
                    true,
                    await ReadBalancesAsync(connection, null, characterId, stopping),
                    await ReadSlotsAsync(connection, null, characterId, stopping));
            }
        }

        // 2. 逐条变更。任何一条失败都往外抛，事务自然回滚。
        foreach (var entry in entries)
        {
            switch (entry.BodyCase)
            {
                case GrantEntry.BodyOneofCase.Currency:
                    await ApplyCurrencyAsync(connection, transaction, characterId, entry.Currency, stopping);
                    break;
                case GrantEntry.BodyOneofCase.Item:
                    await ApplyItemAsync(connection, transaction, characterId, entry.Item, stopping);
                    break;
                default:
                    throw new GrantException("entry_missing");
            }
        }

        // 3. 读回结果再提交：读的是这一笔之后的状态。
        var balances = await ReadBalancesAsync(connection, transaction, characterId, stopping);
        var slots = await ReadSlotsAsync(connection, transaction, characterId, stopping);
        await transaction.CommitAsync(stopping);

        return new GrantResult(false, balances, slots);
    }

    async Task ApplyCurrencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        CurrencyDelta delta,
        CancellationToken stopping)
    {
        var kind = (delta.Kind ?? "").Trim();
        if (currencies.Find(kind) is null)
        {
            throw new GrantException("unknown_currency");
        }

        if (delta.Delta == 0)
        {
            throw new GrantException("delta_zero");
        }

        // 与 P1-1b4 同一套写法：加走 upsert，减走带余额条件的 update。
        // 合成一条 upsert 会让负数插入值先撞 CHECK，轮不到 DO UPDATE。
        var sql = delta.Delta > 0
            ? """
              insert into "角色".currency (character_id, kind, amount)
              values ($1, $2, $3)
              on conflict (character_id, kind) do update
                  set amount = currency.amount + excluded.amount,
                      updated_at = now()
              returning amount
              """
            : """
              update "角色".currency
                 set amount = amount + $3,
                     updated_at = now()
               where character_id = $1
                 and kind = $2
                 and amount + $3 >= 0
              returning amount
              """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(kind);
        command.Parameters.AddWithValue(delta.Delta);

        var amount = await command.ExecuteScalarAsync(stopping);
        if (amount is null)
        {
            throw new GrantException("insufficient");
        }
    }

    async Task ApplyItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        ItemDelta delta,
        CancellationToken stopping)
    {
        var item = items.Find((delta.ItemId ?? "").Trim());
        if (item is null)
        {
            throw new GrantException("unknown_item");
        }

        if (delta.Count < 1)
        {
            throw new GrantException("count_invalid");
        }

        if (delta.Count > item.StackMax)
        {
            throw new GrantException("stack_overflow");
        }

        if (delta.Slot < 1 || delta.Slot > config.BagSlots)
        {
            throw new GrantException("slot_out_of_range");
        }

        // 锁住这一格再判断：同格异物品、堆叠上限、绑定都不许被并发绕过。
        int? existingCount = null;
        string? existingItem = null;
        await using (var read = new NpgsqlCommand(
            "select item_id, count, bound from \"角色\".inventory "
            + "where character_id = $1 and slot = $2 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue((short)delta.Slot);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                existingItem = reader.GetString(0);
                existingCount = reader.GetInt32(1);
                // bound 留着不动：已经绑的不会因为再放而变回不绑。
                _ = reader.GetBoolean(2);
            }
        }

        if (existingItem is not null)
        {
            if (!string.Equals(existingItem, item.Id, StringComparison.Ordinal))
            {
                throw new GrantException("slot_occupied");
            }

            if (existingCount!.Value + delta.Count > item.StackMax)
            {
                throw new GrantException("stack_overflow");
            }

            await using var update = new NpgsqlCommand(
                "update \"角色\".inventory set count = count + $3 "
                + "where character_id = $1 and slot = $2",
                connection, transaction);
            update.Parameters.AddWithValue(characterId);
            update.Parameters.AddWithValue((short)delta.Slot);
            update.Parameters.AddWithValue(delta.Count);
            await update.ExecuteNonQueryAsync(stopping);
            return;
        }

        var bound = item.BindsOnArrival;
        await using var insert = new NpgsqlCommand(
            "insert into \"角色\".inventory (character_id, slot, item_id, count, bound, bound_at) "
            + "values ($1, $2, $3, $4, $5, case when $5 then now() else null end)",
            connection, transaction);
        insert.Parameters.AddWithValue(characterId);
        insert.Parameters.AddWithValue((short)delta.Slot);
        insert.Parameters.AddWithValue(item.Id);
        insert.Parameters.AddWithValue(delta.Count);
        insert.Parameters.AddWithValue(bound);
        await insert.ExecuteNonQueryAsync(stopping);
    }

    async Task<Dictionary<string, long>> ReadBalancesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long characterId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select kind, amount from \"角色\".currency where character_id = $1",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);

        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(stopping))
        {
            map[reader.GetString(0)] = reader.GetInt64(1);
        }

        return map;
    }

    async Task<List<BagSlot>> ReadSlotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long characterId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select slot, item_id, count, bound from \"角色\".inventory "
            + "where character_id = $1 order by slot",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);

        var list = new List<BagSlot>();
        while (await reader.ReadAsync(stopping))
        {
            list.Add(new BagSlot(reader.GetInt16(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3)));
        }

        return list;
    }

    /// <summary>只给核对用：这个事务号在表里有几行。</summary>
    public async Task<int> TxnCountAsync(string txnId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select count(*) from \"角色\".grant_txn where txn_id = $1");
        command.Parameters.AddWithValue(txnId);
        var value = await command.ExecuteScalarAsync(stopping);
        return Convert.ToInt32(value);
    }

    /// <summary>
    /// 只给核对用：这个角色名下有几笔商城成交。商城事务号带 shop_buy: 前缀，
    /// 与发奖、其它卡的写入分得开，不必为商城另建一张流水表。
    /// </summary>
    public async Task<int> ShopTxnCountAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select count(*) from \"角色\".grant_txn "
            + "where character_id = $1 and txn_id like 'shop\\_buy:%'");
        command.Parameters.AddWithValue(characterId);
        var value = await command.ExecuteScalarAsync(stopping);
        return Convert.ToInt32(value);
    }
}
