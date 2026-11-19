using Npgsql;

namespace Character;

/// <summary>移动一格的三种落法之一：挪到空格、两格交换、同物品合并。</summary>
public sealed record BagMoveOutcome(
    bool Found, string Reason, BagSlot Source, BagSlot Target, bool Merged, bool Swapped);

/// <summary>丢弃一笔的结果。Duplicate 表示同一个事务号重放，整笔没做。</summary>
public sealed record BagDiscardOutcome(
    bool Found, string Reason, string ItemId, int CountLeft, bool Duplicate);

/// <summary>排序的结果：Moved 是真正换了格的行数。</summary>
public sealed record BagSortOutcome(
    bool Found, string Reason, int Moved, List<BagSlot> Slots);

/// <summary>
/// 背包操作（Q10 / I2）的唯一写入点：移动、丢弃、排序。
///
/// 三条都只改 "角色".inventory 的 slot，或（合并时）目标格的数量：
/// item_id / bound / bound_at / 数量一个都不动，排序整行不碰。
/// 强化等级挂在 inventory.id 上（C4），所以换格不丢等级、也不串到下一件装备；
/// 结晶挂在 (character_id, slot) 上，v28 把那条外键改成 ON UPDATE CASCADE，
/// 换格时镶嵌行跟着走，不用重装。
///
/// 三条都在一笔事务里：先锁角色行（同角色的两次背包操作排队），再锁要动的那几格。
/// 拒绝路径一律先回滚再回包，库里一个字节都不留。
/// </summary>
public sealed class BagRepository(NpgsqlDataSource dataSource)
{
    /// <summary>库层 CHECK 的上界（slot 1..200）；配置里的 bag_slots 只更小。</summary>
    const int MaxBagSlot = 200;

    /// <summary>
    /// 排序的种类顺序（Q10 口径：种类 → 品阶 → 物品编号）。新种类追加到末尾，不插队；
    /// 物品表里没登记的种类一律排在最后，仍按编号稳定。这是排序键的顺序，
    /// 不是玩法数值，所以不进配置。
    /// </summary>
    static readonly string[] KindOrder =
    [
        "equipment", "crystal", "voucher", "consumable", "material",
        "tool", "bag", "title", "outfit", "appearance_fragment", "home_decor",
    ];

    /// <summary>品阶顺序与 K5 的六档一致；没写品阶的排在同种类的最前。</summary>
    static readonly string[] GradeOrder =
    [
        "white", "green", "blue", "purple", "orange", "red",
    ];

    // ------------------------------------------------------------------ 移动

