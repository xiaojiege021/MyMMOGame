using Npgsql;

namespace Social;

public sealed record SectState(
    bool Found, long SectId, string Name, int Level, int MemberCap, int MemberCount);

public sealed record JoinOutcome(
    bool Ok, string Reason, long SectId, int Level, int MemberCap, int MemberCount);

public sealed record CreateOutcome(
    bool Ok, string Reason, long SectId, int Level, int MemberCap, int MemberCount);

/// <summary>P2-1a2 任命 / 免职的回包事实值。业务拒绝时 seat_count=0（未生效）。</summary>
public sealed record PositionOutcome(
    bool Ok, string Reason, long SectId, string Position, int SeatCap, int SeatCount);

/// <summary>只读：某宗门各职位在名册里的已占席位（disciple 不占席位，只数人数）。</summary>
public sealed record PositionState(
    bool Found, long SectId, int Master, int ViceMaster, int Elder, int Steward, int Disciple);

public sealed record PositionRow(long CharacterId, string Position);

/// <summary>P2-1a3 传位 / 退出的回包事实值。业务拒绝时未生效数值给 0。</summary>
public sealed record LeadershipOutcome(
    bool Ok, string Reason, long SectId, long OldMaster, long NewMaster,
    int MasterCount, int ViceMasterCount, int MemberCount);

/// <summary>只读：某宗门当前掌门权威、名册席位事实值、上一次传位事务号。</summary>
public sealed record LeadershipState(
    bool Found, long SectId, long MasterCharacterId, long LastTransferOldMaster,
    int MasterCount, int ViceMasterCount, int MemberCount, string LastTransferTxn);

/// <summary>P2-1a4 升级的回包事实值。业务拒绝时未生效数值给 0。</summary>
public sealed record UpgradeOutcome(
    bool Ok, string Reason, long SectId, int OldLevel, int NewLevel, int MemberCap,
    long ConstructionBefore, long ConstructionAfter, long UpgradeCost);

/// <summary>只读：某宗门的等级 / 名册上限 / 建设 / 下一档消耗 / 幂等事务号 / 名册人数 / 升级流水条数。</summary>
public sealed record UpgradeState(
    bool Found, long SectId, int Level, int MemberCap, long Construction,
    long NextCost, string LastUpgradeTxn, int MemberCount, int UpgradeTxnCount);

/// <summary>
/// 宗门与名册的唯一写入点。只写 "社交".sect / "社交".sect_member 两张表，
/// 不 join 角色 schema，也不向战斗服下发人数。
///
/// 收人的人数判定在一笔事务里：for update 锁宗门行 → count(*) → insert。
/// 并发抢最后一个席位时，两个事务会串行在宗门行上，第二个看到已满就拒绝，
/// 库里恒定只多一个人。reader 都在回滚之前读完关掉，避免挂在连接上。
/// </summary>
public sealed class SectRepository(NpgsqlDataSource dataSource, SectTable sectTable)
{
    // ---- 写：建 1 级宗门 ----
    public async Task<CreateOutcome> CreateAsync(
        string name, long founderCharacterId, string txnId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 同一事务号已经建过：整笔不做（与角色服 grant_txn 的同一号幂等对齐）。
        await using (var dupe = new NpgsqlCommand(
            "select id from \"社交\".sect where create_txn = $1 for update",
            connection, transaction))
        {
            dupe.Parameters.AddWithValue(txnId);
            var existing = await dupe.ExecuteScalarAsync(stopping);
            if (existing is not null)
            {
                await transaction.RollbackAsync(stopping);
                return new CreateOutcome(false, "duplicate", Convert.ToInt64(existing), 0, 0, 0);
            }
        }

        // 重名挡在插入之前，不给创建扣款留下「钱扣了宗门没建」的窗口。
        await using (var nameCheck = new NpgsqlCommand(
            "select id from \"社交\".sect where name = $1", connection, transaction))
        {
            nameCheck.Parameters.AddWithValue(name);
            if (await nameCheck.ExecuteScalarAsync(stopping) is not null)
            {
                await transaction.RollbackAsync(stopping);
                return new CreateOutcome(false, "name_taken", 0, 0, 0, 0);
            }
        }

        // 等级与上限都从配置正本第一档读，SQL 里不写第二个数字。
        var first = sectTable.Levels[0];
        long id;
        // 创建时当前掌门就是创建者：master_character_id 是权限判定唯一权威，
        // founder_character_id 只作创建历史。创建者仍不进 sect_member 名册（a1 语义）。
        await using (var insert = new NpgsqlCommand(
            "insert into \"社交\".sect "
            + "(name, level, founder_character_id, create_txn, master_character_id) "
            + "values ($1, $2, $3, $4, $5) returning id", connection, transaction))
        {
            insert.Parameters.AddWithValue(name);
            insert.Parameters.AddWithValue(first.Number);
            insert.Parameters.AddWithValue(founderCharacterId);
            insert.Parameters.AddWithValue(txnId);
            insert.Parameters.AddWithValue(founderCharacterId);
            id = Convert.ToInt64(await insert.ExecuteScalarAsync(stopping));
        }

        await transaction.CommitAsync(stopping);
        return new CreateOutcome(true, "", id, first.Number, first.MemberCap, 0);
    }

