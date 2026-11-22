using Npgsql;

namespace Character;

/// <summary>一个角色现在的背包容量：买过几次、一共几格。全是事实值，不回「还能买几次」。</summary>
public sealed record BagExpandState(
    long CharacterId, int BaseSlots, int Step, int MaxSlots, int Count, int Slots,
    string ProductId, long PriceYuanbao, bool Found);

/// <summary>买一次背包扩容的结果。Ok=false 时 Reason 给拒绝码。</summary>
public sealed record BagExpandPurchase(
    bool Ok, string Reason, int Count, int Slots, long Price, long Balance, bool Duplicate);

/// <summary>
/// 背包容量的唯一算法（B7+B8，方案 12.1「背包与仓库，只加格子」）。
///
/// 容量是**算出来的**，不是存的：`min(base + count × step, max)`。库里只有
/// `character.bag_expand_count` 一个计数列——多存一列格数就有了第二份事实，
/// 改配置的时候两份会打架（C7 数值台账同口径）。
///
/// 三份数都只从 config/character.json 来：`bag_slots` / `bag_expand_step` / `bag_slots_max`。
/// 老配置不写后两个键时 step=0、max=base，容量恒等于 bag_slots，
/// 于是只改过 bag_slots 的那几张老卡的临时配置目录照常起、行为不变。
/// </summary>
public static class BagCapacity
{
    public static int Of(CharacterConfig config, int expandCount)
    {
        var baseSlots = config.BagSlots;
        var step = config.BagExpandStep;
        // 上界复用 BagRepository.MaxBagSlot（库层 inventory.slot 的 CHECK 上界 1..200），
        // 不在这里再写一个 200：同一个上界只留一处（C7 表 C 已登记那一处）。
        var max = Math.Min(config.BagSlotsMax, BagRepository.MaxBagSlot);
        if (step <= 0 || expandCount <= 0)
        {
            return baseSlots;
        }

        var slots = baseSlots + (expandCount * step);
        return slots > max ? max : slots;
    }

    /// <summary>再买一次能不能加格：加不动就是已经到顶（配置关了扩容也按到顶算）。</summary>
    public static bool CanExpand(CharacterConfig config, int expandCount)
    {
        return Of(config, expandCount + 1) > Of(config, expandCount);
    }
}

