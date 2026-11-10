using Npgsql;

namespace Character;

/// <summary>强化被业务规则挡下。Code 原样回到 EnhanceResponse.reason，不抛 gRPC 错误码。</summary>
public sealed class EnhanceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// 一次强化的结果。Reason 为空表示这一笔做成了（不管掷点成功还是失败），
/// Duplicate=true 表示事务号用过、一笔没做。
/// </summary>
public sealed record EnhanceOutcome(
    bool Duplicate,
    string Reason,
    int LevelBefore,
    int Level,
    bool Success,
    EnhanceTier? Tier,
    long Gold,
    int StoneLeft);

/// <summary>只给核对用：从库里独立读回强化等级、金币与三档石头余量。</summary>
public sealed record EnhanceState(int Level, long Gold, int Low, int Mid, int High);

/// <summary>
/// 强化是角色服的一笔数据库事务：扣石头、扣金币、改强化等级，全成或全退。
/// 当前等级、档位、材料、消耗与成功率都只在服务端算，请求里带不进来。
/// 失败只掉 1 级（最低 0），不删装备、不清零、也不记「失败几次下次必成」。
/// </summary>
public sealed class EnhanceRepository(
    NpgsqlDataSource dataSource,
    EnhanceTable table,
    ItemTable items)
{
    public async Task<EnhanceOutcome> ApplyAsync(
        long characterId,
        int slot,
        string stoneId,
        string txnId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 抢号。撞主键说明这个号点过了，一笔不做。
        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".enhance_txn (txn_id, character_id, slot) values ($1, $2, $3) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            claim.Parameters.AddWithValue((short)slot);
            if (await claim.ExecuteScalarAsync(stopping) is null)
            {
                await transaction.RollbackAsync(stopping);
                var state = await StateAsync(characterId, slot, CancellationToken.None);
                return new EnhanceOutcome(true, "duplicate", state.Level, state.Level,
                    false, null, state.Gold, 0);
            }
        }

        // 2. 锁住装备这一格再读当前强化等级。所有强化都先拿这把行锁，
        //    同一格并发点两次也不会各自按旧等级算。
        string? itemId = null;
        var levelBefore = 0;
        await using (var read = new NpgsqlCommand(
            "select i.item_id, coalesce(e.level, 0) from \"角色\".inventory i "
            + "left join \"角色\".enhance e on e.character_id = i.character_id and e.slot = i.slot "
            + "where i.character_id = $1 and i.slot = $2 for update of i",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue((short)slot);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                itemId = reader.GetString(0);
                levelBefore = reader.GetInt16(1);
            }
        }
        // reader 关掉之后再判业务规则：Npgsql 里带着未读完的 reader 回滚会炸成 gRPC 502。

        if (itemId is null)
        {
            throw new EnhanceException("slot_empty");
        }

        var item = items.Find(itemId);
        if (item is null || !string.Equals(item.Kind, "equipment", StringComparison.Ordinal))
        {
            throw new EnhanceException("not_equipment");
        }

        var target = levelBefore + 1;
        if (target > table.MaxLevel)
        {
            throw new EnhanceException("at_cap");
        }

        var tier = table.TierFor(target);
        if (!string.Equals((stoneId ?? "").Trim(), tier.StoneId, StringComparison.Ordinal))
        {
            // 方案 9.2：低级石不能代替中级 / 高级石。用低级石点 +7 就落在这里。
            throw new EnhanceException("wrong_stone");
        }

        // 3. 扣石头。找一格够扣的锁住；扣到 0 就删行（count 有 CHECK > 0）。
        int? stoneSlot = null;
        await using (var findStone = new NpgsqlCommand(
            "select slot from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2 and count >= $3 "
            + "order by slot limit 1 for update",
            connection, transaction))
        {
            findStone.Parameters.AddWithValue(characterId);
            findStone.Parameters.AddWithValue(tier.StoneId);
            findStone.Parameters.AddWithValue(tier.StoneCost);
            await using var reader = await findStone.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                stoneSlot = reader.GetInt16(0);
            }
        }

        if (stoneSlot is null)
        {
            throw new EnhanceException("stone_insufficient");
        }

        int leftInSlot;
        await using (var consume = new NpgsqlCommand(
            "update \"角色\".inventory set count = count - $3 "
            + "where character_id = $1 and slot = $2 returning count",
            connection, transaction))
        {
            consume.Parameters.AddWithValue(characterId);
            consume.Parameters.AddWithValue((short)stoneSlot.Value);
            consume.Parameters.AddWithValue(tier.StoneCost);
            leftInSlot = Convert.ToInt32(await consume.ExecuteScalarAsync(stopping));
        }

        if (leftInSlot == 0)
        {
            await using var remove = new NpgsqlCommand(
                "delete from \"角色\".inventory where character_id = $1 and slot = $2",
                connection, transaction);
            remove.Parameters.AddWithValue(characterId);
            remove.Parameters.AddWithValue((short)stoneSlot.Value);
            await remove.ExecuteNonQueryAsync(stopping);
        }

        // 4. 扣金币。带余额条件的 update，钱不够整笔回滚，石头也跟着退回去。
        long gold;
        await using (var pay = new NpgsqlCommand(
            "update \"角色\".currency set amount = amount - $3, updated_at = now() "
            + "where character_id = $1 and kind = $2 and amount >= $3 returning amount",
            connection, transaction))
        {
            pay.Parameters.AddWithValue(characterId);
            pay.Parameters.AddWithValue(table.GoldKind);
            pay.Parameters.AddWithValue(tier.GoldCost);
            var paid = await pay.ExecuteScalarAsync(stopping);
            if (paid is null)
            {
                throw new EnhanceException("gold_insufficient");
            }

            gold = Convert.ToInt64(paid);
        }

        // 5. 掷点只在服务端。失败掉 1 级，最低 0；没有失败累计必成。
        var success = Random.Shared.NextDouble() < tier.SuccessRate;
        var level = success ? target : Math.Max(0, levelBefore - 1);

        await using (var write = new NpgsqlCommand(
            "insert into \"角色\".enhance (character_id, slot, level) values ($1, $2, $3) "
            + "on conflict (character_id, slot) do update "
            + "set level = excluded.level, updated_at = now()",
            connection, transaction))
        {
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue((short)slot);
            write.Parameters.AddWithValue((short)level);
            await write.ExecuteNonQueryAsync(stopping);
        }

        var stoneLeft = await StoneCountAsync(connection, transaction, characterId, tier.StoneId, stopping);
        await transaction.CommitAsync(stopping);

        return new EnhanceOutcome(false, "", levelBefore, level, success, tier, gold, stoneLeft);
    }

    async Task<int> StoneCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string stoneId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(stoneId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(stopping));
    }

    /// <summary>只给核对用：不看回包，直接从库里读状态。</summary>
    public async Task<EnhanceState> StateAsync(long characterId, int slot, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);

        var level = 0;
        await using (var read = new NpgsqlCommand(
            "select level from \"角色\".enhance where character_id = $1 and slot = $2",
            connection))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue((short)slot);
            var value = await read.ExecuteScalarAsync(stopping);
            if (value is not null)
            {
                level = Convert.ToInt32(value);
            }
        }

        return new EnhanceState(
            level,
            await AmountAsync(connection, characterId, table.GoldKind, stopping),
            await CountAsync(connection, characterId, table.Tiers[0].StoneId, stopping),
            await CountAsync(connection, characterId, table.Tiers[1].StoneId, stopping),
            await CountAsync(connection, characterId, table.Tiers[2].StoneId, stopping));
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

    static async Task<int> CountAsync(
        NpgsqlConnection connection, long characterId, string itemId, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2", connection);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(itemId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(stopping));
    }

    /// <summary>只给核对用：这个事务号在表里有几行。</summary>
    public async Task<int> TxnCountAsync(string txnId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select count(*) from \"角色\".enhance_txn where txn_id = $1");
        command.Parameters.AddWithValue(txnId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(stopping));
    }

    /// <summary>只给核对用：绕开业务层直写等级，验证库层 CHECK 挡得住越界。</summary>
    public async Task RawWriteLevelAsync(long characterId, int slot, int level, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "insert into \"角色\".enhance (character_id, slot, level) values ($1, $2, $3) "
            + "on conflict (character_id, slot) do update set level = excluded.level");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue((short)slot);
        command.Parameters.AddWithValue((short)level);
        await command.ExecuteNonQueryAsync(stopping);
    }
}