    // ---- 写：收人入名册，一笔事务 ----
    public async Task<JoinOutcome> JoinAsync(
        long sectId, long characterId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁宗门行，读等级。reader 读完关掉再回滚，别挂在连接上。
        var found = false;
        var level = 0;
        await using (var sect = new NpgsqlCommand(
            "select level from \"社交\".sect where id = $1 for update",
            connection, transaction))
        {
            sect.Parameters.AddWithValue(sectId);
            await using (var reader = await sect.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    level = reader.GetInt32(0);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new JoinOutcome(false, "sect_not_found", sectId, 0, 0, 0);
        }

        int cap;
        try
        {
            cap = sectTable.CapFor(level);
        }
        catch (ConfigException)
        {
            await transaction.RollbackAsync(stopping);
            return new JoinOutcome(false, "bad_sect_level", sectId, level, 0, 0);
        }

        // 2. 数已经收了几个人（同一笔事务，宗门行还锁着）。
        int count;
        await using (var cnt = new NpgsqlCommand(
            "select count(*) from \"社交\".sect_member where sect_id = $1",
            connection, transaction))
        {
            cnt.Parameters.AddWithValue(sectId);
            count = Convert.ToInt32(await cnt.ExecuteScalarAsync(stopping));
        }

        // 3. 满了就不收，一个字不写；本次未生效的 member_count 给 0。
        if (count >= cap)
        {
            await transaction.RollbackAsync(stopping);
            return new JoinOutcome(false, "sect_full", sectId, level, cap, 0);
        }

        // 4. 插名册。character_id 唯一约束挡重复入册（已在任意宗门都拒）。
        await using (var insert = new NpgsqlCommand(
            "insert into \"社交\".sect_member (sect_id, character_id) values ($1, $2) "
            + "on conflict (character_id) do nothing returning character_id",
            connection, transaction))
        {
            insert.Parameters.AddWithValue(sectId);
            insert.Parameters.AddWithValue(characterId);
            if (await insert.ExecuteScalarAsync(stopping) is null)
            {
                await transaction.RollbackAsync(stopping);
                return new JoinOutcome(false, "already_member", sectId, level, cap, 0);
            }
        }

        await transaction.CommitAsync(stopping);
        return new JoinOutcome(true, "", sectId, level, cap, count + 1);
    }

    // ---- 只读：--sect-list / --sect-state / --sect-members / DescribeSect ----
    public async Task<List<SectState>> ListAsync(CancellationToken stopping)
    {
        var list = new List<SectState>();
        await using var command = dataSource.CreateCommand(
            "select s.id, s.name, s.level, "
            + "(select count(*) from \"社交\".sect_member m where m.sect_id = s.id) "
            + "from \"社交\".sect s order by s.id");
        await using var reader = await command.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            var level = reader.GetInt32(2);
            list.Add(new SectState(true, reader.GetInt64(0), reader.GetString(1), level,
                CapOrZero(level), Convert.ToInt32(reader.GetValue(3))));
        }

        return list;
    }

