using Mmo.Proto.Character;
using Npgsql;

namespace Character;

/// <summary>制作被业务规则挡下。Code 原样回到回包的 reason，不抛 gRPC 错误码。</summary>
public sealed class CraftException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>一件正在做的活。ReadyAt 之前 finish 只能被拒，不会提前掷点。</summary>
public sealed record CraftJobFacts(
    string RecipeId,
    string Profession,
    int CraftTimeS,
    DateTimeOffset StartedAt,
    DateTimeOffset ReadyAt,
    int RemainingSeconds);

/// <summary>
/// 一次收取的结果。Duplicate=true 表示这个事务号用过、一笔没做；
/// Success=false 且 Ok=true 是「做了但没成」——材料与费用照扣，产出为 0，
/// 与「被规则拒绝、一个字节都没动」是两回事。
/// </summary>
public sealed record CraftFinishOutcome(
    bool Duplicate,
    string RecipeId,
    string Profession,
    bool Success,
    string OutputItemId,
    int OutputCount,
    double SuccessPct,
    int CraftTimeS,
    DateTimeOffset ReadyAt);

/// <summary>只给核对用：一条制作流水。</summary>
public sealed record CraftLogRow(
    string TxnId, string Action, string RecipeId, string Profession, bool Success,
    string OutputItemId, int OutputCount, DateTimeOffset CreatedAt);

/// <summary>只给核对用：这个角色当前的一件活、流水、货币与背包。</summary>
public sealed record CraftStateFacts(
    CraftJobFacts? Job,
    IReadOnlyList<CraftLogRow> Logs,
    IReadOnlyList<(string Kind, long Amount)> Currencies,
    IReadOnlyList<BagSlot> Slots);

