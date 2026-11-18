using Npgsql;

namespace Character;

/// <summary>只读：一条任务的奖励行（币种 = 灵石某一档 + 数量，来自 config/city_tasks.json）。</summary>
public sealed record CityTaskRewardRow(string Currency, long Amount);

/// <summary>
/// 只读：一条任务在这个角色、这个周期里的事实值。Eligible 是服务端按库里的
/// level / realm 算出来的展示事实，不是请求带进来的判定。
/// </summary>
public sealed record CityTaskRow(
    string TaskId, string Name, string CityId, string Period, int Done, int MaxPerPeriod,
    int RequireLevel, string RequireRealm, bool Eligible,
    IReadOnlyList<CityTaskRewardRow> Rewards,

    // 这一条当前周期的业务键（日常是业务日、周常是业务周首），只作核对。
    string PeriodKey);

/// <summary>只读：城池任务面板的事实值。角色不存在回 character_not_found。</summary>
public sealed record CityTaskBoard(
    bool Ok, string Reason, long CharacterId, int Level, string Realm,
    IReadOnlyList<CityTaskRow> Rows, IReadOnlyDictionary<string, long> Stones);

/// <summary>
/// Q5 完成一笔的回包事实值。业务拒绝时未生效数值给 0，reason 说明哪一种拒绝。
/// 每条奖励带 before / after 与这一笔算出的城池抽成（抽成只记账，不从玩家扣）。
/// </summary>
public sealed record CityTaskGrant(
    string Currency, long Amount, long BalanceBefore, long BalanceAfter, long TaxStone);

public sealed record CityTaskOutcome(
    bool Ok, string Reason, string TaskId, string CityId, int Done, int MaxPerPeriod,
    string PeriodKey, int TaxPct, IReadOnlyList<CityTaskGrant> Grants);

/// <summary>只读：一条任务流水（核对用）。</summary>
public sealed record CityTaskLedgerRow(
    string TxnId, string TaskId, string CityId, string Period, string PeriodKey,
    string Currency, long Amount);

