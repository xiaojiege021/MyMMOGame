using Npgsql;

namespace Social;

public sealed partial class SectRepository
{
    // ---- 写：升级（P2-1a4）。只耗建设，一笔事务 ----
    // select ... for update 锁宗门行 → 校验（掌门 / 5 级封顶 / 建设够不够）→
    // 扣建设 → level = level + 1 → 插 sect_upgrade 流水 → 提交，全部同一个
    // BeginTransaction / 一次 CommitAsync，没有中间提交。不跳级（请求里没有目标等级），
    // 一个字不碰角色 schema、一个字不碰名册（人数不变）。并发两笔靠宗门行锁串行，
    // 第二笔看到 level 已升 / 建设已扣，只会按新等级再判一次，不会重复扣同一档。
    public async Task<UpgradeOutcome> UpgradeAsync(
        long sectId, long actorCharacterId, string txnId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 幂等：同一事务号已经升过级，整笔不做（sect_upgrade.txn_id 主键）。
        await using (var dupe = new NpgsqlCommand(
            "select 1 from \"社交\".sect_upgrade where txn_id = $1 for update",
            connection, transaction))
        {
            dupe.Parameters.AddWithValue(txnId);
            if (await dupe.ExecuteScalarAsync(stopping) is not null)
            {
                await transaction.RollbackAsync(stopping);
                return new UpgradeOutcome(false, "duplicate", sectId, 0, 0, 0, 0, 0, 0);
            }
        }

        // 1. 锁宗门行，读等级 / 建设 / 当前掌门 / 上次升级事务号。reader 读完关掉再回滚。
        var found = false;
        var level = 0;
        long construction = 0, master = 0;
        var lastTxn = "";
        await using (var sect = new NpgsqlCommand(
            "select level, construction, master_character_id, coalesce(last_upgrade_txn, '') "
            + "from \"社交\".sect where id = $1 for update",
            connection, transaction))
        {
            sect.Parameters.AddWithValue(sectId);
            await using (var reader = await sect.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    level = reader.GetInt32(0);
                    construction = reader.GetInt64(1);
                    master = reader.GetInt64(2);
                    lastTxn = reader.GetString(3);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new UpgradeOutcome(false, "sect_not_found", sectId, 0, 0, 0, 0, 0, 0);
        }

        // 2. 发起者必须是现任掌门（复用 a3 的 master_character_id，不新造第二套权限）。
        if (actorCharacterId != master)
        {
            await transaction.RollbackAsync(stopping);
            return new UpgradeOutcome(false, "not_leader", sectId, 0, 0, 0, 0, 0, 0);
        }

        // 同一笔事务号重放：sect.last_upgrade_txn 再挡一道（配合流水表主键）。
        if (string.Equals(lastTxn, txnId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(stopping);
            return new UpgradeOutcome(false, "duplicate", sectId, 0, 0, 0, 0, 0, 0);
        }

        // 3. 5 级封顶：最高档没有下一档 cost，不再升。
        if (level >= sectTable.MaxLevel)
        {
            await transaction.RollbackAsync(stopping);
            return new UpgradeOutcome(false, "at_max_level", sectId, 0, 0, 0, 0, 0, 0);
        }

        var cost = sectTable.CostFor(level);
        if (cost is null)
        {
            await transaction.RollbackAsync(stopping);
            return new UpgradeOutcome(false, "bad_sect_level", sectId, 0, 0, 0, 0, 0, 0);
        }

        // 4. 建设不足：一个字不扣，本次未生效数值给 0。
        if (construction < cost.Value)
        {
            await transaction.RollbackAsync(stopping);
            return new UpgradeOutcome(false, "not_enough_construction", sectId, 0, 0, 0, 0, 0, 0);
        }

        var newLevel = level + 1;
        int memberCap;
        try
        {
            memberCap = sectTable.CapFor(newLevel);
        }
        catch (ConfigException)
        {
            await transaction.RollbackAsync(stopping);
            return new UpgradeOutcome(false, "bad_sect_level", sectId, 0, 0, 0, 0, 0, 0);
        }

        // 5. 扣建设 → level = level + 1 → 写幂等事务号，一条 UPDATE 在同一笔事务里。
        //    名册一列都不碰：已有名册人数不变，上限只由 CapFor(newLevel) 给。
        await using (var update = new NpgsqlCommand(
            "update \"社交\".sect set construction = construction - $2, "
            + "level = level + 1, last_upgrade_txn = $3 where id = $1",
            connection, transaction))
        {
            update.Parameters.AddWithValue(sectId);
            update.Parameters.AddWithValue(cost.Value);
            update.Parameters.AddWithValue(txnId);
            await update.ExecuteNonQueryAsync(stopping);
        }

        // 6. 插升级流水（txn_id 主键挡重放），同一笔事务、同一次 CommitAsync。
        await using (var insert = new NpgsqlCommand(
            "insert into \"社交\".sect_upgrade "
            + "(txn_id, sect_id, from_level, to_level, cost) "
            + "values ($1, $2, $3, $4, $5) on conflict (txn_id) do nothing",
            connection, transaction))
        {
            insert.Parameters.AddWithValue(txnId);
            insert.Parameters.AddWithValue(sectId);
            insert.Parameters.AddWithValue(level);
            insert.Parameters.AddWithValue(newLevel);
            insert.Parameters.AddWithValue(cost.Value);
            await insert.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new UpgradeOutcome(true, "", sectId, level, newLevel, memberCap,
            construction, construction - cost.Value, cost.Value);
    }

    // ---- 只读：DescribeUpgrade / --sect-upgrade-state ----
    public async Task<UpgradeState> UpgradeStateAsync(long sectId, CancellationToken stopping)
    {
        int level = 0;
        long construction = 0;
        var lastTxn = "";
        int memberCount = 0, upgradeTxnCount = 0;
        await using (var command = dataSource.CreateCommand(
            "select s.level, s.construction, coalesce(s.last_upgrade_txn, ''), "
            + "(select count(*) from \"社交\".sect_member m where m.sect_id = s.id), "
            + "(select count(*) from \"社交\".sect_upgrade u where u.sect_id = s.id) "
            + "from \"社交\".sect s where s.id = $1"))
        {
            command.Parameters.AddWithValue(sectId);
            await using (var reader = await command.ExecuteReaderAsync(stopping))
            {
                if (!await reader.ReadAsync(stopping))
                {
                    return new UpgradeState(false, sectId, 0, 0, 0, 0, "", 0, 0);
                }

                level = reader.GetInt32(0);
                construction = reader.GetInt64(1);
                lastTxn = reader.GetString(2);
                memberCount = Convert.ToInt32(reader.GetValue(3));
                upgradeTxnCount = Convert.ToInt32(reader.GetValue(4));
            }
        }

        int memberCap;
        try
        {
            memberCap = sectTable.CapFor(level);
        }
        catch (ConfigException)
        {
            memberCap = 0;
        }

        var nextCost = level >= sectTable.MaxLevel ? 0 : (sectTable.CostFor(level) ?? 0);
        return new UpgradeState(true, sectId, level, memberCap, construction,
            nextCost, lastTxn, memberCount, upgradeTxnCount);
    }

    /// <summary>
    /// 只给核对用：把某个宗门的建设赋值，验「建设不足 / 足够」。不对外、
    /// 不作为玩法来源（建设只从宗门秘境和宗门任务来、不从商城买）。
    /// </summary>
    public async Task<bool> SetConstructionAsync(
        long sectId, long construction, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"社交\".sect set construction = $2 where id = $1");
        command.Parameters.AddWithValue(sectId);
        command.Parameters.AddWithValue(construction);
        return await command.ExecuteNonQueryAsync(stopping) == 1;
    }
}
