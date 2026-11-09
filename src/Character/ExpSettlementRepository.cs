using Npgsql;

namespace Character;

/// <summary>一次经验结算的结果。Duplicate 表示这个事务号已经发过，本次没有再发。</summary>
public sealed record ExpSettlementOutcome(
    bool Found,
    bool Duplicate,
    long BaseExp,
    long PartyBonus,
    long ExpGain,
    int LevelBefore,
    int Level,
    long Exp);

/// <summary>
/// 等级经验的唯一写入点。活动、副本、任务、竞技场、战场、城战和世界 Boss
/// 都调同一笔：基础经验 + （同一场至少两人时）一截少量加成，只加经验。
///
/// 事务顺序与前面的发奖/进本一致：锁角色行 → 抢事务号 → 只写 level 与 exp → 提交。
/// 抢不到号（重复提交）整笔回滚；并发两笔不同号冲同一角色由行锁串行，
/// 不会把这一笔算丢。等级到 level_cap 就不再升，库层 CHECK 仍在。
///
/// account 为空表示服间可信调用（战斗服通关后直连），只按角色编号归属；
/// 网关转发时一定带账号，where 里再核一次，别的账号改不到这个角色。
/// 这一笔只 set level / exp 与写 exp_settlement 流水：疲劳、周份额、货币、
/// 背包、进本记录一概不碰。
/// </summary>
public sealed class ExpSettlementRepository(NpgsqlDataSource dataSource)
{
    public const int MaxTxnLength = 128;
    public const int MaxSourceLength = 32;

    public async Task<ExpSettlementOutcome> SettleAsync(
        long characterId,
        string? account,
        string txnId,
        string source,
        int partySize,
        ExpTable table,
        CancellationToken stopping)
    {
        var baseExp = table.BaseExp;
        var bonus = table.PartyBonus(partySize);
        var gain = baseExp + bonus;

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁角色行并核归属。带账号的走网关口径，空账号走服间口径。
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        int levelBefore = 0;
        long expBefore = 0;
        var found = false;
        await using (var read = new NpgsqlCommand(
            "select level, exp from \"角色\".character " + owned,
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                read.Parameters.AddWithValue(account);
            }

            // 先把 reader 读完关掉再回滚：连接上还挂着 reader 时发 rollback
            // 会抛异常，那样「别人的角色」会被当成服务端内部错误而不是 404。
            await using (var reader = await read.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    levelBefore = reader.GetInt32(0);
                    expBefore = reader.GetInt64(1);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new ExpSettlementOutcome(false, false, 0, 0, 0, 0, 0, 0);
        }

        var (level, exp) = table.Apply(levelBefore, expBefore, gain);

        // 2. 抢事务号。同一个号已经发过就整笔不做，返回当时的状态。
        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".exp_settlement "
            + "(txn_id, character_id, source, party_size, base_exp, party_bonus, exp_gain, "
            + " level_before, level_after) "
            + "values ($1, $2, $3, $4, $5, $6, $7, $8, $9) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            claim.Parameters.AddWithValue(source);
            claim.Parameters.AddWithValue(partySize);
            claim.Parameters.AddWithValue(baseExp);
            claim.Parameters.AddWithValue(bonus);
            claim.Parameters.AddWithValue(gain);
            claim.Parameters.AddWithValue(levelBefore);
            claim.Parameters.AddWithValue(level);
            var claimed = await claim.ExecuteScalarAsync(stopping);
            if (claimed is null)
            {
                await transaction.RollbackAsync(stopping);
                return new ExpSettlementOutcome(
                    true, true, 0, 0, 0, levelBefore, levelBefore, expBefore);
            }
        }

        // 3. 只写这两列。越界等级写不进去，事务回滚后流水也不会留下。
        await using (var write = new NpgsqlCommand(
            "update \"角色\".character set level = $2, exp = $3 where id = $1",
            connection, transaction))
        {
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue(level);
            write.Parameters.AddWithValue(exp);
            await write.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new ExpSettlementOutcome(
            true, false, baseExp, bonus, gain, levelBefore, level, exp);
    }

    /// <summary>只给核对用：读等级、当前级经验、结算笔数与累计入账经验。</summary>
    public async Task<(int Level, long Exp, int Rows, long TotalGain)?> ExpStateAsync(
        long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select c.level, c.exp, "
            + "(select count(*) from \"角色\".exp_settlement s where s.character_id = c.id), "
            + "(select coalesce(sum(s.exp_gain), 0) from \"角色\".exp_settlement s where s.character_id = c.id) "
            + "from \"角色\".character c where c.id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3));
    }

    /// <summary>只给核对用：绕过接口直写经验，验库层的非负 CHECK。</summary>
    public async Task RawWriteExpAsync(long characterId, long exp, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set exp = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(exp);
        await command.ExecuteNonQueryAsync(stopping);
    }
}
