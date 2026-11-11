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
    int StoneLeft,
    bool ProtectedLevel,
    int CouponUsed,
    int CouponLeft);

/// <summary>只给核对用：从库里独立读回强化等级、金币与三档石头余量。</summary>
public sealed record EnhanceState(int Level, long Gold, int Low, int Mid, int High);

/// <summary>
/// 强化是角色服的一笔数据库事务：扣石头、扣金币、必要时扣保护券、改强化等级，全成或全退。
/// 当前等级、档位、材料、消耗与成功率都只在服务端算，请求里带不进来。
/// 失败只掉 1 级（最低 0），不删装备、不清零、也不记「失败几次下次必成」。
/// P1-4b：券只挡掉级。掷点只用同一个 tier.SuccessRate，带券不另开一套概率；
/// 券只在真的挡下一次掉级时被扣掉，成功那一手不消耗。券按物品编号认，不看来源。
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
        bool useCoupon,
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
                    false, null, state.Gold, 0, false, 0, 0);
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

        // 3. 用券：先按唯一物品编号锁住一格券，确认这一手有得用。券只在失败时真的扣，
        //    这里不扣，免得成功那一手白掉一张。券的「来源」在库里根本不存在，
        //    商城和通行证以后发的是同一个 item_id，用的时候不看来源。
        int? couponSlot = null;
        var couponCount = 0;
        if (useCoupon)
        {
            await using (var findCoupon = new NpgsqlCommand(
                "select slot, count from \"角色\".inventory "
                + "where character_id = $1 and item_id = $2 and count >= 1 "
                + "order by slot limit 1 for update",
                connection, transaction))
            {
                findCoupon.Parameters.AddWithValue(characterId);
                findCoupon.Parameters.AddWithValue(table.ProtectItemId);
                await using var reader = await findCoupon.ExecuteReaderAsync(stopping);
                if (await reader.ReadAsync(stopping))
                {
                    couponSlot = reader.GetInt16(0);
                    couponCount = reader.GetInt32(1);
                }
            }

            if (couponSlot is null)
            {
                throw new EnhanceException("coupon_insufficient");
            }
        }

        // 4. 扣石头。找一格够扣的锁住；扣到 0 就删行（count 有 CHECK > 0）。
        int? stoneSlot = null;
        var stoneCount = 0;
        await using (var findStone = new NpgsqlCommand(
            "select slot, count from \"角色\".inventory "
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
                stoneCount = reader.GetInt32(1);
            }
        }

        if (stoneSlot is null)
        {
            throw new EnhanceException("stone_insufficient");
        }

        // inventory.count 有 CHECK (count > 0)：扣到 0 必须先删行，不能先 update 成 0
        // 再删（PostgreSQL 在 UPDATE 当下就检查约束，会整笔抛异常回滚）。
        if (stoneCount == tier.StoneCost)
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
            consume.Parameters.AddWithValue(tier.StoneCost);
            await consume.ExecuteNonQueryAsync(stopping);
        }

        // 5. 扣金币。带余额条件的 update，钱不够整笔回滚，石头也跟着退回去。
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

        // 6. 掷点只在服务端，带券与不带券共用这一行、同一个 tier.SuccessRate：
        //    券不提高成功率，也没有「必成券」。失败时若用了券，只把掉级挡住，等级不变。
        var success = Random.Shared.NextDouble() < tier.SuccessRate;
        var protectedLevel = !success && useCoupon;
        var level = success ? target
            : protectedLevel ? levelBefore
            : Math.Max(0, levelBefore - 1);

        if (protectedLevel)
        {
            // 券只在真的挡下一次掉级时用掉一张；成功那一手不消耗。
            // 和扣石头、扣金币、写等级在同一笔事务里，任何一步失败整笔回滚。
            // 最后一张直接删行：inventory.count 有 CHECK (count > 0)，update 成 0 会被库层挡下。
            if (couponCount == 1)
            {
                await using var removeCoupon = new NpgsqlCommand(
                    "delete from \"角色\".inventory where character_id = $1 and slot = $2",
                    connection, transaction);
                removeCoupon.Parameters.AddWithValue(characterId);
                removeCoupon.Parameters.AddWithValue((short)couponSlot!.Value);
                await removeCoupon.ExecuteNonQueryAsync(stopping);
            }
            else
            {
                await using var consumeCoupon = new NpgsqlCommand(
                    "update \"角色\".inventory set count = count - 1 "
                    + "where character_id = $1 and slot = $2",
                    connection, transaction);
                consumeCoupon.Parameters.AddWithValue(characterId);
                consumeCoupon.Parameters.AddWithValue((short)couponSlot!.Value);
                await consumeCoupon.ExecuteNonQueryAsync(stopping);
            }
        }

        // 7. 写等级。没有失败累计必成，券不写进任何「下次必成」的计数里。
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

        var stoneLeft = await ItemCountAsync(connection, transaction, characterId, tier.StoneId, stopping);
        var couponLeft = await ItemCountAsync(
            connection, transaction, characterId, table.ProtectItemId, stopping);
        await transaction.CommitAsync(stopping);

        return new EnhanceOutcome(false, "", levelBefore, level, success, tier, gold, stoneLeft,
            protectedLevel, protectedLevel ? 1 : 0, couponLeft);
    }

    async Task<int> ItemCountAsync(
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

    /// <summary>只给核对用：从库里独立读回保护券张数，不看强化回包。</summary>
    public async Task<int> CouponCountAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(table.ProtectItemId);
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
