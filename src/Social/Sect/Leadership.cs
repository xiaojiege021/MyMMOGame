using Npgsql;

namespace Social;

public sealed partial class SectRepository
{
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
}