/// <summary>
/// 商城 / NPC 商店的背包扩容写入口（任务队列 #39，方案 12.1）。
///
///   · 买：一笔事务，锁角色行 → 抢事务号 → 读上限 → 到顶就 bag_slots_max（不扣钱）
///     → 带余额条件扣元宝 → 计数 +1 → 事务内读回余额 → 提交。
///   · 计数只由这一笔写：别处一律只读，扩容不能靠赠品、任务或后台悄悄加。
///   · 不落背包：扩容是状态发货（delivered=false，state=bag_expanded），
///     背包里凭空多一件「背包」反而会和 NPC 卖的基础背包重复。
/// </summary>
public sealed class BagExpandRepository(
    NpgsqlDataSource dataSource,
    CharacterConfig config,
    string productId)
{
    public const int MaxTxnLength = 64;

    // ------------------------------------------------------------------ 只读

    public async Task<BagExpandState> DescribeAsync(long characterId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        var count = await CountAsync(connection, null, characterId, stopping);
        var found = count >= 0;

        return new BagExpandState(
            characterId, config.BagSlots, config.BagExpandStep, config.BagSlotsMax,
            Math.Max(count, 0), BagCapacity.Of(config, Math.Max(count, 0)),
            productId, 0, found);
    }

    /// <summary>按角色现取容量（自己开连接）：RPC 里校验格号上界都用这一条。</summary>
    public async Task<int> SlotsAsync(long characterId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        var count = await CountAsync(connection, null, characterId, stopping);
        return BagCapacity.Of(config, Math.Max(count, 0));
    }

    // ------------------------------------------------------------------ 买

    public async Task<BagExpandPurchase> BuyAsync(
        long characterId, string txnId, long price, CancellationToken stopping)
    {
        var txn = (txnId ?? "").Trim();
        if (txn.Length == 0 || txn.Length > MaxTxnLength)
        {
            return new BagExpandPurchase(false, "txn_missing", 0, 0, price, 0, false);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        try
        {
            var current = await CountAsync(connection, transaction, characterId, stopping);
            if (current < 0)
            {
                throw new BagExpandException("character_not_found");
            }

            if (!await ClaimTxnAsync(connection, transaction, characterId, txn, stopping))
            {
                await transaction.RollbackAsync(stopping);
                var after = await BalanceAsync(connection, null, characterId, stopping);
                return new BagExpandPurchase(false, "duplicate", current,
                    BagCapacity.Of(config, current), price, after, true);
            }

            if (!BagCapacity.CanExpand(config, current))
            {
                // 到顶或配置关了扩容：不再卖，也不扣钱。
                throw new BagExpandException("bag_slots_max");
            }

            if (!await DebitYuanbaoAsync(connection, transaction, characterId, price, stopping))
            {
                throw new BagExpandException("not_enough_yuanbao");
            }

            var target = current + 1;
            await using (var write = new NpgsqlCommand(
                "update \"角色\".character set bag_expand_count = $2 where id = $1",
                connection, transaction))
            {
                write.Parameters.AddWithValue(characterId);
                write.Parameters.AddWithValue(target);
                await write.ExecuteNonQueryAsync(stopping);
            }

            var balance = await BalanceAsync(connection, transaction, characterId, stopping);
            await transaction.CommitAsync(stopping);

            return new BagExpandPurchase(true, "", target, BagCapacity.Of(config, target),
                price, balance, false);
        }
        catch (BagExpandException ex)
        {
            await transaction.RollbackAsync(stopping);
            var after = await BalanceAsync(connection, null, characterId, stopping);
            var now = await CountAsync(connection, null, characterId, stopping);
            var count = Math.Max(now, 0);
            return new BagExpandPurchase(false, ex.Code, count, BagCapacity.Of(config, count),
                price, after, false);
        }
    }

    // ------------------------------------------------------------------ 核对 / 种子

    /// <summary>
    /// 只给核对与种子用：直接把次数推到指定值（只增不减），不扣钱、不抢事务号。
    /// 产品里的次数只由上面那一笔购买事务写。返回 false 表示这一列还不在库里。
    /// </summary>
    public async Task<bool> RawGrantAsync(long characterId, int count, CancellationToken stopping)
    {
        try
        {
            await using var command = dataSource.CreateCommand(
                "update \"角色\".character set bag_expand_count = greatest(bag_expand_count, $2) "
                + "where id = $1");
            command.Parameters.AddWithValue(characterId);
            command.Parameters.AddWithValue(Math.Max(count, 0));
            return await command.ExecuteNonQueryAsync(stopping) > 0;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 内部

    /// <summary>读扩容次数。角色不在返回 -1（调用方据此拒绝），列不在也按 -1 处理。</summary>
    public static async Task<int> CountAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long characterId,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select bag_expand_count from \"角色\".character where id = $1",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? -1 : Convert.ToInt32(value);
    }

    /// <summary>
    /// 按角色现取容量——所有按格号校验的写入口都走这里，不再读 config.BagSlots 的单一值。
    /// 角色不在时退回基础格数（调用方稍后会因为找不到角色而拒绝，这里不另造错误）。
    /// </summary>
    public static async Task<int> SlotsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long characterId,
        CharacterConfig config, CancellationToken stopping)
    {
        var count = await CountAsync(connection, transaction, characterId, stopping);
        return BagCapacity.Of(config, Math.Max(count, 0));
    }

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

/// <summary>扩容被业务规则挡下。Code 直接回给调用方，转成 ok=false + reason。</summary>
public sealed class BagExpandException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
