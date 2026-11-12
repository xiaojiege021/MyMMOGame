using Npgsql;

namespace Character;

/// <summary>一次经验结算的结果。Duplicate 表示这个事务号已经发过，本次没有再发。</summary>
public sealed record ExpSettlementOutcome(
    bool Found,
    bool Duplicate,
    long BaseExp,
    long PartyBonus,
    long ExpGain,
    int LevelBefore,
    int Level,
    long Exp);

/// <summary>
/// 等级经验的唯一写入点。活动、副本、任务、竞技场、战场、城战和世界 Boss
/// 都调同一笔：基础经验 + （同一场至少两人时）一截少量加成，只加经验。
///
/// 事务顺序与前面的发奖/进本一致：锁角色行 → 抢事务号 → 只写 level 与 exp → 提交。
/// 抢不到号（重复提交）整笔回滚；并发两笔不同号冲同一角色由行锁串行，
/// 不会把这一笔算丢。等级到 level_cap 就不再升，库层 CHECK 仍在。
///
/// account 为空表示服间可信调用（战斗服通关后直连），只按角色编号归属；
/// 网关转发时一定带账号，where 里再核一次，别的账号改不到这个角色。
/// 这一笔只 set level / exp 与写 exp_settlement 流水：疲劳、周份额、货币、
/// 背包、进本记录一概不碰。
/// </summary>
public sealed class ExpSettlementRepository(NpgsqlDataSource dataSource)
{
    public const int MaxTxnLength = 128;
    public const int MaxSourceLength = 32;

