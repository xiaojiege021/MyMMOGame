using Npgsql;

namespace Character;

/// <summary>月叠落到背包里的一摞：哪一件、共几张、第一张落在第几格。</summary>
public sealed record BundleStack(string ItemId, int Count, int Slot);

/// <summary>买一次月叠的结果。Ok=false 时 Reason 给拒绝码，未生效的数值一律留 0。</summary>
public sealed record MonthlyBundlePurchase(
    bool Ok, string Reason, long Price, long Balance, bool Duplicate,
    IReadOnlyList<BundleStack> Stacks);

/// <summary>月叠被业务规则挡下。Code 直接回给调用方，转成 ok=false + reason。</summary>
public sealed class MonthlyBundleException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// 月叠的唯一写入点（方案 12.1 第 809 行、开发架构 11 节 P3-4 / 13 节 P3-4b / 第 69 行）。
/// 一笔事务，全在角色服：
///
///   锁角色行 → 先抢事务号 → 按配置规划格子（先叠到同物品未满的格，再占空格）
///   → 带余额条件扣元宝 → 逐格写入 → 事务内读回余额 → 提交。
///
/// 「到账后背包里只有传送符和红符」是结构保证：这份仓储只会写
/// `config/monthly_bundle.json` 里那两条（传送符与红符），数量是「每天几张 × 天数」，
/// 别的物品从这条路径里长不出来。数量跨过堆叠上限就占多格，一摞一摞放；
/// 格子不够整笔退回（bag_full），元宝一分不扣。同一个事务号重放整笔不做。
/// </summary>
public sealed class MonthlyBundleRepository(
    NpgsqlDataSource dataSource,
    MonthlyBundleTable table,
    ItemTable items,
    CharacterConfig config)
{
    public const int MaxTxnLength = 64;

    public async Task<MonthlyBundlePurchase> BuyAsync(
        long characterId, string txnId, CancellationToken stopping)
    {
        var txn = (txnId ?? "").Trim();
        var price = table.PriceYuanbao;

        if (txn.Length == 0 || txn.Length > MaxTxnLength)
        {
            return new MonthlyBundlePurchase(false, "txn_missing", price, 0, false, []);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        try
        {
            await LockCharacterAsync(connection, transaction, characterId, stopping);

            if (!await ClaimTxnAsync(connection, transaction, characterId, txn, stopping))
            {
                await transaction.RollbackAsync(stopping);
                // 回滚之后不能拿已结束的事务再读（P3-1a2 踩坑①）：余额从同一连接重读。
                var after = await BalanceAsync(connection, null, characterId, stopping);
                return new MonthlyBundlePurchase(false, "duplicate", price, after, true, []);
            }

            var rows = await ReadInventoryAsync(connection, transaction, characterId, stopping);
            var plan = Plan(rows, config.BagSlots);

            if (!await DebitYuanbaoAsync(connection, transaction, characterId, price, stopping))
            {
                throw new MonthlyBundleException("not_enough_yuanbao");
            }

            foreach (var write in plan.Writes)
            {
                await ApplyAsync(connection, transaction, characterId, write, stopping);
            }

            var balance = await BalanceAsync(connection, transaction, characterId, stopping);
            await transaction.CommitAsync(stopping);

            return new MonthlyBundlePurchase(true, "", price, balance, false, plan.Stacks);
        }
        catch (MonthlyBundleException ex)
        {
            await transaction.RollbackAsync(stopping);
            var after = await BalanceAsync(connection, null, characterId, stopping);
            return new MonthlyBundlePurchase(false, ex.Code, price, after, false, []);
        }
    }

    /// <summary>一格要写的量：新占一格（insert）还是叠进已有那一格（update）。</summary>
    sealed record PlannedWrite(int Slot, string ItemId, int Count, bool IsNew);

    sealed record PlanResult(List<PlannedWrite> Writes, List<BundleStack> Stacks);

    /// <summary>
    /// 按配置规划格子：先叠到「同物品且没到堆叠上限」的格，再依次占空格。
    /// 格子不够就抛 bag_full——整笔退回，元宝不扣，背包一格不动。
    /// </summary>
    PlanResult Plan(List<(int Slot, string ItemId, int Count)> rows, int bagSlots)
    {
        var free = new List<int>();
        var used = new HashSet<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            used.Add(rows[i].Slot);
        }

        for (var slot = 1; slot <= bagSlots; slot++)
        {
            if (!used.Contains(slot))
            {
                free.Add(slot);
            }
        }

        // 现有各格的余量：先按同物品把能叠的叠满，叠不下的再占新格。
        var room = new Dictionary<int, int>();
        for (var i = 0; i < rows.Count; i++)
        {
            var goods = items.Find(rows[i].ItemId);
            if (goods is null)
            {
                continue;
            }

            room[rows[i].Slot] = goods.StackMax - rows[i].Count;
        }

        var writes = new List<PlannedWrite>();
        var stacks = new List<BundleStack>();
        var next = 0;

        foreach (var entry in table.Entries)
        {
            var goods = items.Find(entry.ItemId)
                ?? throw new InvalidOperationException("月叠配置校验漏了物品：" + entry.ItemId);

            var remaining = entry.Count;
            var firstSlot = 0;
            var placed = 0;

            for (var i = 0; i < rows.Count && remaining > 0; i++)
            {
                if (!string.Equals(rows[i].ItemId, entry.ItemId, StringComparison.Ordinal))
                {
                    continue;
                }

                var spare = room[rows[i].Slot];
                if (spare <= 0)
                {
                    continue;
                }

                var add = Math.Min(spare, remaining);
                writes.Add(new PlannedWrite(rows[i].Slot, entry.ItemId, add, false));
                room[rows[i].Slot] = spare - add;
                remaining -= add;
                placed += add;
                if (firstSlot == 0)
                {
                    firstSlot = rows[i].Slot;
                }
            }

            while (remaining > 0)
            {
                if (next >= free.Count)
                {
                    // 空格不够放完这一叠：整笔退回，不半截发放。
                    throw new MonthlyBundleException("bag_full");
                }

                var slot = free[next++];
                var add = Math.Min(goods.StackMax, remaining);
                writes.Add(new PlannedWrite(slot, entry.ItemId, add, true));
                remaining -= add;
                placed += add;
                if (firstSlot == 0)
                {
                    firstSlot = slot;
                }
            }

            stacks.Add(new BundleStack(entry.ItemId, placed, firstSlot));
        }

        return new PlanResult(writes, stacks);
    }

    async Task ApplyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        PlannedWrite write, CancellationToken stopping)
    {
        if (!write.IsNew)
        {
            await using var update = new NpgsqlCommand(
                "update \"角色\".inventory set count = count + $3 "
                + "where character_id = $1 and slot = $2",
                connection, transaction);
            update.Parameters.AddWithValue(characterId);
            update.Parameters.AddWithValue((short)write.Slot);
            update.Parameters.AddWithValue(write.Count);
            await update.ExecuteNonQueryAsync(stopping);
            return;
        }

        var goods = items.Find(write.ItemId)
            ?? throw new InvalidOperationException("月叠配置校验漏了物品：" + write.ItemId);
        var bound = goods.BindsOnArrival;
        await using (var insert = new NpgsqlCommand(
            "insert into \"角色\".inventory (character_id, slot, item_id, count, bound, bound_at) "
            + "values ($1, $2, $3, $4, $5, case when $5 then now() else null end)",
            connection, transaction))
        {
            insert.Parameters.AddWithValue(characterId);
            insert.Parameters.AddWithValue((short)write.Slot);
            insert.Parameters.AddWithValue(write.ItemId);
            insert.Parameters.AddWithValue(write.Count);
            insert.Parameters.AddWithValue(bound);
            await insert.ExecuteNonQueryAsync(stopping);
        }
    }

    static async Task LockCharacterAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select id from \"角色\".character where id = $1 for update", connection, transaction);
        read.Parameters.AddWithValue(characterId);
        var found = await read.ExecuteScalarAsync(stopping);
        if (found is null)
        {
            throw new MonthlyBundleException("character_not_found");
        }
    }

    /// <summary>抢本笔的事务号。冲突说明这个号用过了，调用方按 duplicate 处理。</summary>
    static async Task<bool> ClaimTxnAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        string txnId, CancellationToken stopping)
    {
        await using var claim = new NpgsqlCommand(
            "insert into \"角色\".grant_txn (txn_id, character_id) values ($1, $2) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction);
        claim.Parameters.AddWithValue(txnId);
        claim.Parameters.AddWithValue(characterId);
        return await claim.ExecuteScalarAsync(stopping) is not null;
    }

    /// <summary>带余额条件扣元宝。扣不动说明不够，整笔回滚。</summary>
    static async Task<bool> DebitYuanbaoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        long price, CancellationToken stopping)
    {
        await using var debit = new NpgsqlCommand(
            "update \"角色\".currency set amount = amount - $2, updated_at = now() "
            + "where character_id = $1 and kind = $3 and amount >= $2 returning amount",
            connection, transaction);
        debit.Parameters.AddWithValue(characterId);
        debit.Parameters.AddWithValue(price);
        debit.Parameters.AddWithValue(ShopTable.WrittenCurrency);
        return await debit.ExecuteScalarAsync(stopping) is not null;
    }

    static async Task<long> BalanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long characterId,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select amount from \"角色\".currency where character_id = $1 and kind = $2",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(ShopTable.WrittenCurrency);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>锁住整个背包再规划：并发进货时同一格不会被两笔各写一半。</summary>
    static async Task<List<(int Slot, string ItemId, int Count)>> ReadInventoryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        CancellationToken stopping)
    {
        var rows = new List<(int, string, int)>();
        await using var read = new NpgsqlCommand(
            "select slot, item_id, count from \"角色\".inventory "
            + "where character_id = $1 order by slot for update",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        await using var reader = await read.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            rows.Add((reader.GetInt16(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return rows;
    }
}
