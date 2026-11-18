using Npgsql;

using CalendarTable = Mmo.Shared.Domain.CalendarTable;
namespace Social;

/// <summary>只读：一条任务在这个角色、这个周期里的已完成次数。</summary>
public sealed record SectTaskRow(
    string TaskId, string Name, string Period, int Done, int MaxPerPeriod,
    int RequireLevel, long RewardContribution, long RewardConstruction,

    // 这一条当前周期的业务键（日常是业务日、周常是业务周首），只作核对。
    string PeriodKey);

/// <summary>只读：宗门任务面板的事实值。找不到宗门 / 不是成员时分别是 sect_not_found / not_member。</summary>
public sealed record SectTaskBoard(
    bool Ok, string Reason, long SectId, int Level, long Construction,
    long Contribution, IReadOnlyList<SectTaskRow> Rows);

/// <summary>
/// Q4 完成一笔的回包事实值。业务拒绝时未生效数值给 0，回包 reason 说明哪一种拒绝。
/// granted_* 是这一笔实际加的数量（来自配置），before / after 是库里的事实值。
/// </summary>
public sealed record SectTaskOutcome(
    bool Ok, string Reason, long SectId, string TaskId, int Done, int MaxPerPeriod,
    long ContributionBefore, long ContributionAfter,
    long ConstructionBefore, long ConstructionAfter,
    long GrantedContribution, long GrantedConstruction, string PeriodKey);

/// <summary>只读：一条任务流水（核对用）。</summary>
public sealed record SectTaskLedgerRow(
    string TxnId, long SectId, long CharacterId, string TaskId, string Period,
    string PeriodKey, long Contribution, long Construction);