    public async Task<ExpSettlementOutcome> SettleAsync(
        long characterId,
        string? account,
        string txnId,
        string source,
        int partySize,
        ExpTable table,
        CancellationToken stopping)
    {
        var baseExp = table.BaseExp;
        var bonus = table.PartyBonus(partySize);
        var gain = baseExp + bonus;

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁角色行并核归属。带账号的走网关口径，空账号走服间口径。
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        int levelBefore = 0;
        long expBefore = 0;
        var found = false;
        await using (var read = new NpgsqlCommand(
            "select level, exp from \"角色\".character " + owned,
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                read.Parameters.AddWithValue(account);
            }

            // 先把 reader 读完关掉再回滚：连接上还挂着 reader 时发 rollback
            // 会抛异常，那样「别人的角色」会被当成服务端内部错误而不是 404。
            await using (var reader = await read.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    levelBefore = reader.GetInt32(0);
                    expBefore = reader.GetInt64(1);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new ExpSettlementOutcome(false, false, 0, 0, 0, 0, 0, 0);
        }

        var (level, exp) = table.Apply(levelBefore, expBefore, gain);

        // 2. 抢事务号。同一个号已经发过就整笔不做，返回当时的状态。
        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".exp_settlement "
            + "(txn_id, character_id, source, party_size, base_exp, party_bonus, exp_gain, "
            + " level_before, level_after) "
            + "values ($1, $2, $3, $4, $5, $6, $7, $8, $9) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            claim.Parameters.AddWithValue(source);
            claim.Parameters.AddWithValue(partySize);
            claim.Parameters.AddWithValue(baseExp);
            claim.Parameters.AddWithValue(bonus);
            claim.Parameters.AddWithValue(gain);
            claim.Parameters.AddWithValue(levelBefore);
            claim.Parameters.AddWithValue(level);
            var claimed = await claim.ExecuteScalarAsync(stopping);
            if (claimed is null)
            {
                await transaction.RollbackAsync(stopping);
                return new ExpSettlementOutcome(
                    true, true, 0, 0, 0, levelBefore, levelBefore, expBefore);
            }
        }

        // 3. 只写这两列。越界等级写不进去，事务回滚后流水也不会留下。
        await using (var write = new NpgsqlCommand(
            "update \"角色\".character set level = $2, exp = $3 where id = $1",
            connection, transaction))
        {
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue(level);
            write.Parameters.AddWithValue(exp);
            await write.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new ExpSettlementOutcome(
            true, false, baseExp, bonus, gain, levelBefore, level, exp);
    }

    /// <summary>只给核对用：读等级、当前级经验、结算笔数与累计入账经验。</summary>
    public async Task<(int Level, long Exp, int Rows, long TotalGain)?> ExpStateAsync(
        long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select c.level, c.exp, "
            + "(select count(*) from \"角色\".exp_settlement s where s.character_id = c.id), "
            + "(select coalesce(sum(s.exp_gain), 0) from \"角色\".exp_settlement s where s.character_id = c.id) "
            + "from \"角色\".character c where c.id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3));
    }

    /// <summary>只给核对用：绕过接口直写经验，验库层的非负 CHECK。</summary>
    public async Task RawWriteExpAsync(long characterId, long exp, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set exp = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(exp);
        await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>
    /// P1-5c6：用掉一张复活卷轴。方案 8.3 / 12.1 写定「拉起只扣当前等级已有经验
    /// 的一部分，当前级经验是零时这次不扣，不掉级」「冷却记在当前玩家身上，
    /// 跨场保留」「比例按一转基础，不吃生息二转减免」。
    ///
    /// 等级经验的写入全部集中在这个 Repository：上面 SettleAsync 是「加」，
    /// 这里是「减」，两条都只写 `character.exp`，**都不写 level**——等级不会掉，
    /// 上一等级的经验也不会被动到。经验比例从 config/classes.json 的一转基础比例
    /// 传进来（调用方读配置），这里不写第二套数。
    ///
    /// 一张卷轴 = 一笔事务：锁角色行 → 抢事务号 → 查全局冷却 → 扣一张 →
    /// 扣当前级经验 → 写冷却到期 → 提交。角色行锁把同一角色的两次用卷串行，
    /// 所以「两个人同时在两场里各点一张」也只有第一笔能过冷却检查。
    /// 任何一条拒绝路径都在扣之前回滚：卷轴、经验、冷却一个字都不写。
    /// </summary>
    public async Task<ReviveScrollOutcome> UseReviveScrollAsync(
        long characterId,
        string? account,
        string txnId,
        string itemId,
        int expCostPct,
        int cooldownMs,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁角色行并核归属。带账号的走网关口径，空账号走服间口径。
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        int level = 0;
        long exp = 0;
        var found = false;
        await using (var read = new NpgsqlCommand(
            "select level, exp from \"角色\".character " + owned,
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                read.Parameters.AddWithValue(account);
            }

            // 先把 reader 读完关掉再回滚，连接上挂着 reader 时 rollback 会抛异常。
            await using (var reader = await read.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    level = reader.GetInt32(0);
                    exp = reader.GetInt64(1);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new ReviveScrollOutcome(false, "character_not_found", 0, 0, 0, 0, 0, false);
        }

        // 2. 抢事务号。同一个号已经用过就整笔不做，返回「重放」让调用方别再加血。
        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".grant_txn (txn_id, character_id) values ($1, $2) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            if (await claim.ExecuteScalarAsync(stopping) is null)
            {
                await transaction.RollbackAsync(stopping);
                return new ReviveScrollOutcome(
                    true, "duplicate", 0, 0, level, exp, 0, true);
            }
        }

        // 3. 全局冷却：这一行在角色库里，所以换一场、换进程、重启都还在。
        long readyAtMs = 0;
        await using (var cooldown = new NpgsqlCommand(
            "select extract(epoch from r.ready_at) * 1000, "
            + "(r.ready_at > now()) "
            + "from \"角色\".revive_scroll_cooldown r where r.character_id = $1",
            connection, transaction))
        {
            cooldown.Parameters.AddWithValue(characterId);
            var cooling = false;
            await using (var reader = await cooldown.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    readyAtMs = (long)Math.Round(Convert.ToDouble(reader.GetValue(0)));
                    cooling = Convert.ToBoolean(reader.GetValue(1));
                }
            }

            if (cooling)
            {
                // 冷却没到：卷轴、经验、冷却都不动，整笔回滚。
                await transaction.RollbackAsync(stopping);
                return new ReviveScrollOutcome(
                    true, "cooldown", 0, 0, level, exp, readyAtMs, false);
            }
        }

        // 4. 扣一张：count > 1 就减一，等于 1 直接删（表上有 CHECK (count > 0)）。
        int slot;
        int count;
        await using (var bag = new NpgsqlCommand(
            "select slot, count from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2 order by slot limit 1 for update",
            connection, transaction))
        {
            bag.Parameters.AddWithValue(characterId);
            bag.Parameters.AddWithValue(itemId);
            var empty = false;
            slot = 0;
            count = 0;
            await using (var reader = await bag.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    slot = reader.GetInt16(0);
                    count = reader.GetInt32(1);
                }
                else
                {
                    empty = true;
                }
            }

            if (empty)
            {
                await transaction.RollbackAsync(stopping);
                return new ReviveScrollOutcome(
                    true, "no_scroll", 0, 0, level, exp, readyAtMs, false);
            }
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
            if (await take.ExecuteNonQueryAsync(stopping) != 1)
            {
                await transaction.RollbackAsync(stopping);
                return new ReviveScrollOutcome(
                    true, "no_scroll", 0, 0, level, exp, readyAtMs, false);
            }
        }

        // 5. 只扣当前等级已有经验的一部分。当前级经验是零时 cost = 0，
        //    这一句就是 exp = exp - 0；level 一次都不写，所以不掉级。
        var cost = exp * expCostPct / 100;
        await using (var deduct = new NpgsqlCommand(
            "update \"角色\".character set exp = exp - $2 where id = $1",
            connection, transaction))
        {
            deduct.Parameters.AddWithValue(characterId);
            deduct.Parameters.AddWithValue(cost);
            await deduct.ExecuteNonQueryAsync(stopping);
        }

        // 6. 写冷却到期。到期时刻按数据库时钟算，不受战斗服时钟影响；
        //    同一个角色再点一张时，上面第 3 步会读到这一行并回滚整笔。
        await using (var writeCooldown = new NpgsqlCommand(
            "insert into \"角色\".revive_scroll_cooldown "
            + "(character_id, ready_at, used_at, last_txn_id) "
            + "values ($1, now() + ($2::double precision * interval '1 millisecond'), now(), $3) "
            + "on conflict (character_id) do update set "
            + "ready_at = excluded.ready_at, used_at = excluded.used_at, "
            + "last_txn_id = excluded.last_txn_id",
            connection, transaction))
        {
            writeCooldown.Parameters.AddWithValue(characterId);
            writeCooldown.Parameters.AddWithValue(cooldownMs);
            writeCooldown.Parameters.AddWithValue(txnId);
            await writeCooldown.ExecuteNonQueryAsync(stopping);
        }

        // 7. 读回这一笔之后的事实值，一起提交。
        long left;
        long newReadyAtMs;
        long expAfter;
        await using (var state = new NpgsqlCommand(
            "select "
            + "(select coalesce(sum(i.count), 0) from \"角色\".inventory i "
            + " where i.character_id = $1 and i.item_id = $2), "
            + "(select extract(epoch from r.ready_at) * 1000 "
            + " from \"角色\".revive_scroll_cooldown r where r.character_id = $1), "
            + "(select c.exp from \"角色\".character c where c.id = $1)",
            connection, transaction))
        {
            state.Parameters.AddWithValue(characterId);
            state.Parameters.AddWithValue(itemId);
            await using var reader = await state.ExecuteReaderAsync(stopping);
            await reader.ReadAsync(stopping);
            left = Convert.ToInt64(reader.GetValue(0));
            newReadyAtMs = (long)Math.Round(Convert.ToDouble(reader.GetValue(1)));
            expAfter = reader.GetInt64(2);
        }

        await transaction.CommitAsync(stopping);
        return new ReviveScrollOutcome(
            true, "", left, cost, level, expAfter, newReadyAtMs, false);
    }
}

/// <summary>
/// 一笔用卷的结果。Reason 空串表示真扣掉了；其余路径整笔没执行，
/// 卷轴、经验、冷却都不动。Level 恒是扣之前的等级（这条不写 level）。
/// </summary>
public sealed record ReviveScrollOutcome(
    bool Found,
    string Reason,
    long CountLeft,
    long ExpCost,
    int Level,
    long ExpAfter,
    long ReadyAtMs,
    bool Duplicate);
