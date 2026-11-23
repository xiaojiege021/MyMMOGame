using Npgsql;

namespace Social;

public sealed partial class SectRepository
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
}