/// <summary>
/// 主城修炼 / 境界任务的唯一写入点（Q5/J2）。钱只在角色服写：
///
///   1. 在同一笔事务里抢「角色」.grant_txn 事务号（与 P1-1b5 同一个发放入口），
///      同一个号重放回 duplicate，不二次发灵石。
///   2. 加灵石复用 GrantRepository.ApplyCurrencyDeltaInTransactionAsync —— 全服
///      货币加减只这一条 SQL，本卡不另开一条 UPDATE。
///   3. 城池抽成写「社交」.city_stone_tax_ledger（与金币账本分开两张表，单位不同），
///      税率读运行时「社交」.city.tax_pct，只记账、不从玩家扣。
///   4. 进度 +1 写「角色」.city_task_progress；次数上限与奖励只从
///      config/city_tasks.json 读，周期键只由 config/calendar.json 算。
///
/// 没有任何中间提交：任何一步失败整笔回滚，不会出现「灵石到账了、次数没加」，
/// 也不会出现「次数加了、账没记」。等级 / 境界门槛按库里的事实值判，请求带不进来。
/// </summary>
public sealed class CityTaskRepository(
    NpgsqlDataSource dataSource,
    CityTaskTable table,
    CalendarTable calendar,
    RealmTable realms,
    GrantRepository grants,
    CityTaxRepository cityTax)
{
    /// <summary>
    /// 只读面板：角色等级 / 境界 + 每条任务当前周期的次数 + 相关灵石档位余额。
    /// 不改库；门槛判定只在这里算展示用的 Eligible，真正的拒绝在 CompleteAsync。
    /// </summary>
    public async Task<CityTaskBoard> BoardAsync(
        long characterId, DateTimeOffset now, CancellationToken stopping)
    {
        int level;
        string realm;
        await using (var read = dataSource.CreateCommand(
            "select level, realm from \"角色\".character where id = $1"))
        {
            read.Parameters.AddWithValue(characterId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                return new CityTaskBoard(false, "character_not_found", characterId, 0, "",
                    [], new Dictionary<string, long>(StringComparer.Ordinal));
            }

            level = reader.GetInt32(0);
            realm = reader.GetString(1);
        }

        var stones = await ReadStonesAsync(dataSource, characterId, stopping);

        var rows = new List<CityTaskRow>();
        foreach (var task in table.Tasks)
        {
            var key = CityTaskTable.PeriodKey(calendar, task.Period, now);
            var done = await DoneCountAsync(dataSource, characterId, task.Id, key, stopping);
            rows.Add(new CityTaskRow(
                task.Id, task.Name, task.CityId, task.Period, done, task.MaxPerPeriod,
                task.RequireLevel, task.RequireRealm,
                Eligible(task, level, realm),
                task.Rewards.Select(r => new CityTaskRewardRow(r.Currency, r.Amount)).ToList(),
                key.ToString("yyyy-MM-dd")));
        }

        return new CityTaskBoard(true, "", characterId, level, realm, rows, stones);
    }

    /// <summary>
    /// 完成一条任务。唯一入口：网关生成 txn_id，角色服只认这个号。
    /// 业务拒绝（task_unknown / duplicate / character_not_found / level_too_low /
    /// realm_too_low / limit_reached / city_missing）都是 ok=false + reason，
    /// 拒绝时一条数值都不写。
    /// </summary>
    public async Task<CityTaskOutcome> CompleteAsync(
        long characterId, string taskId, string txnId, DateTimeOffset now, CancellationToken stopping)
    {
        var task = table.TaskFor(taskId);
        if (task is null)
        {
            return Fail("task_unknown", taskId, "");
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        try
        {
            return await CompleteInTransactionAsync(
                connection, transaction, characterId, task, txnId, now, stopping);
        }
        catch (GrantException ex)
        {
            // 钱这一次没有落地：整笔回滚，回一个业务拒绝码而不是 502。
            await transaction.RollbackAsync(stopping);
            return Fail(ex.Code, task.Id, task.CityId);
        }
    }

    async Task<CityTaskOutcome> CompleteInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        CityTaskTable.Task task,
        string txnId,
        DateTimeOffset now,
        CancellationToken stopping)
    {
        // 1. 抢号：同一个事务号已经用过就整笔不做（与 P1-1b5 的发放入口同一个号）。
        var claimed = await grants.TryClaimTxnInTransactionAsync(
            connection, transaction, characterId, txnId, stopping);
        if (!claimed)
        {
            await transaction.RollbackAsync(stopping);
            return Fail("duplicate", task.Id, task.CityId);
        }

        // 2. 锁角色行，读等级与境界。门槛按库里的事实值判，请求带不进来。
        int level;
        string realm;
        var found = false;
        await using (var read = new NpgsqlCommand(
            "select level, realm from \"角色\".character where id = $1 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                found = true;
                level = reader.GetInt32(0);
                realm = reader.GetString(1);
            }
            else
            {
                level = 0;
                realm = "";
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return Fail("character_not_found", task.Id, task.CityId);
        }

        if (level < task.RequireLevel)
        {
            await transaction.RollbackAsync(stopping);
            return Fail("level_too_low", task.Id, task.CityId);
        }

        if (task.RequireRealm.Length > 0
            && realms.Rank(realm) < realms.Rank(task.RequireRealm))
        {
            await transaction.RollbackAsync(stopping);
            return Fail("realm_too_low", task.Id, task.CityId);
        }

        // 3. 抽成比例只读运行时的城池行（城战写回会改它），请求与配置都带不进来。
        int taxPct;
        await using (var city = new NpgsqlCommand(
            "select tax_pct from \"社交\".city where city_id = $1", connection, transaction))
        {
            city.Parameters.AddWithValue(task.CityId);
            var scalar = await city.ExecuteScalarAsync(stopping);
            if (scalar is null or DBNull)
            {
                await transaction.RollbackAsync(stopping);
                return Fail("city_missing", task.Id, task.CityId);
            }

            taxPct = Convert.ToInt32(scalar);
        }

        // 4. 周期键只由日历算：日常 = 业务日，周常 = 业务周首。重置就是换键。
        var key = CityTaskTable.PeriodKey(calendar, task.Period, now);

        // 5. 进度行：先确保存在，再锁住读次数。
        await using (var seed = new NpgsqlCommand(
            "insert into \"角色\".city_task_progress "
            + "(character_id, task_id, period_key, city_id, done_count) "
            + "values ($1, $2, $3, $4, 0) on conflict (character_id, task_id, period_key) "
            + "do nothing", connection, transaction))
        {
            seed.Parameters.AddWithValue(characterId);
            seed.Parameters.AddWithValue(task.Id);
            seed.Parameters.AddWithValue(key);
            seed.Parameters.AddWithValue(task.CityId);
            await seed.ExecuteNonQueryAsync(stopping);
        }

        var done = await DoneCountInTransactionAsync(
            connection, transaction, characterId, task.Id, key, stopping);

        // 6. 次数上限只从配置来；到顶回 limit_reached，灵石、流水与进度都不动。
        if (done >= task.MaxPerPeriod)
        {
            await transaction.RollbackAsync(stopping);
            return Fail("limit_reached", task.Id, task.CityId);
        }

        // 7. 逐条奖励：读旧余额 → 加灵石（唯一货币写法）→ 读回 → 写任务流水 → 写城池抽成。
        var granted = new List<CityTaskGrant>();
        foreach (var reward in task.Rewards)
        {
            var before = await BalanceAsync(
                connection, transaction, characterId, reward.Currency, stopping);

            await grants.ApplyCurrencyDeltaInTransactionAsync(
                connection, transaction, characterId, reward.Currency, reward.Amount, stopping);

            var after = await BalanceAsync(
                connection, transaction, characterId, reward.Currency, stopping);

            await using (var ledger = new NpgsqlCommand(
                "insert into \"角色\".city_task_ledger "
                + "(txn_id, character_id, task_id, city_id, period, period_key, currency, amount) "
                + "values ($1, $2, $3, $4, $5, $6, $7, $8)",
                connection, transaction))
            {
                ledger.Parameters.AddWithValue(txnId);
                ledger.Parameters.AddWithValue(characterId);
                ledger.Parameters.AddWithValue(task.Id);
                ledger.Parameters.AddWithValue(task.CityId);
                ledger.Parameters.AddWithValue(task.Period);
                ledger.Parameters.AddWithValue(key);
                ledger.Parameters.AddWithValue(reward.Currency);
                ledger.Parameters.AddWithValue(reward.Amount);
                await ledger.ExecuteNonQueryAsync(stopping);
            }

            var taxStone = await cityTax.RecordStoneGrantInTransactionAsync(
                connection, transaction, task.CityId, txnId, reward.Currency,
                reward.Amount, stopping);

            granted.Add(new CityTaskGrant(
                reward.Currency, reward.Amount, before, after, taxStone));
        }

        // 8. 进度 +1，与灵石、两处流水同一次提交。
        await using (var advance = new NpgsqlCommand(
            "update \"角色\".city_task_progress set done_count = done_count + 1, "
            + "city_id = $4, updated_at = now() "
            + "where character_id = $1 and task_id = $2 and period_key = $3",
            connection, transaction))
        {
            advance.Parameters.AddWithValue(characterId);
            advance.Parameters.AddWithValue(task.Id);
            advance.Parameters.AddWithValue(key);
            advance.Parameters.AddWithValue(task.CityId);
            await advance.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new CityTaskOutcome(true, "", task.Id, task.CityId, done + 1,
            task.MaxPerPeriod, key.ToString("yyyy-MM-dd"), taxPct, granted);
    }

    /// <summary>只读：某个角色最近的任务流水（核对用，按时间倒序）。</summary>
    public async Task<IReadOnlyList<CityTaskLedgerRow>> LedgerAsync(
        long characterId, int limit, CancellationToken stopping)
    {
        var rows = new List<CityTaskLedgerRow>();
        await using var command = dataSource.CreateCommand(
            "select txn_id, task_id, city_id, period, period_key, currency, amount "
            + "from \"角色\".city_task_ledger where character_id = $1 "
            + "order by recorded_at desc, txn_id desc, currency limit $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            rows.Add(new CityTaskLedgerRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateOnly>(4).ToString("yyyy-MM-dd"),
                reader.GetString(5), reader.GetInt64(6)));
        }

        return rows;
    }

    /// <summary>只读：这个角色在这一刻的各条任务进度（核对用）。</summary>
    public async Task<IReadOnlyList<CityTaskRow>> ProgressAsync(
        long characterId, DateTimeOffset now, CancellationToken stopping)
    {
        var board = await BoardAsync(characterId, now, stopping);
        return board.Ok ? board.Rows : [];
    }

    bool Eligible(CityTaskTable.Task task, int level, string realm)
    {
        if (level < task.RequireLevel)
        {
            return false;
        }

        return task.RequireRealm.Length == 0
            || realms.Rank(realm) >= realms.Rank(task.RequireRealm);
    }

    /// <summary>灵石五档里，这份配置实际用到的档位余额（其余档位不返回，面板不展示无关币种）。</summary>
    async Task<Dictionary<string, long>> ReadStonesAsync(
        NpgsqlDataSource source,
        long characterId,
        CancellationToken stopping)
    {
        var kinds = table.Tasks
            .SelectMany(t => t.Rewards)
            .Select(r => r.Currency)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var kind in kinds)
        {
            map[kind] = 0;
        }

        if (kinds.Length == 0)
        {
            return map;
        }

        await using var command = source.CreateCommand(
            "select kind, amount from \"角色\".currency "
            + "where character_id = $1 and kind = any($2)");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(kinds);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            map[reader.GetString(0)] = reader.GetInt64(1);
        }

        return map;
    }

    static async Task<long> BalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string kind,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select amount from \"角色\".currency where character_id = $1 and kind = $2",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(kind);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>只读：这一条在这一周期的次数（面板用，不加锁）。</summary>
    static async Task<int> DoneCountAsync(
        NpgsqlDataSource source,
        long characterId,
        string taskId,
        DateOnly key,
        CancellationToken stopping)
    {
        await using var command = source.CreateCommand(
            "select done_count from \"角色\".city_task_progress "
            + "where character_id = $1 and task_id = $2 and period_key = $3");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(taskId);
        command.Parameters.AddWithValue(key);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>完成路径：锁住这一行读次数，与后面的 +1 同一次提交。</summary>
    static async Task<int> DoneCountInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string taskId,
        DateOnly key,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select done_count from \"角色\".city_task_progress "
            + "where character_id = $1 and task_id = $2 and period_key = $3 for update",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(taskId);
        command.Parameters.AddWithValue(key);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    static CityTaskOutcome Fail(string reason, string taskId, string cityId) =>
        new(false, reason, taskId, cityId, 0, 0, "", 0, []);
}
