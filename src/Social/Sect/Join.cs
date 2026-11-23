using Npgsql;

namespace Social;

public sealed partial class SectRepository
{
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
}