    /// <summary>
    /// 把 fromSlot 那一格挪走。目标空着就直接挪；同一件物品在堆叠上限内合并
    /// （合并会删掉来源行，所以来源行上有强化或结晶时直接拒绝，不替玩家决定「附属不要了」）；
    /// 不同物品两格交换。交换与排序都靠一个临时空格周转，满格时回 bag_full。
    /// account 为空表示服间可信调用，只按角色编号核存在。
    /// </summary>
    public async Task<BagMoveOutcome> MoveAsync(
        long characterId,
        string? account,
        int fromSlot,
        int toSlot,
        int bagSlots,
        ItemTable items,
        CancellationToken stopping)
    {
        var emptySource = EmptySlot(fromSlot);
        var emptyTarget = EmptySlot(toSlot);
        if (fromSlot == toSlot)
        {
            return new BagMoveOutcome(true, "same_slot", emptySource, emptyTarget, false, false);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await LockCharacterAsync(connection, transaction, characterId, account, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return new BagMoveOutcome(false, "character_not_found", emptySource, emptyTarget, false, false);
        }

        var rows = await LockSlotsAsync(connection, transaction, characterId, [fromSlot, toSlot], stopping);
        if (!rows.TryGetValue(fromSlot, out var source))
        {
            await transaction.RollbackAsync(stopping);
            return new BagMoveOutcome(
                true, "slot_empty", emptySource,
                rows.TryGetValue(toSlot, out var untouched) ? ToBagSlot(untouched) : emptyTarget,
                false, false);
        }

        rows.TryGetValue(toSlot, out var target);
        var merged = false;
        var swapped = false;

        if (target is not null && string.Equals(target.ItemId, source.ItemId, StringComparison.Ordinal))
        {
            var stackMax = items.Find(source.ItemId)?.StackMax ?? int.MaxValue;
            if (source.Count + target.Count > stackMax)
            {
                await transaction.RollbackAsync(stopping);
                return new BagMoveOutcome(
                    true, "stack_overflow", ToBagSlot(source), ToBagSlot(target), false, false);
            }

            if (await HasAttachmentAsync(
                    connection, transaction, characterId, source.Id, fromSlot, stopping))
            {
                await transaction.RollbackAsync(stopping);
                return new BagMoveOutcome(
                    true, "row_attached", ToBagSlot(source), ToBagSlot(target), false, false);
            }

            await using (var add = new NpgsqlCommand(
                "update \"角色\".inventory set count = count + $2 where id = $1",
                connection, transaction))
            {
                add.Parameters.AddWithValue(source.Id);
                add.Parameters.AddWithValue(source.Count);
                await add.ExecuteNonQueryAsync(stopping);
            }

            // 来源行删掉：它的数量已经加到目标格上，库存不会少一件。
            await using (var drop = new NpgsqlCommand(
                "delete from \"角色\".inventory where id = $1", connection, transaction))
            {
                drop.Parameters.AddWithValue(source.Id);
                await drop.ExecuteNonQueryAsync(stopping);
            }

            merged = true;
        }
        else if (target is null)
        {
            await SetSlotAsync(connection, transaction, source.Id, toSlot, stopping);
        }
        else
        {
            var temp = await FreeSlotAsync(connection, transaction, characterId, stopping);
            if (temp == 0)
            {
                await transaction.RollbackAsync(stopping);
                return new BagMoveOutcome(
                    true, "bag_full", ToBagSlot(source), ToBagSlot(target), false, false);
            }

            // 唯一索引卡在 (character_id, slot) 上，两行不能同时指向对方的格子，
            // 所以先挪到临时格再落位。
            await SetSlotAsync(connection, transaction, source.Id, temp, stopping);
            await SetSlotAsync(connection, transaction, target.Id, fromSlot, stopping);
            await SetSlotAsync(connection, transaction, temp, toSlot, stopping);
            swapped = true;
        }

        var after = await LockSlotsAsync(connection, transaction, characterId, [fromSlot, toSlot], stopping);
        await transaction.CommitAsync(stopping);

        return new BagMoveOutcome(
            true, "",
            after.TryGetValue(fromSlot, out var s) ? ToBagSlot(s) : EmptySlot(fromSlot),
            after.TryGetValue(toSlot, out var d) ? ToBagSlot(d) : EmptySlot(toSlot),
            merged, swapped);
    }

    // ------------------------------------------------------------------ 丢弃

    /// <summary>
    /// 丢掉某一格的 count 个。不带二次确认（confirm=false）一律拒绝，一个字节都不写；
    /// 事务号复用 "角色".grant_txn，同号重放整笔不做。丢光就删行（强化 / 结晶跟着
    /// CASCADE 走，这是玩家确认过的「这一格的东西不要了」），没丢光的只减数量。
    /// </summary>
    public async Task<BagDiscardOutcome> DiscardAsync(
        long characterId,
        string? account,
        int slot,
        int count,
        bool confirm,
        string txnId,
        CancellationToken stopping)
    {
        if (!confirm)
        {
            return new BagDiscardOutcome(true, "not_confirmed", "", 0, false);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await LockCharacterAsync(connection, transaction, characterId, account, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return new BagDiscardOutcome(false, "character_not_found", "", 0, false);
        }

        var rows = await LockSlotsAsync(connection, transaction, characterId, [slot], stopping);
        if (!rows.TryGetValue(slot, out var row))
        {
            await transaction.RollbackAsync(stopping);
            return new BagDiscardOutcome(true, "slot_empty", "", 0, false);
        }

        if (count < 1 || count > row.Count)
        {
            await transaction.RollbackAsync(stopping);
            return new BagDiscardOutcome(true, "count_invalid", row.ItemId, row.Count, false);
        }

        // 抢事务号：跨进程消耗品扣减统一复用 grant_txn，同号重放整笔不做。
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
                return new BagDiscardOutcome(true, "duplicate", row.ItemId, row.Count, true);
            }
        }

        var left = row.Count - count;
        if (left == 0)
        {
            await using var drop = new NpgsqlCommand(
                "delete from \"角色\".inventory where id = $1", connection, transaction);
            drop.Parameters.AddWithValue(row.Id);
            await drop.ExecuteNonQueryAsync(stopping);
        }
        else
        {
            await using var cut = new NpgsqlCommand(
                "update \"角色\".inventory set count = $2 where id = $1", connection, transaction);
            cut.Parameters.AddWithValue(row.Id);
            cut.Parameters.AddWithValue(left);
            await cut.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new BagDiscardOutcome(true, "", row.ItemId, left, false);
    }

    // ------------------------------------------------------------------ 排序

    /// <summary>
    /// 按「种类 → 品阶 → 物品编号」重排，紧凑从第 1 格起。只改 slot 一列：
    /// 数量、绑定、绑定时刻、强化等级、镶嵌行都不动（镶嵌靠 v28 的 ON UPDATE CASCADE 跟着走）。
    /// 用的是一格临时格周转：先把一条链最后一件落到空出来的格，再一件接一件跟上去；
    /// 纯环（满格里两行互换）才用临时格，满格又只有环时回 bag_full。
    /// </summary>
    public async Task<BagSortOutcome> SortAsync(
        long characterId, string? account, ItemTable items, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await LockCharacterAsync(connection, transaction, characterId, account, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return new BagSortOutcome(false, "character_not_found", 0, []);
        }

        var rows = await LockAllAsync(connection, transaction, characterId, stopping);
        if (rows.Count < 2)
        {
            await transaction.CommitAsync(stopping);
            return new BagSortOutcome(true, "", 0, rows.ConvertAll(ToBagSlot));
        }

        var ordered = rows
            .OrderBy(r => KindRank(items.Find(r.ItemId)?.Kind ?? string.Empty))
            .ThenBy(r => GradeRank(items.Find(r.ItemId)?.Grade ?? string.Empty))
            .ThenBy(r => r.ItemId, StringComparer.Ordinal)
            .ThenBy(r => r.Slot)
            .ToList();

        // 目标格号：排好序的第 i 件落在第 i+1 格。id → 目标格是一一映射。
        var targetOf = new Dictionary<long, int>(rows.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            targetOf[ordered[i].Id] = i + 1;
        }

        // 落位顺序交给纯函数算（见 BagSortPlan）：链条倒着走、纯环用一个周转格。
        var occupied = new HashSet<int>();
        foreach (var row in rows)
        {
            occupied.Add(row.Slot);
        }

        var free = new List<int>();
        for (var slot = 1; slot <= MaxBagSlot; slot++)
        {
            if (!occupied.Contains(slot))
            {
                free.Add(slot);
            }
        }

        var plan = BagSortPlan.Plan(rows.ConvertAll(r => (r.Id, r.Slot)), targetOf, free);
        if (plan is null)
        {
            await transaction.RollbackAsync(stopping);
            return new BagSortOutcome(true, "bag_full", 0, []);
        }

        foreach (var step in plan)
        {
            await SetSlotAsync(connection, transaction, step.Id, step.Dest, stopping);
        }

        var moved = plan.Count;
        var after = await LockAllAsync(connection, transaction, characterId, stopping);
        await transaction.CommitAsync(stopping);
        return new BagSortOutcome(true, "", moved, after.ConvertAll(ToBagSlot));
    }

    // ------------------------------------------------------------------ 内部

    sealed record Row(long Id, int Slot, string ItemId, int Count, bool Bound);

    static BagSlot ToBagSlot(Row row) => new(row.Slot, row.ItemId, row.Count, row.Bound);

    static BagSlot EmptySlot(int slot) => new(slot, "", 0, false);

    /// <summary>锁角色行并核归属。account 为空时只核存在（服间可信调用）。</summary>
    static async Task<bool> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string? account,
        CancellationToken stopping)
    {
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        await using var command = new NpgsqlCommand(
            "select id from \"角色\".character " + owned, connection, transaction);
        command.Parameters.AddWithValue(characterId);
        if (!string.IsNullOrEmpty(account))
        {
            command.Parameters.AddWithValue(account);
        }

        await using var reader = await command.ExecuteReaderAsync(stopping);
        return await reader.ReadAsync(stopping);
    }

