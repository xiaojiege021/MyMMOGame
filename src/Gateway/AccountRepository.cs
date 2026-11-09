using Npgsql;

namespace Gateway;

/// <summary>账号表的一行。哈希和盐是 bytea，迭代次数跟着行走。</summary>
public sealed record AccountRow(string Account, byte[] PasswordHash, byte[] PasswordSalt, int Iterations, bool Disabled);

/// <summary>
/// 账号表在 gamedb 的「角色」schema 里。这一行只读账号做口令校验，
/// 建表与种子账号走运维参数；账号的写入以后仍归角色服，网关不越界。
/// </summary>
public sealed class AccountRepository(NpgsqlDataSource dataSource)
{
    public async Task<AccountRow?> FindAsync(string account, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select account, password_hash, password_salt, iterations, disabled "
            + "from \"角色\".account where account = $1");
        command.Parameters.AddWithValue(account);

        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return new AccountRow(
            reader.GetString(0),
            (byte[])reader[1],
            (byte[])reader[2],
            reader.GetInt32(3),
            reader.GetBoolean(4));
    }

    /// <summary>执行建表语句。改表结构只改 db/character/*.sql，不在这里拼 SQL。</summary>
    public async Task<int> MigrateAsync(string sqlPath, CancellationToken stopping)
    {
        var sql = File.ReadAllText(sqlPath);
        await using var command = dataSource.CreateCommand(sql);
        return await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>写一条种子账号。同一账号再跑一次是覆盖，方便核对反复跑。</summary>
    public async Task SeedAsync(string account, string password, int iterations, CancellationToken stopping)
    {
        var (hash, salt) = PasswordHasher.Create(password, iterations);

        await using var command = dataSource.CreateCommand(
            "insert into \"角色\".account (account, password_hash, password_salt, iterations) "
            + "values ($1, $2, $3, $4) "
            + "on conflict (account) do update set "
            + "password_hash = excluded.password_hash, "
            + "password_salt = excluded.password_salt, "
            + "iterations = excluded.iterations, "
            + "disabled = false");
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue(hash);
        command.Parameters.AddWithValue(salt);
        command.Parameters.AddWithValue(iterations);
        await command.ExecuteNonQueryAsync(stopping);
    }

    public async Task<int> DeleteAsync(string account, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand("delete from \"角色\".account where account = $1");
        command.Parameters.AddWithValue(account);
        return await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>
    /// 只给核对用的统计：账号数、不同哈希数、不同盐数、哈希长度区间。
    /// 同一口令的多个账号哈希必须各不相同（盐生效）；哈希长度固定 32 字节，
    /// 说明库里存的不是口令明文。不打印任何哈希。
    /// </summary>
    public async Task<AccountStats> StatsAsync(CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select count(*), count(distinct password_hash), count(distinct password_salt), "
            + "coalesce(min(length(password_hash)), 0), coalesce(max(length(password_hash)), 0) "
            + "from \"角色\".account");
        await using var reader = await command.ExecuteReaderAsync(stopping);
        await reader.ReadAsync(stopping);
        return new AccountStats(
            (int)reader.GetInt64(0),
            (int)reader.GetInt64(1),
            (int)reader.GetInt64(2),
            (int)reader.GetInt32(3),
            (int)reader.GetInt32(4));
    }
}

public sealed record AccountStats(int Accounts, int DistinctHashes, int DistinctSalts, int MinHashBytes, int MaxHashBytes);