/// <summary>
/// 宗门日常 / 周常的唯一写入点（Q4/J1）。只写「社交」schema 的三张表：
/// sect_task_progress / sect_task_ledger / sect_contribution，外加 sect.construction 一列；
/// 不 join 角色 schema，不动角色货币、背包、拍卖与订单。
///
/// 完成一笔是一笔事务：锁宗门行 → 验名家（掌门或名册）→ 验角色等级 → 算周期键 →
/// 进度 +1 → 插流水（txn_id 主键挡重放）→ 加建设 → 加个人贡献 → 提交。
/// 没有中间提交：任何一步失败整笔回滚，不会出现「次数加了、建设没加」。
///
/// 周期键只由 <see cref="SectTaskTable.PeriodKey"/>（背后是 config/calendar.json）算；
/// 次数上限与奖励只从 config/sect_tasks.json 读。库里不出现第二套上限 / 小时数 /
/// 奖励数字，也不出现数据库当天 / 周首函数。
/// </summary>
public sealed class SectTaskRepository(
    NpgsqlDataSource dataSource, SectTaskTable table, CalendarTable calendar)
{
    /// <summary>
    /// 只读面板：宗门等级 / 建设 / 这个角色的累计贡献 + 每条任务当前周期的次数。
    /// 不是成员（含掌门本人）回 not_member，不改库。
    /// </summary>
    public async Task<SectTaskBoard> BoardAsync(
        long sectId, long characterId, DateTimeOffset now, CancellationToken stopping)
    {
        int level = 0;
        long construction = 0, contribution = 0;
        bool found = false, member = false;

        await using (var command = dataSource.CreateCommand(
            "select s.level, s.construction, coalesce(c.contribution, 0), "
            + "(s.master_character_id = $2 or exists ("
            + "select 1 from \"社交\".sect_member m "
            + "where m.sect_id = s.id and m.character_id = $2)) "
            + "from \"社交\".sect s "
            + "left join \"社交\".sect_contribution c "
            + "on c.sect_id = s.id and c.character_id = $2 "
            + "where s.id = $1"))
        {
            command.Parameters.AddWithValue(sectId);
            command.Parameters.AddWithValue(characterId);
            await using var reader = await command.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                found = true;
                level = reader.GetInt32(0);
                construction = reader.GetInt64(1);
                contribution = reader.GetInt64(2);
                member = reader.GetBoolean(3);
            }
        }

        if (!found)
        {
            return new SectTaskBoard(false, "sect_not_found", sectId, 0, 0, 0, []);
        }

        if (!member)
        {
            return new SectTaskBoard(false, "not_member", sectId, level, construction, 0, []);
        }

        var rows = new List<SectTaskRow>();
        foreach (var task in table.Tasks)
        {
            var key = SectTaskTable.PeriodKey(calendar, task.Period, now);
            var done = 0;
            await using (var count = dataSource.CreateCommand(
                "select done_count from \"社交\".sect_task_progress "
                + "where character_id = $1 and task_id = $2 and period_key = $3"))
            {
                count.Parameters.AddWithValue(characterId);
                count.Parameters.AddWithValue(task.Id);
                count.Parameters.AddWithValue(key);
                var v = await count.ExecuteScalarAsync(stopping);
                if (v is not null && v != DBNull.Value)
                {
                    done = Convert.ToInt32(v);
                }
            }

            rows.Add(new SectTaskRow(task.Id, task.Name, task.Period, done,
                task.MaxPerPeriod, task.RequireLevel, task.Contribution, task.Construction,
                key.ToString("yyyy-MM-dd")));
        }

        return new SectTaskBoard(true, "", sectId, level, construction, contribution, rows);
    }

    /// <summary>
    /// 完成一条任务。唯一入口：网关生成 txn_id，社交服只认这个号；同一号重放回
    /// duplicate，不二次计次、不二次加建设、不二次加贡献。
    /// </summary>
    public async Task<SectTaskOutcome> CompleteAsync(
        long sectId, long characterId, int characterLevel, string taskId, string txnId,
        DateTimeOffset now, CancellationToken stopping)
    {
        var task = table.TaskFor(taskId);
        if (task is null)
        {
            return Fail("task_unknown", sectId, taskId);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 0. 幂等早判：同一事务号已经完成过，直接回 duplicate（不再看次数上限）。
        //    并发两笔同一号仍由第 7 步的 txn_id 主键兜底：晚到的那笔拿不到行。
        await using (var dupe = new NpgsqlCommand(
            "select 1 from \"社交\".sect_task_ledger where txn_id = $1 for update",
            connection, transaction))
        {
            dupe.Parameters.AddWithValue(txnId);
            if (await dupe.ExecuteScalarAsync(stopping) is not null)
            {
                await transaction.RollbackAsync(stopping);
                return Fail("duplicate", sectId, taskId);
            }
        }

        // 1. 锁宗门行：读等级 / 建设 / 掌门。并发完成同一宗门的任务在这里串行，
        //    建设与进度不会丢更新；也顺手把「宗门不存在」判掉。
        int level = 0;
        long construction = 0, master = 0;
        var found = false;
        await using (var sect = new NpgsqlCommand(
            "select level, construction, master_character_id from \"社交\".sect "
            + "where id = $1 for update", connection, transaction))
        {
            sect.Parameters.AddWithValue(sectId);
            await using var reader = await sect.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                found = true;
                level = reader.GetInt32(0);
                construction = reader.GetInt64(1);
                master = reader.GetInt64(2);
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return Fail("sect_not_found", sectId, taskId);
        }

        // 2. 名家：掌门本人可以不在名册，所以两条都看（与 a3 的权限口径一致）。
        var member = master == characterId;
        if (!member)
        {
            await using var check = new NpgsqlCommand(
                "select 1 from \"社交\".sect_member where sect_id = $1 and character_id = $2",
                connection, transaction);
            check.Parameters.AddWithValue(sectId);
            check.Parameters.AddWithValue(characterId);
            member = await check.ExecuteScalarAsync(stopping) is not null;
        }

        if (!member)
        {
            await transaction.RollbackAsync(stopping);
            return Fail("not_member", sectId, taskId);
        }

        // 3. 等级门槛：角色等级由角色服的只读战斗快照给（服务层查好传进来），
        //    请求里带不进来；不够就一个字不写。
        if (characterLevel < task.RequireLevel)
        {
            await transaction.RollbackAsync(stopping);
            return Fail("level_too_low", sectId, taskId);
        }

        // 4. 周期键只由日历算：日常 = 业务日，周常 = 业务周首。重置就是换键。
        var key = SectTaskTable.PeriodKey(calendar, task.Period, now);

        // 5. 进度行：先确保存在，再锁住读次数（宗门行已锁，这里是同宗门的第二道串行）。
        await using (var seed = new NpgsqlCommand(
            "insert into \"社交\".sect_task_progress "
            + "(character_id, task_id, period_key, sect_id, done_count) "
            + "values ($1, $2, $3, $4, 0) on conflict (character_id, task_id, period_key) "
            + "do nothing", connection, transaction))
        {
            seed.Parameters.AddWithValue(characterId);
            seed.Parameters.AddWithValue(task.Id);
            seed.Parameters.AddWithValue(key);
            seed.Parameters.AddWithValue(sectId);
            await seed.ExecuteNonQueryAsync(stopping);
        }

        var done = 0;
        await using (var progress = new NpgsqlCommand(
            "select done_count from \"社交\".sect_task_progress "
            + "where character_id = $1 and task_id = $2 and period_key = $3 for update",
            connection, transaction))
        {
            progress.Parameters.AddWithValue(characterId);
            progress.Parameters.AddWithValue(task.Id);
            progress.Parameters.AddWithValue(key);
            done = Convert.ToInt32(await progress.ExecuteScalarAsync(stopping));
        }

        // 6. 次数上限只从配置来；到顶回 limit_reached，进度、建设、贡献都不动。
        if (done >= task.MaxPerPeriod)
        {
            await transaction.RollbackAsync(stopping);
            return Fail("limit_reached", sectId, taskId);
        }

        // 7. 流水用 txn_id 主键挡重放：同一号第二个事务在这里拿到 0 行 → duplicate。
        //    先插流水再写三处数值，重复提交连一次加法都不会做。
        var inserted = 0;
        await using (var ledger = new NpgsqlCommand(
            "insert into \"社交\".sect_task_ledger "
            + "(txn_id, sect_id, character_id, task_id, period, period_key, "
            + "contribution, construction) "
            + "values ($1, $2, $3, $4, $5, $6, $7, $8) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            ledger.Parameters.AddWithValue(txnId);
            ledger.Parameters.AddWithValue(sectId);
            ledger.Parameters.AddWithValue(characterId);
            ledger.Parameters.AddWithValue(task.Id);
            ledger.Parameters.AddWithValue(task.Period);
            ledger.Parameters.AddWithValue(key);
            ledger.Parameters.AddWithValue(task.Contribution);
            ledger.Parameters.AddWithValue(task.Construction);
            inserted = await ledger.ExecuteScalarAsync(stopping) is null ? 0 : 1;
        }

        if (inserted == 0)
        {
            await transaction.RollbackAsync(stopping);
            return Fail("duplicate", sectId, taskId);
        }

        // 8. 个人在这个宗门的累计贡献（读旧值给回包，再累加）。
        long contributionBefore = 0;
        await using (var contribution = new NpgsqlCommand(
            "select contribution from \"社交\".sect_contribution "
            + "where sect_id = $1 and character_id = $2", connection, transaction))
        {
            contribution.Parameters.AddWithValue(sectId);
            contribution.Parameters.AddWithValue(characterId);
            var v = await contribution.ExecuteScalarAsync(stopping);
            if (v is not null && v != DBNull.Value)
            {
                contributionBefore = Convert.ToInt64(v);
            }
        }

        await using (var bump = new NpgsqlCommand(
            "insert into \"社交\".sect_contribution (sect_id, character_id, contribution) "
            + "values ($1, $2, $3) on conflict (sect_id, character_id) do update "
            + "set contribution = \"社交\".sect_contribution.contribution + excluded.contribution, "
            + "updated_at = now()", connection, transaction))
        {
            bump.Parameters.AddWithValue(sectId);
            bump.Parameters.AddWithValue(characterId);
            bump.Parameters.AddWithValue(task.Contribution);
            await bump.ExecuteNonQueryAsync(stopping);
        }

        // 9. 宗门建设累加（升级入口 P2-1a4 只扣这一列；任务只加）。
        await using (var build = new NpgsqlCommand(
            "update \"社交\".sect set construction = construction + $2 where id = $1",
            connection, transaction))
        {
            build.Parameters.AddWithValue(sectId);
            build.Parameters.AddWithValue(task.Construction);
            await build.ExecuteNonQueryAsync(stopping);
        }

        // 10. 进度 +1，与上面三处同一次提交。
        await using (var advance = new NpgsqlCommand(
            "update \"社交\".sect_task_progress set done_count = done_count + 1, "
            + "sect_id = $4, updated_at = now() "
            + "where character_id = $1 and task_id = $2 and period_key = $3",
            connection, transaction))
        {
            advance.Parameters.AddWithValue(characterId);
            advance.Parameters.AddWithValue(task.Id);
            advance.Parameters.AddWithValue(key);
            advance.Parameters.AddWithValue(sectId);
            await advance.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new SectTaskOutcome(true, "", sectId, task.Id, done + 1, task.MaxPerPeriod,
            contributionBefore, contributionBefore + task.Contribution,
            construction, construction + task.Construction,
            task.Contribution, task.Construction, key.ToString("yyyy-MM-dd"));
    }

    /// <summary>只读：某宗门最近的完成流水（核对用，按时间倒序）。</summary>
    public async Task<IReadOnlyList<SectTaskLedgerRow>> LedgerAsync(
        long sectId, int limit, CancellationToken stopping)
    {
        var rows = new List<SectTaskLedgerRow>();
        await using var command = dataSource.CreateCommand(
            "select txn_id, sect_id, character_id, task_id, period, period_key, "
            + "contribution, construction from \"社交\".sect_task_ledger "
            + "where sect_id = $1 order by recorded_at desc, txn_id desc limit $2");
        command.Parameters.AddWithValue(sectId);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            rows.Add(new SectTaskLedgerRow(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                reader.GetString(4), reader.GetFieldValue<DateOnly>(5).ToString("yyyy-MM-dd"),
                reader.GetInt64(6), reader.GetInt64(7)));
        }

        return rows;
    }

    static SectTaskOutcome Fail(string reason, long sectId, string taskId) =>
        new(false, reason, sectId, taskId, 0, 0, 0, 0, 0, 0, 0, 0, "");
}