    static async Task<Dictionary<int, Row>> LockSlotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        int[] slots,
        CancellationToken stopping)
    {
        var map = new Dictionary<int, Row>(slots.Length);
        var wanted = new short[slots.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            wanted[i] = (short)slots[i];
        }

        await using var command = new NpgsqlCommand(
            "select id, slot, item_id, count, bound from \"角色\".inventory "
            + "where character_id = $1 and slot = any($2) order by slot for update",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(wanted);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            var row = new Row(
                reader.GetInt64(0), reader.GetInt16(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetBoolean(4));
            map[row.Slot] = row;
        }

        return map;
    }

    static async Task<List<Row>> LockAllAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        CancellationToken stopping)
    {
        var rows = new List<Row>();
        await using var command = new NpgsqlCommand(
            "select id, slot, item_id, count, bound from \"角色\".inventory "
            + "where character_id = $1 order by slot for update",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            rows.Add(new Row(
                reader.GetInt64(0), reader.GetInt16(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetBoolean(4)));
        }

        return rows;
    }

    static async Task SetSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long id,
        int slot,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "update \"角色\".inventory set slot = $2 where id = $1", connection, transaction);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue((short)slot);
        await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>
    /// 找一个没被占用的格号当周转位。上限用库层 CHECK 的 200，不用 bag_slots：
    /// 周转格只在事务里活一瞬间，不落成玩家看得见的格子；用 bag_slots 会在满包时白拒。
    /// </summary>
    static async Task<int> FreeSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select s from generate_series(1, $2) s "
            + "where not exists (select 1 from \"角色\".inventory i "
            + "                  where i.character_id = $1 and i.slot = s) "
            + "order by s limit 1",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue((short)MaxBagSlot);
        var found = await command.ExecuteScalarAsync(stopping);
        return found is null ? 0 : Convert.ToInt32(found);
    }

    /// <summary>
    /// 这一行上有没有附属：强化等级（按 inventory_id）或镶嵌中的结晶（按格子）。
    /// 合并要删掉这一行，附属会跟着 CASCADE 一起没，所以有附属就不合并。
    /// </summary>
    static async Task<bool> HasAttachmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        long inventoryId,
        int slot,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select 1 from \"角色\".enhance where inventory_id = $1 "
            + "union all "
            + "select 1 from \"角色\".crystal_socket where character_id = $2 and slot = $3 "
            + "limit 1",
            connection, transaction);
        command.Parameters.AddWithValue(inventoryId);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue((short)slot);
        return await command.ExecuteScalarAsync(stopping) is not null;
    }

    static int KindRank(string kind)
    {
        var at = Array.IndexOf(KindOrder, kind);
        return at < 0 ? KindOrder.Length : at;
    }

    static int GradeRank(string grade)
    {
        var at = Array.IndexOf(GradeOrder, grade);
        return at < 0 ? -1 : at;
    }
}
