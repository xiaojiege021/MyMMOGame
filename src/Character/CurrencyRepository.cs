using Npgsql;

namespace Character;

/// <summary>
/// 货币的加减。一个角色一个币种一行，没有的行按 0。
/// 加减用一条语句完成：余额不足时那条 update 影响 0 行，
/// 调用方据此判 insufficient——不会先读再写，也就没有两次请求之间的空隙。
/// </summary>
public sealed class CurrencyRepository(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// 加减一笔。返回新余额；余额会变成负数时返回 null（调用方转 insufficient）。
    ///
    /// 加和减分两条语句，不能合成一条 upsert：PostgreSQL 在 ON CONFLICT 里会先校验
    /// 拟插入的那一行，扣钱时传进去的负数会直接撞 amount >= 0 的 CHECK，
    /// 根本轮不到 DO UPDATE 的 WHERE。所以加用 upsert，减只用 update。
    /// </summary>
    public async Task<long?> AddAsync(
        long characterId,
        string kind,
        long delta,
        CancellationToken stopping)
    {
        var sql = delta > 0
            ? """
              insert into "角色".currency (character_id, kind, amount)
              values ($1, $2, $3)
              on conflict (character_id, kind) do update
                  set amount = currency.amount + excluded.amount,
                      updated_at = now()
              returning amount
              """
            : """
              update "角色".currency
                 set amount = amount + $3,
                     updated_at = now()
               where character_id = $1
                 and kind = $2
                 and amount + $3 >= 0
              returning amount
              """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(kind);
        command.Parameters.AddWithValue(delta);

        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            // 扣钱时没更新到行：要么余额不够，要么这个币种还没记过账（按 0 算）。
            return null;
        }

        return reader.GetInt64(0);
    }

    /// <summary>读回全部币种。没记过账的按 0，调用方把顺序摆好。</summary>
    public async Task<Dictionary<string, long>> AllAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select kind, amount from \"角色\".currency where character_id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);

        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(stopping))
        {
            map[reader.GetString(0)] = reader.GetInt64(1);
        }

        return map;
    }

    /// <summary>
    /// 只给核对用：绕过接口直写一个数，验库层 CHECK 会不会挡住负数。
    /// 正常路径不能走这里。
    /// </summary>
    public async Task RawWriteAsync(long characterId, string kind, long amount, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".currency set amount = $3 where character_id = $1 and kind = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(kind);
        command.Parameters.AddWithValue(amount);
        await command.ExecuteNonQueryAsync(stopping);
    }
}
