using Npgsql;

namespace Character;

/// <summary>相邻档兑换的结果。Ok=false 时 Reason 给拒绝码；数值字段一律留 0。</summary>
public sealed record StoneExchangeOutcome(
    bool Ok,
    string Reason,
    string FromKind,
    string ToKind,
    long FromCost,
    long ToGain,
    long FromAmount,
    long ToAmount,
    int Ratio,
    string HighestName,
    bool Duplicate);

/// <summary>兑换被业务规则挡下。Code 直接回给调用方，转成 ok=false + reason。</summary>
public sealed class StoneExchangeException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// 灵石相邻档兑换的唯一写入点（开发架构 4.1 / P3-2a）。一笔事务：
/// 锁角色行 → 档位判定 → 先抢事务号 → 带余额条件扣低档 → upsert 加高档 →
/// 事务内读回两个余额 → 提交。比例与产出只从 StoneExchangeTable 读，不经过拍卖。
///
/// 「先抢事务号」必须在余额条件之前：升档后重放同一个号，若先判余额就会被误判成
/// 余额不足，撞主键才能正确回 duplicate。任何拒绝都是整笔回滚后返回，
/// 回滚之后不再读库（事务已经结束，再读会抛已完成异常）。
/// </summary>
public sealed class StoneExchangeRepository(
    NpgsqlDataSource dataSource,
    CurrencyTable currencies,
    StoneExchangeTable table,
    CharacterConfig config)
{
    // 启动配置随仓储一起注入，保持与角色服其它仓储同一个形状；这里不另起一套数值。
    public CharacterConfig Config { get; } = config;

    public async Task<StoneExchangeOutcome> ExchangeAsync(
        long characterId, string fromKind, string txnId, CancellationToken stopping)
    {
        var from = (fromKind ?? "").Trim();
        var txn = (txnId ?? "").Trim();
        var to = "";

        if (txn.Length == 0)
        {
            return Rejected("txn_missing", from, to, false);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        try
        {
            // 1. 锁角色行。角色不存在整笔不做。
            var found = false;
            await using (var read = new NpgsqlCommand(
                "select id from \"角色\".character where id = $1 for update",
                connection, transaction))
            {
                read.Parameters.AddWithValue(characterId);
                await using var reader = await read.ExecuteReaderAsync(stopping);
                found = await reader.ReadAsync(stopping);
            }

            if (!found)
            {
                throw new StoneExchangeException("character_not_found");
            }

            // 2. 档位判定：unknown_tier / top_tier / unknown_currency 都在这里挡下。
            if (table.IndexOf(from) < 0)
            {
                throw new StoneExchangeException("unknown_tier");
            }

            to = table.Next(from) ?? throw new StoneExchangeException("top_tier");

            if (currencies.Find(from) is null || currencies.Find(to) is null)
            {
                throw new StoneExchangeException("unknown_currency");
            }

            var fromCost = (long)table.Ratio;
            var toGain = (long)table.Gain;

            // 3. 先抢事务号。撞主键说明这一笔已经兑过，整笔退回，
            //    绝不能走到下面的余额条件被误判成 not_enough_stone。
            await using (var claim = new NpgsqlCommand(
                "insert into \"角色\".stone_exchange_txn "
                + "(txn_id, character_id, from_kind, to_kind, from_cost, to_gain) "
                + "values ($1, $2, $3, $4, $5, $6) "
                + "on conflict (txn_id) do nothing returning txn_id",
                connection, transaction))
            {
                claim.Parameters.AddWithValue(txn);
                claim.Parameters.AddWithValue(characterId);
                claim.Parameters.AddWithValue(from);
                claim.Parameters.AddWithValue(to);
                claim.Parameters.AddWithValue(fromCost);
                claim.Parameters.AddWithValue(toGain);
                if (await claim.ExecuteScalarAsync(stopping) is null)
                {
                    await transaction.RollbackAsync(stopping);
                    return Rejected("duplicate", from, to, true);
                }
            }

            // 4. 带余额条件扣低档。扣不动整笔回滚，回 not_enough_stone。
            await using (var debit = new NpgsqlCommand(
                "update \"角色\".currency set amount = amount - $2, updated_at = now() "
                + "where character_id = $1 and kind = $3 and amount >= $2 returning amount",
                connection, transaction))
            {
                debit.Parameters.AddWithValue(characterId);
                debit.Parameters.AddWithValue(fromCost);
                debit.Parameters.AddWithValue(from);
                if (await debit.ExecuteScalarAsync(stopping) is null)
                {
                    throw new StoneExchangeException("not_enough_stone");
                }
            }

            // 5. upsert 给高档加产出。
            await using (var credit = new NpgsqlCommand(
                "insert into \"角色\".currency (character_id, kind, amount) values ($1, $2, $3) "
                + "on conflict (character_id, kind) do update "
                + "set amount = \"角色\".currency.amount + excluded.amount, updated_at = now()",
                connection, transaction))
            {
                credit.Parameters.AddWithValue(characterId);
                credit.Parameters.AddWithValue(to);
                credit.Parameters.AddWithValue(toGain);
                await credit.ExecuteNonQueryAsync(stopping);
            }

            // 6. 事务内读回两个余额（提交之前，仍在同一笔事务里）。
            var fromAmount = await AmountOfAsync(connection, transaction, characterId, from, stopping);
            var toAmount = await AmountOfAsync(connection, transaction, characterId, to, stopping);

            await transaction.CommitAsync(stopping);

            return new StoneExchangeOutcome(true, "", from, to, fromCost, toGain,
                fromAmount, toAmount, table.Ratio, table.HighestName, false);
        }
        catch (StoneExchangeException ex)
        {
            // 所有 reader 都在各自的 await using 块里关掉了；这里只回滚、不读库。
            await transaction.RollbackAsync(stopping);
            return Rejected(ex.Code, from, to, false);
        }
    }

    StoneExchangeOutcome Rejected(string reason, string from, string kind, bool duplicate) =>
        new(false, reason, from, kind, 0, 0, 0, 0,
            table.Ratio, table.HighestName, duplicate);

    static async Task<long> AmountOfAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, string kind, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select amount from \"角色\".currency where character_id = $1 and kind = $2",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(kind);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value);
    }
}
