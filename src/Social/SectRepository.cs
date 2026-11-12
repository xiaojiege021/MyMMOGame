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
        await using (var insert = new NpgsqlCommand(
            "insert into \"社交\".sect (name, level, founder_character_id, create_txn) "
            + "values ($1, $2, $3, $4) returning id", connection, transaction))
        {
            insert.Parameters.AddWithValue(name);
            insert.Parameters.AddWithValue(first.Number);
            insert.Parameters.AddWithValue(founderCharacterId);
            insert.Parameters.AddWithValue(txnId);
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

        // 1. 锁宗门行，读创建者。reader 读完关掉再回滚，别挂在连接上。
        var found = false;
        long founder = 0;
        await using (var sect = new NpgsqlCommand(
            "select founder_character_id from \"社交\".sect where id = $1 for update",
            connection, transaction))
        {
            sect.Parameters.AddWithValue(sectId);
            await using (var reader = await sect.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    founder = reader.GetInt64(0);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "sect_not_found", sectId, position, 0, 0);
        }

        // 最小权限出口：任命 / 免职只由现任掌门（创建者）发起，其余回 not_leader。
        if (actorCharacterId != founder)
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

        var found = false;
        long founder = 0;
        await using (var sect = new NpgsqlCommand(
            "select founder_character_id from \"社交\".sect where id = $1 for update",
            connection, transaction))
        {
            sect.Parameters.AddWithValue(sectId);
            await using (var reader = await sect.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    founder = reader.GetInt64(0);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new PositionOutcome(false, "sect_not_found", sectId, position, 0, 0);
        }

        if (actorCharacterId != founder)
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
}