    public async Task<SectState> StateAsync(long sectId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select s.id, s.name, s.level, "
            + "(select count(*) from \"社交\".sect_member m where m.sect_id = s.id) "
            + "from \"社交\".sect s where s.id = $1");
        command.Parameters.AddWithValue(sectId);
        return await ReadOneAsync(command, sectId, stopping);
    }

    public async Task<SectState> StateByNameAsync(string name, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select s.id, s.name, s.level, "
            + "(select count(*) from \"社交\".sect_member m where m.sect_id = s.id) "
            + "from \"社交\".sect s where s.name = $1");
        command.Parameters.AddWithValue(name);
        return await ReadOneAsync(command, 0, stopping);
    }

    async Task<SectState> ReadOneAsync(
        NpgsqlCommand command, long missingId, CancellationToken stopping)
    {
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return new SectState(false, missingId, "", 0, 0, 0);
        }

        var level = reader.GetInt32(2);
        return new SectState(true, reader.GetInt64(0), reader.GetString(1), level,
            CapOrZero(level), Convert.ToInt32(reader.GetValue(3)));
    }

    public async Task<List<long>> MembersAsync(long sectId, CancellationToken stopping)
    {
        var list = new List<long>();
        await using var command = dataSource.CreateCommand(
            "select character_id from \"社交\".sect_member "
            + "where sect_id = $1 order by joined_at, character_id");
        command.Parameters.AddWithValue(sectId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            list.Add(reader.GetInt64(0));
        }

        return list;
    }

    /// <summary>
    /// 只给核对用：把某个宗门的 level 改到指定档，验「第 31 人被拒」这类上限切换，
    /// 不做升级逻辑（升级属 P2-1a4）。玩法代码不碰这个入口。
    /// </summary>
    public async Task<bool> SetLevelAsync(long sectId, int level, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"社交\".sect set level = $2 where id = $1");
        command.Parameters.AddWithValue(sectId);
        command.Parameters.AddWithValue(level);
        return await command.ExecuteNonQueryAsync(stopping) == 1;
    }

    // 只读查询里的上限也从配置正本读；等级不在表里（只给核对用改坏了）就给 0。
    int CapOrZero(int level)
    {
        try
        {
            return sectTable.CapFor(level);
        }
        catch (ConfigException)
        {
            return 0;
        }
    }

    // ---- 写：任命（P2-1a2）。由现任掌门（创建者）发起，一笔事务 ----
    // select ... for update 锁宗门行 → 查目标在名册 → 数该职位已占席位 → update/拒绝。
    // 并发抢最后一个席位时两个事务串行在宗门行上，第二个看到已满就拒绝。
    // 只写 "社交".sect_member.position，不碰等级/境界/伤害/经验/属性/角色 schema。
    public async Task<PositionOutcome> AppointAsync(
        long sectId, long actorCharacterId, long targetCharacterId, string position,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁宗门行，读当前掌门权威。reader 读完关掉再回滚，别挂在连接上。
        // P2-1a3：掌门可变，not_leader 改查 sect.master_character_id（创建时=创建者）。
        var found = false;
        long master = 0;
        await using (var sect = new NpgsqlCommand(
            "select master_character_id from \"社交\".sect where id = $1 for update",
            connection, transaction))
        {
            sect.Parameters.AddWithValue(sectId);
            await using (var reader = await sect.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    master = reader.GetInt64(0);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "sect_not_found", sectId, position, 0, 0);
        }

        // 最小权限出口：任命 / 免职只由现任掌门发起，其余回 not_leader。
        if (actorCharacterId != master)
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "not_leader", sectId, position, 0, 0);
        }

        // 职位名只认五个；任命只针对有席位的四个，默认弟子走免职。
        if (!SectTable.IsKnownPosition(position) || !SectTable.IsSeatedPosition(position))
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "position_unknown", sectId, position, 0, 0);
        }

        int cap;
        try
        {
            cap = sectTable.SeatCapFor(position);
        }
        catch (ConfigException)
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "position_unknown", sectId, position, 0, 0);
        }

        // 2. 查目标在名册（锁成员行），不在就拒。
        var targetFound = false;
        var current = "";
        await using (var target = new NpgsqlCommand(
            "select position from \"社交\".sect_member "
            + "where sect_id = $1 and character_id = $2 for update",
            connection, transaction))
        {
            target.Parameters.AddWithValue(sectId);
            target.Parameters.AddWithValue(targetCharacterId);
            await using (var reader = await target.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    targetFound = true;
                    current = reader.GetString(0);
                }
            }
        }

        if (!targetFound)
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "not_member", sectId, position, cap, 0);
        }

        // 3. 数该职位已占席位（同一笔事务，宗门行还锁着）。
        var count = await CountPositionAsync(
            connection, transaction, sectId, position, stopping);

        // 已是该职位：幂等成功，不重复改（重放不重复改）。
        if (current == position)
        {
            await transaction.CommitAsync(stopping);
            return new PositionOutcome(true, "", sectId, position, cap, count);
        }

        // 4. 超过席位就拒，一个字不写；本次未生效 seat_count 给 0。
        if (count >= cap)
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "position_full", sectId, position, cap, 0);
        }

        // 5. 只写 position 列。
        await using (var update = new NpgsqlCommand(
            "update \"社交\".sect_member set position = $3 "
            + "where sect_id = $1 and character_id = $2", connection, transaction))
        {
            update.Parameters.AddWithValue(sectId);
            update.Parameters.AddWithValue(targetCharacterId);
            update.Parameters.AddWithValue(position);
            await update.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new PositionOutcome(true, "", sectId, position, cap, count + 1);
    }

    // ---- 写：免职（P2-1a2）。由现任掌门发起，免成默认弟子，一笔事务 ----
    // 幂等：已是 disciple 也回 ok，不重复改；只写 position 列。
    public async Task<PositionOutcome> DismissAsync(
        long sectId, long actorCharacterId, long targetCharacterId, string position,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // P2-1a3：not_leader 改查 sect.master_character_id（当前掌门唯一权威）。
        var found = false;
        long master = 0;
        await using (var sect = new NpgsqlCommand(
            "select master_character_id from \"社交\".sect where id = $1 for update",
            connection, transaction))
        {
            sect.Parameters.AddWithValue(sectId);
            await using (var reader = await sect.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    master = reader.GetInt64(0);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "sect_not_found", sectId, position, 0, 0);
        }

        if (actorCharacterId != master)
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "not_leader", sectId, position, 0, 0);
        }

        if (!SectTable.IsKnownPosition(position))
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "position_unknown", sectId, position, 0, 0);
        }

        var targetFound = false;
        await using (var target = new NpgsqlCommand(
            "select 1 from \"社交\".sect_member "
            + "where sect_id = $1 and character_id = $2 for update",
            connection, transaction))
        {
            target.Parameters.AddWithValue(sectId);
            target.Parameters.AddWithValue(targetCharacterId);
            targetFound = await target.ExecuteScalarAsync(stopping) is not null;
        }

        if (!targetFound)
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "not_member", sectId, position, 0, 0);
        }

        await using (var update = new NpgsqlCommand(
            "update \"社交\".sect_member set position = 'disciple' "
            + "where sect_id = $1 and character_id = $2", connection, transaction))
        {
            update.Parameters.AddWithValue(sectId);
            update.Parameters.AddWithValue(targetCharacterId);
            await update.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new PositionOutcome(true, "", sectId, SectTable.DefaultPosition, 0, 0);
    }

    static async Task<int> CountPositionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long sectId, string position, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from \"社交\".sect_member "
            + "where sect_id = $1 and position = $2", connection, transaction);
        command.Parameters.AddWithValue(sectId);
        command.Parameters.AddWithValue(position);
        return Convert.ToInt32(await command.ExecuteScalarAsync(stopping));
    }

    // ---- 只读：DescribePositions / --sect-position-state ----
    public async Task<PositionState> PositionStateAsync(long sectId, CancellationToken stopping)
    {
        await using var exists = dataSource.CreateCommand(
            "select 1 from \"社交\".sect where id = $1");
        exists.Parameters.AddWithValue(sectId);
        if (await exists.ExecuteScalarAsync(stopping) is null)
        {
            return new PositionState(false, sectId, 0, 0, 0, 0, 0);
        }

        int master = 0, vice = 0, elder = 0, steward = 0, disciple = 0;
        await using var command = dataSource.CreateCommand(
            "select position, count(*) from \"社交\".sect_member "
            + "where sect_id = $1 group by position");
        command.Parameters.AddWithValue(sectId);
        await using (var reader = await command.ExecuteReaderAsync(stopping))
        {
            while (await reader.ReadAsync(stopping))
            {
                var p = reader.GetString(0);
                var n = Convert.ToInt32(reader.GetValue(1));
                switch (p)
                {
                    case "master": master = n; break;
                    case "vice_master": vice = n; break;
                    case "elder": elder = n; break;
                    case "steward": steward = n; break;
                    case "disciple": disciple = n; break;
                }
            }
        }

        return new PositionState(true, sectId, master, vice, elder, steward, disciple);
    }

    // ---- 只读：--sect-position-members ----
    public async Task<List<PositionRow>> PositionMembersAsync(
        long sectId, CancellationToken stopping)
    {
        var list = new List<PositionRow>();
        await using var command = dataSource.CreateCommand(
            "select character_id, position from \"社交\".sect_member "
            + "where sect_id = $1 order by joined_at, character_id");
        command.Parameters.AddWithValue(sectId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            list.Add(new PositionRow(reader.GetInt64(0), reader.GetString(1)));
        }

        return list;
    }

    // ---- 写：传位（P2-1a3）。只给现任副掌门，一笔事务 ----
    // select ... for update 锁宗门行 → 校验发起者 / 目标 →
    // ①把名册里所有 master 行降为配置去向 ②升新掌门为 master
    // ③写 sect.master_character_id + last_transfer_txn ④插 sect_transfer 挡重放，
    // 全部在同一个 BeginTransaction / CommitAsync 里，没有中间提交。
    // 提交后 master_character_id 与名册里唯一的 master 行恒一致。
    public async Task<LeadershipOutcome> TransferAsync(
        long sectId, long actorCharacterId, long targetCharacterId, string txnId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 幂等：同一事务号已经传过位，整笔不做（sect_transfer.txn_id 主键）。
        await using (var dupe = new NpgsqlCommand(
            "select 1 from \"社交\".sect_transfer where txn_id = $1 for update",
            connection, transaction))
        {
            dupe.Parameters.AddWithValue(txnId);
            if (await dupe.ExecuteScalarAsync(stopping) is not null)
            {
                await transaction.RollbackAsync(stopping);
                return new LeadershipOutcome(false, "duplicate", sectId, 0, 0, 0, 0, 0);
            }
        }

        // 1. 锁宗门行，读当前掌门权威。reader 读完关掉再回滚。
        var found = false;
        long master = 0;
        await using (var sect = new NpgsqlCommand(
            "select master_character_id from \"社交\".sect where id = $1 for update",
            connection, transaction))
        {
            sect.Parameters.AddWithValue(sectId);
            await using (var reader = await sect.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    master = reader.GetInt64(0);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new LeadershipOutcome(false, "sect_not_found", sectId, 0, 0, 0, 0, 0);
        }

        // 传位只由现任掌门发起。
        if (actorCharacterId != master)
        {
            await transaction.RollbackAsync(stopping);
            return new LeadershipOutcome(false, "not_leader", sectId, 0, 0, 0, 0, 0);
        }

        // 2. 目标必须在本宗名册且是现任副掌门：弟子 / 长老 / 执事 / 非成员 / 不存在全拒。
        var targetOk = false;
        await using (var target = new NpgsqlCommand(
            "select 1 from \"社交\".sect_member "
            + "where sect_id = $1 and character_id = $2 and position = 'vice_master' for update",
            connection, transaction))
        {
            target.Parameters.AddWithValue(sectId);
            target.Parameters.AddWithValue(targetCharacterId);
            targetOk = await target.ExecuteScalarAsync(stopping) is not null;
        }

        if (!targetOk)
        {
            await transaction.RollbackAsync(stopping);
            return new LeadershipOutcome(false, "not_vice_master", sectId, 0, 0, 0, 0, 0);
        }

        // 3. 先降名册里所有 master 行（含 a2 任命出来的那条；掌门本人不在名册时
        //    按 character_id 找是 no-op，按 position 扫才能清干净），再升新掌门。
        await using (var demote = new NpgsqlCommand(
            "update \"社交\".sect_member set position = $2 "
            + "where sect_id = $1 and position = 'master'", connection, transaction))
        {
            demote.Parameters.AddWithValue(sectId);
            demote.Parameters.AddWithValue(sectTable.Transfer.OldMasterPosition);
            await demote.ExecuteNonQueryAsync(stopping);
        }

        await using (var promote = new NpgsqlCommand(
            "update \"社交\".sect_member set position = 'master' "
            + "where sect_id = $1 and character_id = $2", connection, transaction))
        {
            promote.Parameters.AddWithValue(sectId);
            promote.Parameters.AddWithValue(targetCharacterId);
            await promote.ExecuteNonQueryAsync(stopping);
        }

        // 4. 写宗门行的当前掌门权威与幂等事务号（两处一起写、同一事务）。
        await using (var updateSect = new NpgsqlCommand(
            "update \"社交\".sect set master_character_id = $2, last_transfer_txn = $3 "
            + "where id = $1", connection, transaction))
        {
            updateSect.Parameters.AddWithValue(sectId);
            updateSect.Parameters.AddWithValue(targetCharacterId);
            updateSect.Parameters.AddWithValue(txnId);
            await updateSect.ExecuteNonQueryAsync(stopping);
        }

        await using (var insert = new NpgsqlCommand(
            "insert into \"社交\".sect_transfer (txn_id, sect_id, old_master, new_master) "
            + "values ($1, $2, $3, $4) on conflict (txn_id) do nothing",
            connection, transaction))
        {
            insert.Parameters.AddWithValue(txnId);
            insert.Parameters.AddWithValue(sectId);
            insert.Parameters.AddWithValue(master);
            insert.Parameters.AddWithValue(targetCharacterId);
            await insert.ExecuteNonQueryAsync(stopping);
        }

        // 事务内读回提交后的事实值（同一连接、同一事务，不开第二条连接）。
        int masterCount = 0, viceCount = 0, memberCount = 0;
        await using (var counts = new NpgsqlCommand(
            "select count(*), "
            + "count(*) filter (where position = 'master'), "
            + "count(*) filter (where position = 'vice_master') "
            + "from \"社交\".sect_member where sect_id = $1", connection, transaction))
        {
            counts.Parameters.AddWithValue(sectId);
            await using (var reader = await counts.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    memberCount = Convert.ToInt32(reader.GetValue(0));
                    masterCount = Convert.ToInt32(reader.GetValue(1));
                    viceCount = Convert.ToInt32(reader.GetValue(2));
                }
            }
        }

        await transaction.CommitAsync(stopping);
        return new LeadershipOutcome(true, "", sectId, master, targetCharacterId,
            masterCount, viceCount, memberCount);
    }

    // ---- 写：退出宗门（P2-1a3）。掌门不能直接退，有职位先辞成弟子，一笔事务 ----
    public async Task<LeadershipOutcome> LeaveAsync(
        long sectId, long characterId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        var found = false;
        long master = 0;
        await using (var sect = new NpgsqlCommand(
            "select master_character_id from \"社交\".sect where id = $1 for update",
            connection, transaction))
        {
            sect.Parameters.AddWithValue(sectId);
            await using (var reader = await sect.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    master = reader.GetInt64(0);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new LeadershipOutcome(false, "sect_not_found", sectId, 0, 0, 0, 0, 0);
        }

        // 掌门不能直接退出：判 master_character_id（掌门可以不在名册，
        // 用 position='master' 判会漏掉）；掌门要先传位降成弟子才能退。
        if (characterId == master)
        {
            await transaction.RollbackAsync(stopping);
            return new LeadershipOutcome(false, "master_cannot_leave", sectId, 0, 0, 0, 0, 0);
        }

        var memberFound = false;
        var position = "";
        await using (var member = new NpgsqlCommand(
            "select position from \"社交\".sect_member "
            + "where sect_id = $1 and character_id = $2 for update",
            connection, transaction))
        {
            member.Parameters.AddWithValue(sectId);
            member.Parameters.AddWithValue(characterId);
            await using (var reader = await member.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    memberFound = true;
                    position = reader.GetString(0);
                }
            }
        }

        if (!memberFound)
        {
            await transaction.RollbackAsync(stopping);
            return new LeadershipOutcome(false, "not_member", sectId, 0, 0, 0, 0, 0);
        }

        // 有职位的人（副掌门 / 长老 / 执事 / master 展示行）先辞成弟子再退出。
        if (position != SectTable.DefaultPosition)
        {
            await transaction.RollbackAsync(stopping);
            return new LeadershipOutcome(false, "resign_first", sectId, 0, 0, 0, 0, 0);
        }

        // 弟子退会：删名册一行，一笔事务；该 character_id 之后可再被收。
        await using (var del = new NpgsqlCommand(
            "delete from \"社交\".sect_member where sect_id = $1 and character_id = $2",
            connection, transaction))
        {
            del.Parameters.AddWithValue(sectId);
            del.Parameters.AddWithValue(characterId);
            await del.ExecuteNonQueryAsync(stopping);
        }

        int masterCount = 0, viceCount = 0, memberCount = 0;
        await using (var counts = new NpgsqlCommand(
            "select count(*), "
            + "count(*) filter (where position = 'master'), "
            + "count(*) filter (where position = 'vice_master') "
            + "from \"社交\".sect_member where sect_id = $1", connection, transaction))
        {
            counts.Parameters.AddWithValue(sectId);
            await using (var reader = await counts.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    memberCount = Convert.ToInt32(reader.GetValue(0));
                    masterCount = Convert.ToInt32(reader.GetValue(1));
                    viceCount = Convert.ToInt32(reader.GetValue(2));
                }
            }
        }

        await transaction.CommitAsync(stopping);
        return new LeadershipOutcome(true, "", sectId, 0, 0,
            masterCount, viceCount, memberCount);
    }

    // ---- 只读：DescribeLeadership / --sect-leadership-state ----
    public async Task<LeadershipState> LeadershipStateAsync(long sectId, CancellationToken stopping)
    {
        long master = 0;
        var txn = "";
        int masterCount = 0, viceCount = 0, memberCount = 0;
        await using (var command = dataSource.CreateCommand(
            "select s.master_character_id, coalesce(s.last_transfer_txn, ''), "
            + "(select count(*) from \"社交\".sect_member m where m.sect_id = s.id), "
            + "(select count(*) from \"社交\".sect_member m "
            + "where m.sect_id = s.id and m.position = 'master'), "
            + "(select count(*) from \"社交\".sect_member m "
            + "where m.sect_id = s.id and m.position = 'vice_master') "
            + "from \"社交\".sect s where s.id = $1"))
        {
            command.Parameters.AddWithValue(sectId);
            await using (var reader = await command.ExecuteReaderAsync(stopping))
            {
                if (!await reader.ReadAsync(stopping))
                {
                    return new LeadershipState(false, sectId, 0, 0, 0, 0, 0, "");
                }

                master = reader.GetInt64(0);
                txn = reader.GetString(1);
                memberCount = Convert.ToInt32(reader.GetValue(2));
                masterCount = Convert.ToInt32(reader.GetValue(3));
                viceCount = Convert.ToInt32(reader.GetValue(4));
            }
        }

        // 上一笔传位的旧掌门（只读事实值）。reader 读完关掉再查，别挂在连接上。
        long oldMaster = 0;
        if (txn.Length > 0)
        {
            await using var prev = dataSource.CreateCommand(
                "select old_master from \"社交\".sect_transfer where txn_id = $1");
            prev.Parameters.AddWithValue(txn);
            var v = await prev.ExecuteScalarAsync(stopping);
            if (v is not null)
            {
                oldMaster = Convert.ToInt64(v);
            }
        }

        return new LeadershipState(true, sectId, master, oldMaster,
            masterCount, viceCount, memberCount, txn);
    }

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
