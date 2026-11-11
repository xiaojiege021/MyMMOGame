using Npgsql;

namespace Character;

/// <summary>
/// 一次强化转移的结果。Reason 为空表示这一笔做成了；Duplicate=true 表示事务号用过、一笔没做。
/// 方案 9.3：转移必定成功，没有掷点，也没有「失败但部分生效」这回事。
/// </summary>
public sealed record TransferOutcome(
    bool Duplicate,
    string Reason,
    int FromLevelBefore,
    int ToLevelBefore,
    int FromLevel,
    int ToLevel,
    long GoldCost,
    long Gold,
    int StoneLeft,
    bool TargetBoundBefore,
    bool TargetBound);

/// <summary>只给核对用：从库里独立读回两格等级、绑定、金币与转移石余量。</summary>
public sealed record TransferState(
    int FromLevel, int ToLevel, bool FromBound, bool ToBound, long Gold, int StoneLeft);

/// <summary>
/// 强化转移是角色服的一笔数据库事务：扣转移石、扣金币、必要时绑定目标格、
/// 改两格强化等级，全成或全退。方案 9.3 写定的规则全在这里判：
/// 来源必须高于目标、覆盖不是相加、成功后来源 +0、不限部位、转移必定成功。
/// 绑定规则在同一笔事务里完成：来源绑定时目标变为绑定（方案「绑定装备的强化
/// 只能转给绑定装备」的结果态，实现路径是紧随其后的「来源已绑定而目标未绑定时，
/// 目标变为绑定」）；非绑来源转非绑目标不动目标绑定，目标本来已绑定也不解开。
/// 两格等级、绑定状态、石头与金币费用都只在服务端读，请求里带不进来。
/// </summary>
public sealed class TransferRepository(
    NpgsqlDataSource dataSource,
    TransferTable table,
    ItemTable items)
{
    public async Task<TransferOutcome> ApplyAsync(
        long characterId,
        int fromSlot,
        int toSlot,
        string txnId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 抢号。撞主键说明这个号点过了，一笔不做。
        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".transfer_txn (txn_id, character_id, from_slot, to_slot) "
            + "values ($1, $2, $3, $4) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            claim.Parameters.AddWithValue((short)fromSlot);
            claim.Parameters.AddWithValue((short)toSlot);
            if (await claim.ExecuteScalarAsync(stopping) is null)
            {
                await transaction.RollbackAsync(stopping);
                var state = await StateAsync(characterId, fromSlot, toSlot, CancellationToken.None);
                return new TransferOutcome(true, "duplicate", state.FromLevel, state.ToLevel,
                    state.FromLevel, state.ToLevel, 0, state.Gold, state.StoneLeft,
                    state.ToBound, state.ToBound);
            }
        }

        if (fromSlot == toSlot)
        {
            throw new EnhanceException("same_slot");
        }

        // 2. 按格位升序一次锁住两行，避免两笔反向转移互相等锁。
        //    绑定状态与物品编号都在这一行上，后面改绑定不需要再读一次。
        var itemIds = new Dictionary<int, string>();
        var bounds = new Dictionary<int, bool>();
        await using (var read = new NpgsqlCommand(
            "select slot, item_id, bound from \"角色\".inventory "
            + "where character_id = $1 and slot in ($2, $3) order by slot for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue((short)fromSlot);
            read.Parameters.AddWithValue((short)toSlot);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                var slot = reader.GetInt16(0);
                itemIds[slot] = reader.GetString(1);
                bounds[slot] = reader.GetBoolean(2);
            }
        }
        // reader 关掉之后再判业务规则：Npgsql 里带着未读完的 reader 回滚会炸成 gRPC 502。

        if (!itemIds.ContainsKey(fromSlot))
        {
            throw new EnhanceException("source_empty");
        }

        if (!itemIds.ContainsKey(toSlot))
        {
            throw new EnhanceException("target_empty");
        }

        foreach (var slot in new[] { fromSlot, toSlot })
        {
            var item = items.Find(itemIds[slot]);
            if (item is null || !string.Equals(item.Kind, "equipment", StringComparison.Ordinal))
            {
                throw new EnhanceException(slot == fromSlot ? "source_not_equipment" : "target_not_equipment");
            }
        }
        // 方案 9.3「不限部位」：这里只认两格都是装备，不比较 item_id、部位或品阶。

        var levels = new Dictionary<int, int> { [fromSlot] = 0, [toSlot] = 0 };
        await using (var readLevel = new NpgsqlCommand(
            "select slot, level from \"角色\".enhance "
            + "where character_id = $1 and slot in ($2, $3)",
            connection, transaction))
        {
            readLevel.Parameters.AddWithValue(characterId);
            readLevel.Parameters.AddWithValue((short)fromSlot);
            readLevel.Parameters.AddWithValue((short)toSlot);
            await using var reader = await readLevel.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                levels[reader.GetInt16(0)] = reader.GetInt16(1);
            }
        }

        var fromLevelBefore = levels[fromSlot];
        var toLevelBefore = levels[toSlot];
        if (fromLevelBefore <= toLevelBefore)
        {
            // 方案 9.3：来源强化必须高于目标，否则不能转（避免放反）。
            // 「来源 +3、目标 +5」和两格相等都落在这里。
            throw new EnhanceException("source_not_higher");
        }

        if (fromLevelBefore > table.MaxLevel)
        {
            throw new EnhanceException("invalid_level");
        }

        var goldCost = table.GoldCostFor(fromLevelBefore);

        // 3. 扣一枚转移石。找一格够扣的锁住；扣到 0 就删行（count 有 CHECK > 0）。
        int? stoneSlot = null;
        var stoneCount = 0;
        await using (var findStone = new NpgsqlCommand(
            "select slot, count from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2 and count >= $3 "
            + "order by slot limit 1 for update",
            connection, transaction))
        {
            findStone.Parameters.AddWithValue(characterId);
            findStone.Parameters.AddWithValue(table.StoneItemId);
            findStone.Parameters.AddWithValue(table.StoneCost);
            await using var reader = await findStone.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                stoneSlot = reader.GetInt16(0);
                stoneCount = reader.GetInt32(1);
            }
        }

        if (stoneSlot is null)
        {
            throw new EnhanceException("stone_insufficient");
        }

        // inventory.count 有 CHECK (count > 0)：扣到 0 必须先删行，不能先 update 成 0。
        if (stoneCount == table.StoneCost)
        {
            await using var remove = new NpgsqlCommand(
                "delete from \"角色\".inventory where character_id = $1 and slot = $2",
                connection, transaction);
            remove.Parameters.AddWithValue(characterId);
            remove.Parameters.AddWithValue((short)stoneSlot.Value);
            await remove.ExecuteNonQueryAsync(stopping);
        }
        else
        {
            await using var consume = new NpgsqlCommand(
                "update \"角色\".inventory set count = count - $3 "
                + "where character_id = $1 and slot = $2",
                connection, transaction);
            consume.Parameters.AddWithValue(characterId);
            consume.Parameters.AddWithValue((short)stoneSlot.Value);
            consume.Parameters.AddWithValue(table.StoneCost);
            await consume.ExecuteNonQueryAsync(stopping);
        }

        // 4. 扣金币。费用按「转出的强化等级」从配置读；钱不够整笔回滚，
        //    石头也跟着退回来，绑定与等级都还没写。
        long gold;
        await using (var pay = new NpgsqlCommand(
            "update \"角色\".currency set amount = amount - $3, updated_at = now() "
            + "where character_id = $1 and kind = $2 and amount >= $3 returning amount",
            connection, transaction))
        {
            pay.Parameters.AddWithValue(characterId);
            pay.Parameters.AddWithValue(table.GoldKind);
            pay.Parameters.AddWithValue(goldCost);
            var paid = await pay.ExecuteScalarAsync(stopping);
            if (paid is null)
            {
                throw new EnhanceException("gold_insufficient");
            }

            gold = Convert.ToInt64(paid);
        }

        // 5. 绑定规则在同一笔事务里完成。来源已绑定而目标未绑定时目标变为绑定；
        //    非绑来源转非绑目标不动目标绑定；目标本来已绑定也不解开。
        //    bound / bound_at 必须成对落，库层有 CHECK 盯着。
        var targetBoundBefore = bounds[toSlot];
        var targetBound = targetBoundBefore || bounds[fromSlot];
        if (targetBound && !targetBoundBefore)
        {
            await using var bind = new NpgsqlCommand(
                "update \"角色\".inventory set bound = true, bound_at = now() "
                + "where character_id = $1 and slot = $2",
                connection, transaction);
            bind.Parameters.AddWithValue(characterId);
            bind.Parameters.AddWithValue((short)toSlot);
            await bind.ExecuteNonQueryAsync(stopping);
        }

        // 6. 覆盖：来源变 +0，目标变成来源原来的等级。目标原有的强化被覆盖、不返还。
        //    没有掷点，转移必定成功，也没有「失败累计必成」这类东西。
        foreach (var (slot, level) in new[] { (fromSlot, 0), (toSlot, fromLevelBefore) })
        {
            await using var write = new NpgsqlCommand(
                "insert into \"角色\".enhance (character_id, slot, level) values ($1, $2, $3) "
                + "on conflict (character_id, slot) do update "
                + "set level = excluded.level, updated_at = now()",
                connection, transaction);
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue((short)slot);
            write.Parameters.AddWithValue((short)level);
            await write.ExecuteNonQueryAsync(stopping);
        }

        var stoneLeft = await ItemCountAsync(connection, transaction, characterId,
            table.StoneItemId, stopping);
        await transaction.CommitAsync(stopping);

        return new TransferOutcome(false, "", fromLevelBefore, toLevelBefore,
            0, fromLevelBefore, goldCost, gold, stoneLeft, targetBoundBefore, targetBound);
    }

    static async Task<int> ItemCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string itemId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(itemId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(stopping));
    }

    /// <summary>只给核对用：不看回包，直接从库里读两格等级、绑定、金币与转移石余量。</summary>
    public async Task<TransferState> StateAsync(
        long characterId, int fromSlot, int toSlot, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);

        var levels = new Dictionary<int, int> { [fromSlot] = 0, [toSlot] = 0 };
        var bounds = new Dictionary<int, bool> { [fromSlot] = false, [toSlot] = false };
        await using (var read = new NpgsqlCommand(
            "select i.slot, i.bound, coalesce(e.level, 0) from \"角色\".inventory i "
            + "left join \"角色\".enhance e on e.character_id = i.character_id and e.slot = i.slot "
            + "where i.character_id = $1 and i.slot in ($2, $3)",
            connection))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue((short)fromSlot);
            read.Parameters.AddWithValue((short)toSlot);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                var slot = reader.GetInt16(0);
                bounds[slot] = reader.GetBoolean(1);
                levels[slot] = reader.GetInt16(2);
            }
        }

        return new TransferState(
            levels[fromSlot], levels[toSlot], bounds[fromSlot], bounds[toSlot],
            await AmountAsync(connection, characterId, table.GoldKind, stopping),
            await ItemCountAsync(connection, characterId, table.StoneItemId, stopping));
    }

    static async Task<long> AmountAsync(
        NpgsqlConnection connection, long characterId, string kind, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select amount from \"角色\".currency where character_id = $1 and kind = $2", connection);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(kind);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is null ? 0L : Convert.ToInt64(value);
    }

    static async Task<int> ItemCountAsync(
        NpgsqlConnection connection, long characterId, string itemId, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2", connection);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(itemId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(stopping));
    }

    /// <summary>只给核对用：这个转移事务号在表里有几行。</summary>
    public async Task<int> TxnCountAsync(string txnId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select count(*) from \"角色\".transfer_txn where txn_id = $1");
        command.Parameters.AddWithValue(txnId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(stopping));
    }
}
