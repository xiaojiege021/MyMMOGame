using Npgsql;

namespace Character;

/// <summary>账号的角色位现状。全是事实值，不在回包里算「还能建几个」。</summary>
public sealed record CharacterSlotState(
    string Account, int BaseSlots, int MaxSlots, int Slots, int Used, string ProductId,
    long PriceYuanbao, bool Found);

/// <summary>买一次第二角色栏的结果。Ok=false 时 Reason 给拒绝码。</summary>
public sealed record CharacterSlotPurchase(
    bool Ok, string Reason, int Slots, long Price, long Balance, bool Duplicate);

/// <summary>角色位被业务规则挡下。Code 直接回给调用方，转成 ok=false + reason。</summary>
public sealed class CharacterSlotException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// 第二角色栏的唯一写入点（方案 12.1 第 810 行、开发架构 11 节 P3-4 / 13 节 P3-4b / 第 69 行）。
///
///   · 名额记在 `"角色".character_slot`（账号一行、只存总名额）。没有那一行就按
///     config 的 base_slots 算——老账号不受影响，也不必在注册时补一行。
///   · 买：一笔事务，锁账号行 → 抢事务号 → 已有名额到上限就 already_owned（不重复扣）
///     → 带余额条件扣元宝（从当前角色身上扣，角色栏是账号的，钱来自这个角色）
///     → 把名额从「现在的值」推到 +1 → 事务内读回余额 → 提交。
///   · 装备 / 等级 / 境界一概不在这里：那些按 character_id 各存一行，「不共享」是
///     表结构的结果，这张表只有一个名额列。
/// </summary>
public sealed class CharacterSlotRepository(
    NpgsqlDataSource dataSource,
    CharacterSlotTable table)
{
    public const int MaxTxnLength = 64;

    // ------------------------------------------------------------------ 只读

    public async Task<CharacterSlotState> DescribeAsync(string account, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        var found = await AccountExistsAsync(connection, account, stopping);
        var slots = await SlotsOfAsync(connection, account, stopping);
        var used = await UsedAsync(connection, account, stopping);

        return new CharacterSlotState(
            account, table.BaseSlots, table.MaxSlots, slots, used, table.ProductId,
            table.PriceYuanbao, found);
    }

    // ------------------------------------------------------------------ 买

    public async Task<CharacterSlotPurchase> BuyAsync(
        string account, long characterId, string txnId, CancellationToken stopping)
    {
        var txn = (txnId ?? "").Trim();
        var price = table.PriceYuanbao;

        if (txn.Length == 0 || txn.Length > MaxTxnLength)
        {
            return new CharacterSlotPurchase(false, "txn_missing", 0, price, 0, false);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        try
        {
            if (!await LockAccountAsync(connection, transaction, account, stopping))
            {
                throw new CharacterSlotException("account_not_found");
            }

            await LockCharacterAsync(connection, transaction, characterId, account, stopping);

            if (!await ClaimTxnAsync(connection, transaction, characterId, txn, stopping))
            {
                await transaction.RollbackAsync(stopping);
                var after = await BalanceAsync(connection, null, characterId, stopping);
                var now = await SlotsOfAsync(connection, account, stopping);
                return new CharacterSlotPurchase(false, "duplicate", now, price, after, true);
            }

            var current = await SlotsOfAsync(connection, transaction, account, stopping);
            if (current >= table.MaxSlots)
            {
                // 方案只写了「第二角色栏」：已经有第二位就不再卖，也不退钱（这次没扣）。
                throw new CharacterSlotException("already_owned");
            }

            if (!await DebitYuanbaoAsync(connection, transaction, characterId, price, stopping))
            {
                throw new CharacterSlotException("not_enough_yuanbao");
            }

            var target = current + 1;
            await using (var write = new NpgsqlCommand(
                "insert into \"角色\".character_slot (account, slots, price_yuanbao, txn_id) "
                + "values ($1, $2, $3, $4) "
                + "on conflict (account) do update set "
                + "slots = excluded.slots, "
                + "price_yuanbao = excluded.price_yuanbao, "
                + "txn_id = excluded.txn_id, "
                + "updated_at = now()",
                connection, transaction))
            {
                write.Parameters.AddWithValue(account);
                write.Parameters.AddWithValue(target);
                write.Parameters.AddWithValue(price);
                write.Parameters.AddWithValue(txn);
                await write.ExecuteNonQueryAsync(stopping);
            }

            var balance = await BalanceAsync(connection, transaction, characterId, stopping);
            await transaction.CommitAsync(stopping);

            return new CharacterSlotPurchase(true, "", target, price, balance, false);
        }
        catch (CharacterSlotException ex)
        {
            await transaction.RollbackAsync(stopping);
            var after = await BalanceAsync(connection, null, characterId, stopping);
            var now = await SlotsOfAsync(connection, account, stopping);
            return new CharacterSlotPurchase(false, ex.Code, now, price, after, false);
        }
    }

    // ------------------------------------------------------------------ 核对 / 种子

    /// <summary>
    /// 只给核对与种子账号用：直接把名额推到指定值（只增不减），不扣钱、不抢事务号。
    /// 正常路径不能走这里——产品里的名额只由上面那一笔购买事务写。
    /// 表不在（老库只跑了分表迁移）时返回 false，调用方自己决定要不要因此失败。
    /// </summary>
    public async Task<bool> RawGrantAsync(
        string account, int slots, string txnId, CancellationToken stopping)
    {
        var target = Math.Clamp(slots, table.BaseSlots, 16);
        try
        {
            await using var command = dataSource.CreateCommand(
                "insert into \"角色\".character_slot (account, slots, price_yuanbao, txn_id) "
                + "values ($1, $2, 0, $3) "
                + "on conflict (account) do update set "
                + "slots = greatest(\"角色\".character_slot.slots, excluded.slots), "
                + "updated_at = now()");
            command.Parameters.AddWithValue(account);
            command.Parameters.AddWithValue(target);
            command.Parameters.AddWithValue(txnId);
            await command.ExecuteNonQueryAsync(stopping);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 内部

    static async Task<bool> AccountExistsAsync(
        NpgsqlConnection connection, string account, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select 1 from \"角色\".account where account = $1", connection);
        read.Parameters.AddWithValue(account);
        return await read.ExecuteScalarAsync(stopping) is not null;
    }

    /// <summary>锁账号行：同一账号的两笔购买 / 建角会排队，名额不会被并发各加一次。</summary>
    static async Task<bool> LockAccountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string account,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select account from \"角色\".account where account = $1 for update",
            connection, transaction);
        read.Parameters.AddWithValue(account);
        return await read.ExecuteScalarAsync(stopping) is not null;
    }

    /// <summary>角色必须属于这个账号：第二角色栏是账号的，钱从名下某个角色身上扣。</summary>
    static async Task LockCharacterAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        string account, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select id from \"角色\".character where id = $1 and account = $2 for update",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(account);
        if (await read.ExecuteScalarAsync(stopping) is null)
        {
            throw new CharacterSlotException("character_not_found");
        }
    }

    /// <summary>这个账号现在有几个位：库里有行就按行，没有就按配置的基础位。</summary>
    async Task<int> SlotsOfAsync(
        NpgsqlConnection connection, string account, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select slots from \"角色\".character_slot where account = $1", connection);
        read.Parameters.AddWithValue(account);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? table.BaseSlots : Convert.ToInt32(value);
    }

    async Task<int> SlotsOfAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string account,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select slots from \"角色\".character_slot where account = $1",
            connection, transaction);
        read.Parameters.AddWithValue(account);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? table.BaseSlots : Convert.ToInt32(value);
    }

    static async Task<int> UsedAsync(
        NpgsqlConnection connection, string account, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select count(*) from \"角色\".character where account = $1", connection);
        read.Parameters.AddWithValue(account);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
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
}