/// <summary>
/// 生产 / 配方的唯一写入点（K3，开发架构 14.7 顺序 7）。一次制作拆成两笔事务：
///
///   start：抢事务号 → 锁角色行读等级 → 扣材料 → 扣费用 → 检查产出放得下 → 写 craft_job。
///          材料、费用、活三样同一条事务，任何一步不满足整笔回滚，拒绝时数值一个字节都不动。
///   finish：抢事务号 → 锁 craft_job → 没过 ready_at 拒绝 → 掷 success_pct →
///          成功才入包 → 写 craft_log 结果 → 删 craft_job。失败也记流水，但不返材料、不给产出。
///
/// 掷点只在角色服、只在 finish 这一手；成功率、材料、产出、费用、耗时、解锁等级
/// 都从 RecipeTable 读，请求里带不进来。产出入包复用 GrantRepository 的同一条 SQL
/// （ApplyItemDeltasInTransactionAsync），不另开一条物品写入。
/// </summary>
public sealed class CraftRepository(
    NpgsqlDataSource dataSource,
    RecipeTable table,
    ItemTable items,
    CharacterConfig config,
    GrantRepository grants,
    InventoryRepository inventory)
{
    /// <summary>开始一件活。返回这一件活的 ready_at 与剩余秒数（数据库时钟）。</summary>
    public async Task<CraftJobFacts> StartAsync(
        long characterId,
        string recipeId,
        string txnId,
        CancellationToken stopping)
    {
        var recipe = table.Find(recipeId)
            ?? throw new CraftException("unknown_recipe");

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 抢号。这个号用过就整笔不做，不扣第二次材料。
        if (!await ClaimLogAsync(
                connection, transaction, txnId, characterId, "start", recipe, stopping))
        {
            await transaction.RollbackAsync(stopping);
            throw new CraftException("duplicate");
        }

        // 2. 锁角色行再读等级：解锁等级只在这里判，请求里带不进来。
        //    锁住这一行也让同一角色的两笔 start 排队，第二条会看到第一条写的活。
        int level;
        await using (var read = new NpgsqlCommand(
            "select level from \"角色\".character where id = $1 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            var value = await read.ExecuteScalarAsync(stopping);
            if (value is null)
            {
                await transaction.RollbackAsync(stopping);
                throw new CraftException("character_not_found");
            }

            level = Convert.ToInt32(value);
        }

        if (level < recipe.UnlockLevel)
        {
            await transaction.RollbackAsync(stopping);
            throw new CraftException("level_too_low");
        }

        // 3. 一个角色同时只能做一件；上一件没收就拒绝。
        if (await JobExistsAsync(connection, transaction, characterId, stopping))
        {
            await transaction.RollbackAsync(stopping);
            throw new CraftException("craft_in_progress");
        }

        // 4. 扣材料。任何一条不够都整笔回滚；这一步还没写任何东西。
        foreach (var input in recipe.Inputs)
        {
            var enough = await inventory.ConsumeInTransactionAsync(
                connection, transaction, characterId, input.ItemId, input.Count, stopping);
            if (!enough)
            {
                await transaction.RollbackAsync(stopping);
                throw new CraftException("material_insufficient");
            }
        }

        // 5. 扣费用。钱只走 GrantRepository 的那一条 SQL；余额不够会抛 insufficient，
        //    石头 / 材料跟着这一笔一起退回去。
        foreach (var fee in recipe.Fee)
        {
            if (fee.Amount == 0)
            {
                continue;
            }

            try
            {
                await grants.ApplyCurrencyDeltaInTransactionAsync(
                    connection, transaction, characterId, fee.Kind, -fee.Amount, stopping);
            }
            catch (GrantException ex) when (string.Equals(ex.Code, "insufficient", StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(stopping);
                throw new CraftException("fee_insufficient");
            }
            catch (GrantException ex)
            {
                await transaction.RollbackAsync(stopping);
                throw new CraftException(ex.Code);
            }
        }

        // 6. 产出必须放得下。材料先扣，扣出来的空格可以给产出用；
        //    放不下就整笔回滚，材料与费用一起退。
        await PlanOutputAsync(connection, transaction, characterId, recipe, stopping);

        // 7. 写这一件活。ready_at 用数据库时钟算，成败不看应用进程的时钟。
        DateTimeOffset startedAt;
        DateTimeOffset readyAt;
        int remaining;
        await using (var insert = new NpgsqlCommand(
            "insert into \"角色\".craft_job "
            + "(character_id, recipe_id, start_txn_id, started_at, ready_at) "
            + "values ($1, $2, $3, now(), now() + make_interval(secs => $4::double precision)) "
            + "returning started_at, ready_at, "
            + "greatest(0, ceil(extract(epoch from ready_at - now())))::int",
            connection, transaction))
        {
            insert.Parameters.AddWithValue(characterId);
            insert.Parameters.AddWithValue(recipe.Id);
            insert.Parameters.AddWithValue(txnId);
            insert.Parameters.AddWithValue(recipe.CraftTimeS);
            await using var reader = await insert.ExecuteReaderAsync(stopping);
            await reader.ReadAsync(stopping);
            startedAt = reader.GetFieldValue<DateTimeOffset>(0);
            readyAt = reader.GetFieldValue<DateTimeOffset>(1);
            remaining = reader.GetInt32(2);
        }

        await transaction.CommitAsync(stopping);
        return new CraftJobFacts(
            recipe.Id, recipe.Profession, recipe.CraftTimeS, startedAt, readyAt, remaining);
    }

    /// <summary>收取。过了 ready_at 才掷 success_pct；失败也写流水、不返材料。</summary>
    public async Task<CraftFinishOutcome> FinishAsync(
        long characterId,
        string txnId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 同一个号再提交：一笔不做。先读一遍是为了「活已经收了」的重放也能认出来。
        if (await LogExistsAsync(connection, transaction, txnId, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return DuplicateOutcome();
        }

        // 2. 锁这一件活。没有活就拒绝；没过 ready_at 也拒绝，且不掷点。
        //    先把 reader 关掉再回滚：Npgsql 不允许 reader 未关就 RollbackAsync，
        //    那样会抛 NpgsqlOperationInProgressException，返给网关的是 db_error 而不是
        //    no_craft，客户端拿不到一条可判定的业务拒绝。
        string recipeId;
        DateTimeOffset readyAt;
        int remaining;
        bool jobFound;
        await using (var read = new NpgsqlCommand(
            "select recipe_id, ready_at, "
            + "greatest(0, ceil(extract(epoch from ready_at - now())))::int "
            + "from \"角色\".craft_job where character_id = $1 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            jobFound = await reader.ReadAsync(stopping);
            if (jobFound)
            {
                recipeId = reader.GetString(0);
                readyAt = reader.GetFieldValue<DateTimeOffset>(1);
                remaining = reader.GetInt32(2);
            }
            else
            {
                // 没有活时这三个值用不到；显式给默认值，别让「读没读到」靠异常表达。
                recipeId = "";
                readyAt = default;
                remaining = 0;
            }
        }

        if (!jobFound)
        {
            await transaction.RollbackAsync(stopping);
            throw new CraftException("no_craft");
        }

        if (remaining > 0)
        {
            await transaction.RollbackAsync(stopping);
            throw new CraftException("craft_not_ready");
        }

        // 配置在制作途中被改 / 被删：这一件活先留着，等表恢复再收，不从回包里猜。
        var recipe = table.Find(recipeId)
            ?? throw new CraftException("recipe_unknown");

        // 3. 抢这一手的号。并发两个 finish 只有一个能写成。
        if (!await ClaimLogAsync(
                connection, transaction, txnId, characterId, "finish", recipe, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return DuplicateOutcome();
        }

        // 4. 先确认产出放得下，再掷点。放不下整笔回滚、活留在原地，玩家清格子再来收。
        var deltas = await PlanOutputAsync(connection, transaction, characterId, recipe, stopping);

        // 5. 掷点只在服务端这一行：成功率只来自 config/recipes.json。
        var success = Random.Shared.NextDouble() * 100.0 < recipe.SuccessPct;

        if (success)
        {
            try
            {
                await grants.ApplyItemDeltasInTransactionAsync(
                    connection, transaction, characterId, deltas, stopping);
            }
            catch (GrantException ex)
            {
                await transaction.RollbackAsync(stopping);
                throw new CraftException(
                    string.Equals(ex.Code, "bag_full", StringComparison.Ordinal)
                        || string.Equals(ex.Code, "slot_occupied", StringComparison.Ordinal)
                        || string.Equals(ex.Code, "stack_overflow", StringComparison.Ordinal)
                        ? "bag_full"
                        : "recipe_unknown");
            }
        }

        // 6. 写结果：成了带产出，没成留空；success 只在这一处改。
        await using (var update = new NpgsqlCommand(
            "update \"角色\".craft_log "
            + "set success = $3, output_item_id = $4, output_count = $5 "
            + "where txn_id = $1 and character_id = $2",
            connection, transaction))
        {
            update.Parameters.AddWithValue(txnId);
            update.Parameters.AddWithValue(characterId);
            update.Parameters.AddWithValue(success);
            update.Parameters.AddWithValue(success ? recipe.OutputItemId : "");
            update.Parameters.AddWithValue(success ? recipe.OutputCount : 0);
            await update.ExecuteNonQueryAsync(stopping);
        }

        // 7. 收了就删活；开始那一行流水留着，成与不成各一行。
        await using (var delete = new NpgsqlCommand(
            "delete from \"角色\".craft_job where character_id = $1", connection, transaction))
        {
            delete.Parameters.AddWithValue(characterId);
            await delete.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new CraftFinishOutcome(
            false, recipe.Id, recipe.Profession, success,
            success ? recipe.OutputItemId : "", success ? recipe.OutputCount : 0,
            recipe.SuccessPct, recipe.CraftTimeS, readyAt);
    }

    /// <summary>只读：当前有没有活、还差几秒。没有活回 null。</summary>
    public async Task<CraftJobFacts?> DescribeAsync(long characterId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var command = new NpgsqlCommand(
            "select recipe_id, started_at, ready_at, "
            + "greatest(0, ceil(extract(epoch from ready_at - now())))::int "
            + "from \"角色\".craft_job where character_id = $1", connection);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        var recipeId = reader.GetString(0);
        var recipe = table.Find(recipeId);
        return new CraftJobFacts(
            recipeId,
            recipe?.Profession ?? "",
            recipe?.CraftTimeS ?? 0,
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetInt32(3));
    }

    /// <summary>只给核对用：活、流水、货币、背包一次读回，全部只读。</summary>
    public async Task<CraftStateFacts> StateAsync(long characterId, CancellationToken stopping)
    {
        var job = await DescribeAsync(characterId, stopping);
        await using var connection = await dataSource.OpenConnectionAsync(stopping);

        var logs = new List<CraftLogRow>();
        await using (var read = new NpgsqlCommand(
            "select txn_id, action, recipe_id, profession, success, output_item_id, "
            + "output_count, created_at from \"角色\".craft_log "
            + "where character_id = $1 order by created_at, txn_id", connection))
        {
            read.Parameters.AddWithValue(characterId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                logs.Add(new CraftLogRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetFieldValue<DateTimeOffset>(7)));
            }
        }

        var currencies = new List<(string Kind, long Amount)>();
        await using (var read = new NpgsqlCommand(
            "select kind, amount from \"角色\".currency where character_id = $1 order by kind",
            connection))
        {
            read.Parameters.AddWithValue(characterId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                currencies.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        var slots = new List<BagSlot>();
        await using (var read = new NpgsqlCommand(
            "select slot, item_id, count, bound from \"角色\".inventory "
            + "where character_id = $1 order by slot", connection))
        {
            read.Parameters.AddWithValue(characterId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                slots.Add(new BagSlot(
                    reader.GetInt16(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3)));
            }
        }

        return new CraftStateFacts(job, logs, currencies, slots);
    }

    /// <summary>
    /// 把产出挑进背包格：先叠未满格、再占空格。只读，不改库；
    /// 放不下抛 bag_full，调用方整笔回滚。复用 GrantRepository 的同一个选格器。
    /// </summary>
    async Task<List<ItemDelta>> PlanOutputAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        RecipeDef recipe,
        CancellationToken stopping)
    {
        var slots = await grants.ReadSlotsForTransactionAsync(
            connection, transaction, characterId, stopping);
        try
        {
            return GrantRepository.PlanItemDeltas(
                slots,
                new[] { new DropLineDef(recipe.OutputItemId, recipe.OutputCount) },
                items,
                await BagExpandRepository.SlotsAsync(
                    connection, transaction, characterId, config, stopping));
        }
        catch (GrantException ex)
        {
            throw new CraftException(
                string.Equals(ex.Code, "bag_full", StringComparison.Ordinal)
                    ? "bag_full"
                    : "recipe_unknown");
        }
    }

    static async Task<bool> ClaimLogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string txnId,
        long characterId,
        string action,
        RecipeDef recipe,
        CancellationToken stopping)
    {
        await using var claim = new NpgsqlCommand(
            "insert into \"角色\".craft_log "
            + "(txn_id, character_id, action, recipe_id, profession) "
            + "values ($1, $2, $3, $4, $5) on conflict (txn_id) do nothing",
            connection, transaction);
        claim.Parameters.AddWithValue(txnId);
        claim.Parameters.AddWithValue(characterId);
        claim.Parameters.AddWithValue(action);
        claim.Parameters.AddWithValue(recipe.Id);
        claim.Parameters.AddWithValue(recipe.Profession);
        return await claim.ExecuteNonQueryAsync(stopping) > 0;
    }

    static async Task<bool> LogExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string txnId,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select 1 from \"角色\".craft_log where txn_id = $1", connection, transaction);
        read.Parameters.AddWithValue(txnId);
        return await read.ExecuteScalarAsync(stopping) is not null;
    }

    static async Task<bool> JobExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select 1 from \"角色\".craft_job where character_id = $1", connection, transaction);
        read.Parameters.AddWithValue(characterId);
        return await read.ExecuteScalarAsync(stopping) is not null;
    }

    static CraftFinishOutcome DuplicateOutcome() =>
        new(true, "", "", false, "", 0, 0.0, 0, default);
}
