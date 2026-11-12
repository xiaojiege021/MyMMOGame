using Npgsql;

namespace Character;

/// <summary>
/// 一次解绑的只读报价：这一格是什么、绑了多久、按配置要几张、背包里现有几张。
/// 不写库，用来给网关回事实值、给核对看「报价与实扣一致」。
/// </summary>
public sealed record UnbindQuote(
    bool Found,
    string Reason,
    int Slot,
    string TargetItemId,
    bool Bound,
    long AgeHours,
    int Cost,
    bool FloorHit,
    long UnbindCount);

/// <summary>一笔解绑的结果。Reason 空串表示真解开了；其余路径整笔没做。</summary>
public sealed record UnbindOutcome(
    bool Found,
    string Reason,
    int Slot,
    string TargetItemId,
    int Cost,
    long CountLeft,
    long AgeHours,
    bool Duplicate);

/// <summary>
/// 解绑状态的唯一读取点与唯一写入点。绑定状态本身仍是 `"角色".inventory` 的
/// `bound` / `bound_at`（P1-1b3）——本卡不新建表，也不另存第二份「解绑过」的状态。
///
/// 解绑这一笔只在 UseAsync 里：锁角色行 → 抢 `grant_txn` 事务号 → 锁目标格 →
/// 认装备 / 认已绑 / 按 `now() - bound_at` 算年龄 → 按配置算张数 → 锁解绑道具那格 →
/// 扣掉 cost 张 → 把目标改成 `bound=false, bound_at=NULL` → 读回事实值 → 提交。
///
/// 拒绝路径一条都不写库：年龄、种类、绑定态、余量都在扣减之前判完，判不过直接回滚。
/// 年龄用数据库时钟，换进程、重启角色服都还是同一个答案。
/// </summary>
public sealed class UnbindRepository(NpgsqlDataSource dataSource, ItemTable items, UnbindTable table)
{
    public const string EquipmentKind = "equipment";

    /// <summary>只读报价：这一格是不是装备、绑没绑、绑了多久、背包里现有几张解绑道具。</summary>
    public async Task<UnbindQuote> QuoteAsync(
        long characterId, int slot, string itemId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);

        bool exists;
        await using (var character = new NpgsqlCommand(
            "select 1 from \"角色\".character where id = $1", connection))
        {
            character.Parameters.AddWithValue(characterId);
            exists = await character.ExecuteScalarAsync(stopping) is not null;
        }

        if (!exists)
        {
            return new UnbindQuote(false, "character_not_found", slot, "", false, 0, 0, false, 0);
        }

        string targetItemId;
        bool bound;
        long ageHours;
        await using (var target = new NpgsqlCommand(
            "select item_id, bound, "
            + "case when bound_at is null then 0 "
            + "     else floor(extract(epoch from (now() - bound_at)) / 3600)::bigint end "
            + "from \"角色\".inventory where character_id = $1 and slot = $2",
            connection))
        {
            target.Parameters.AddWithValue(characterId);
            target.Parameters.AddWithValue((short)slot);
            await using var reader = await target.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                return new UnbindQuote(true, "slot_empty", slot, "", false, 0, 0, false, 0);
            }

