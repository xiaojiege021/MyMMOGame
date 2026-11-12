using Npgsql;

namespace Character;

/// <summary>
/// 一个人当前的限时经验道具状态，全部只读：背包余量与绑定态、角色库里的到期时刻、
/// 上一次用掉的时刻、数据库当前时刻。「剩余时间」就是 expires_at - now()，
/// 不另存一列；到期判定用数据库时钟，换进程、重启角色服都还是同一个答案。
/// </summary>
public sealed record ExpBoosterState(
    bool Found,
    long Count,
    bool AllBound,
    long ExpiresAtMs,
    long UsedAtMs,
    long NowMs)
{
    public bool Active => Found && ExpiresAtMs > NowMs;
    public long RemainingMs => Math.Max(0L, ExpiresAtMs - NowMs);
}

/// <summary>一笔用道具的结果。Reason 空串表示真扣掉了一张；其余路径整笔没做。</summary>
public sealed record ExpBoosterUseOutcome(
    bool Found,
    string Reason,
    long CountLeft,
    long ExpiresAtMs,
    long UsedAtMs,
    long NowMs,
    bool Duplicate);

/// <summary>
/// 限时经验道具状态的唯一读取点与唯一写入点。
/// `--exp-booster-state` 只读入口与结算事务都读同一张表；用掉一张这一笔事务
/// 只在 UseAsync 里：锁角色行 → 抢 `grant_txn` 事务号 → 扣一张 → upsert 到期时刻 →
/// 读回事实值 → 提交。库里主键是 character_id，所以「同时只生效一个」是表结构保证的；
/// 第二次用只 update 同一行的 `expires_at = now() + duration_ms`，不累加剩余时间，
/// 表里也没有倍率列，倍率只从配置读一个，叠不起来。
///
/// 等级经验的写入不在这里：结算那一笔仍是 `ExpSettlementRepository.SettleAsync`，
/// 它只在这里读「这一刻有没有生效」，不写这张表。
/// </summary>
public sealed class ExpBoosterRepository(NpgsqlDataSource dataSource)
{
    public async Task<ExpBoosterState> StateAsync(
        long characterId, string itemId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select "
            + "coalesce((select sum(i.count) from \"角色\".inventory i "
            + "          where i.character_id = c.id and i.item_id = $2), 0), "
            + "coalesce((select bool_and(i.bound) from \"角色\".inventory i "
            + "          where i.character_id = c.id and i.item_id = $2), false), "
            + "coalesce((select extract(epoch from b.expires_at) * 1000 "
            + "          from \"角色\".exp_booster b where b.character_id = c.id), 0), "
            + "coalesce((select extract(epoch from b.used_at) * 1000 "
            + "          from \"角色\".exp_booster b where b.character_id = c.id), 0), "
            + "extract(epoch from now()) * 1000 "
            + "from \"角色\".character c where c.id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(itemId);

        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return new ExpBoosterState(false, 0, false, 0, 0, 0);
        }

        // extract(epoch ...) 是 numeric，不按 double 取，统一走 Convert。
        return new ExpBoosterState(
            true,
            Convert.ToInt64(reader.GetValue(0)),
            Convert.ToBoolean(reader.GetValue(1)),
            (long)Math.Round(Convert.ToDouble(reader.GetValue(2))),
            (long)Math.Round(Convert.ToDouble(reader.GetValue(3))),
            (long)Math.Round(Convert.ToDouble(reader.GetValue(4))));
    }

    /// <summary>
    /// 用掉一张。account 为空表示服间可信调用，只按角色编号归属；网关转发一定带账号。
    /// durationMs 从配置传进来，这里不写第二套数。同一个 txnId 重放整笔不做。
    /// </summary>
    public async Task<ExpBoosterUseOutcome> UseAsync(
        long characterId,
        string? account,
        string txnId,
        string itemId,
        int durationMs,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁角色行并核归属。与结算、用卷同一口径，角色行锁把同一角色的
        //    两次使用串行，第二次只会刷新同一行，不会插出第二条生效记录。
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        var found = false;
        await using (var read = new NpgsqlCommand(
            "select id from \"角色\".character " + owned,
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                read.Parameters.AddWithValue(account);
            }

            // 先把 reader 读完关掉再回滚，连接上挂着 reader 时 rollback 会抛异常。
            await using var reader = await read.ExecuteReaderAsync(stopping);
            found = await reader.ReadAsync(stopping);
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new ExpBoosterUseOutcome(false, "character_not_found", 0, 0, 0, 0, false);
        }

        // 2. 抢事务号。跨进程消耗品扣减统一复用 grant_txn，同号重放整笔不做。
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
                return new ExpBoosterUseOutcome(true, "duplicate", 0, 0, 0, 0, true);
            }
        }

        // 3. 扣一张：count > 1 就减一，等于 1 直接删（表上有 CHECK (count > 0)）。
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
                return new ExpBoosterUseOutcome(true, "no_booster", 0, 0, 0, 0, false);
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
                return new ExpBoosterUseOutcome(true, "no_booster", 0, 0, 0, 0, false);
            }
        }

        // 4. 写到期时刻。只刷新，不累加：expires_at 恒等于 now() + duration_ms，
        //    不会读旧的 expires_at 再加一段。倍率不写进库，结算时读配置那一个。
        await using (var write = new NpgsqlCommand(
            "insert into \"角色\".exp_booster "
            + "(character_id, expires_at, used_at, last_txn_id) "
            + "values ($1, now() + ($2::double precision * interval '1 millisecond'), now(), $3) "
            + "on conflict (character_id) do update set "
            + "expires_at = excluded.expires_at, used_at = excluded.used_at, "
            + "last_txn_id = excluded.last_txn_id",
            connection, transaction))
        {
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue(durationMs);
            write.Parameters.AddWithValue(txnId);
            await write.ExecuteNonQueryAsync(stopping);
        }

        // 5. 读回这一笔之后的事实值，一起提交。
        long left;
        long expiresAtMs;
        long usedAtMs;
        long nowMs;
        await using (var state = new NpgsqlCommand(
            "select "
            + "(select coalesce(sum(i.count), 0) from \"角色\".inventory i "
            + " where i.character_id = $1 and i.item_id = $2), "
            + "(select extract(epoch from b.expires_at) * 1000 "
            + " from \"角色\".exp_booster b where b.character_id = $1), "
            + "(select extract(epoch from b.used_at) * 1000 "
            + " from \"角色\".exp_booster b where b.character_id = $1), "
            + "extract(epoch from now()) * 1000",
            connection, transaction))
        {
            state.Parameters.AddWithValue(characterId);
            state.Parameters.AddWithValue(itemId);
            await using var reader = await state.ExecuteReaderAsync(stopping);
            await reader.ReadAsync(stopping);
            left = Convert.ToInt64(reader.GetValue(0));
            expiresAtMs = (long)Math.Round(Convert.ToDouble(reader.GetValue(1)));
            usedAtMs = (long)Math.Round(Convert.ToDouble(reader.GetValue(2)));
            nowMs = (long)Math.Round(Convert.ToDouble(reader.GetValue(3)));
        }

        await transaction.CommitAsync(stopping);
        return new ExpBoosterUseOutcome(
            true, "", left, expiresAtMs, usedAtMs, nowMs, false);
    }
}
