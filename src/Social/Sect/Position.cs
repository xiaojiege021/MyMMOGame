using Npgsql;

namespace Social;

public sealed partial class SectRepository
{
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
}