            targetItemId = reader.GetString(0);
            bound = reader.GetBoolean(1);
            ageHours = Convert.ToInt64(reader.GetValue(2));
        }

        long unbindCount;
        await using (var bag = new NpgsqlCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2", connection))
        {
            bag.Parameters.AddWithValue(characterId);
            bag.Parameters.AddWithValue(itemId);
            unbindCount = Convert.ToInt64(await bag.ExecuteScalarAsync(stopping));
        }

        var cost = table.CostFor(ageHours);
        return new UnbindQuote(true, "", slot, targetItemId, bound, ageHours,
            cost, cost == table.FloorCost, unbindCount);
    }

    /// <summary>
    /// 真解绑。account 为空表示服间可信调用，只按角色编号归属；网关转发一定带账号。
    /// 张数在事务里按「读到的年龄 + 配置」算一次，不拿事务外的报价，也不写第二套数。
    /// 同一个 txnId 重放整笔不做。
    /// </summary>
    public async Task<UnbindOutcome> UseAsync(
        long characterId,
        string? account,
        string txnId,
        int slot,
        string itemId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁角色行并核归属，与结算、用符、用卷同一口径。
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        var found = false;
        await using (var read = new NpgsqlCommand(
            "select id from \"角色\".character " + owned, connection, transaction))
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
            return new UnbindOutcome(false, "character_not_found", slot, "", 0, 0, 0, false);
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
                return new UnbindOutcome(true, "duplicate", slot, "", 0, 0, 0, true);
            }
        }

        // 3. 锁目标格，认「装备 + 已绑」。拒绝路径都在扣减之前。
        string targetItemId;
        bool bound;
        long ageHours;
        await using (var target = new NpgsqlCommand(
            "select item_id, bound, "
            + "case when bound_at is null then 0 "
            + "     else floor(extract(epoch from (now() - bound_at)) / 3600)::bigint end "
            + "from \"角色\".inventory where character_id = $1 and slot = $2 for update",
            connection, transaction))
        {
            target.Parameters.AddWithValue(characterId);
            target.Parameters.AddWithValue((short)slot);
            await using var reader = await target.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                await transaction.RollbackAsync(stopping);
                return new UnbindOutcome(true, "slot_empty", slot, "", 0, 0, 0, false);
            }

            targetItemId = reader.GetString(0);
            bound = reader.GetBoolean(1);
            ageHours = Convert.ToInt64(reader.GetValue(2));
        }

        var def = items.Find(targetItemId);
        if (def is null || !string.Equals(def.Kind, EquipmentKind, StringComparison.Ordinal))
        {
            // 符、券、卷轴、经验道具、材料都走这条：解绑道具只解装备。
            await transaction.RollbackAsync(stopping);
            return new UnbindOutcome(true, "not_equipment", slot, targetItemId, 0, 0, ageHours, false);
        }

        if (!bound)
        {
            await transaction.RollbackAsync(stopping);
            return new UnbindOutcome(true, "not_bound", slot, targetItemId, 0, 0, ageHours, false);
        }

        // 4. 按这一笔读到的年龄算张数（用同一个数据库时钟，不拿事务外的报价），
        //    再锁解绑道具那一格、认余量。
        var cost = table.CostFor(ageHours);
        int bagSlot;
        int bagCount;
        await using (var bag = new NpgsqlCommand(
            "select slot, count from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2 order by slot limit 1 for update",
            connection, transaction))
        {
            bag.Parameters.AddWithValue(characterId);
            bag.Parameters.AddWithValue(itemId);
            await using var reader = await bag.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                await transaction.RollbackAsync(stopping);
                return new UnbindOutcome(true, "no_unbind_item", slot, targetItemId, 0, 0, ageHours, false);
            }

            bagSlot = reader.GetInt16(0);
            bagCount = reader.GetInt32(1);
        }

        if (bagCount < cost)
        {
            await transaction.RollbackAsync(stopping);
            return new UnbindOutcome(true, "not_enough_unbind", slot, targetItemId, cost, bagCount, ageHours, false);
        }

        // 5. 扣 cost 张：还有剩就减，正好用完就删（表上有 CHECK (count > 0)）。
        await using (var take = new NpgsqlCommand(
            bagCount > cost
                ? "update \"角色\".inventory set count = count - $3 "
                  + "where character_id = $1 and slot = $2"
                : "delete from \"角色\".inventory where character_id = $1 and slot = $2",
            connection, transaction))
        {
            take.Parameters.AddWithValue(characterId);
            take.Parameters.AddWithValue((short)bagSlot);
            if (bagCount > cost)
            {
                take.Parameters.AddWithValue(cost);
            }

            if (await take.ExecuteNonQueryAsync(stopping) != 1)
            {
                await transaction.RollbackAsync(stopping);
                return new UnbindOutcome(true, "no_unbind_item", slot, targetItemId, 0, bagCount, ageHours, false);
            }
        }

        // 6. 把这一格改回「未绑」。只动 bound / bound_at 两列，不碰数量、不碰属性。
        await using (var clear = new NpgsqlCommand(
            "update \"角色\".inventory set bound = false, bound_at = null "
            + "where character_id = $1 and slot = $2", connection, transaction))
        {
            clear.Parameters.AddWithValue(characterId);
            clear.Parameters.AddWithValue((short)slot);
            if (await clear.ExecuteNonQueryAsync(stopping) != 1)
            {
                await transaction.RollbackAsync(stopping);
                return new UnbindOutcome(true, "slot_empty", slot, targetItemId, 0, bagCount, ageHours, false);
            }
        }

        // 7. 读回这一笔之后的事实值，一起提交。
        long left;
        await using (var state = new NpgsqlCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2", connection, transaction))
        {
            state.Parameters.AddWithValue(characterId);
            state.Parameters.AddWithValue(itemId);
            left = Convert.ToInt64(await state.ExecuteScalarAsync(stopping));
        }

        await transaction.CommitAsync(stopping);
        return new UnbindOutcome(true, "", slot, targetItemId, cost, left, ageHours, false);
    }
}
