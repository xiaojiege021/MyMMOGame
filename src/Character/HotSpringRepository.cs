using Npgsql;

namespace Character;

/// <summary>一次温泉的结果。角色不存在时 Found=false，其余字段都是 0。</summary>
public sealed record HotSpringOutcome(
    bool Found,
    int FatigueBefore,
    int Fatigue,
    int Recovered,
    int WeeklyShare);

/// <summary>
/// 温泉：只把当日已用疲劳减回配置里的 hot_spring_recovery（方案写定 1500）。
///
/// 疲劳记的是「今天已经花掉的」，进本加、温泉减；减到 0 为止，不写成负数，
/// 也不越过库层的日上限。跨天仍沿用 P1-2b1 的日界：日界变了就是 0 起算，
/// 顺手把库里昨天的旧值刷成 0，免得留一个看着像今天的值。
///
/// 周份额、进本记录、货币、背包、等级一概不碰——这一行只补疲劳。
/// 并发用 select ... for update 把角色行锁住，两笔温泉一前一后读到各自的新值，
/// 不会把疲劳减成负数，也不会读到半截账。
/// </summary>
public sealed class HotSpringRepository(NpgsqlDataSource dataSource)
{
    public async Task<HotSpringOutcome> UseAsync(
        long characterId,
        string account,
        int recovery,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        int before;
        int weeklyShare;
        await using (var read = new NpgsqlCommand(
            "select (case when fatigue_day <> current_date then 0 else fatigue end), weekly_share "
            + "from \"角色\".character where id = $1 and account = $2 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue(account);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                await transaction.RollbackAsync(stopping);
                return new HotSpringOutcome(false, 0, 0, 0, 0);
            }

            before = reader.GetInt32(0);
            weeklyShare = reader.GetInt32(1);
        }

        // 只恢复已经花掉的那部分：花得比一次恢复量少时，实际恢复量就少一些。
        // 不把余额夹到一个「假成功」的数字上，回包里的 recovered 是真实减掉的量。
        var after = before - recovery;
        if (after < 0)
        {
            after = 0;
        }

        var recovered = before - after;

        await using (var update = new NpgsqlCommand(
            "update \"角色\".character set fatigue = $2, fatigue_day = current_date "
            + "where id = $1 and account = $3",
            connection, transaction))
        {
            update.Parameters.AddWithValue(characterId);
            update.Parameters.AddWithValue(after);
            update.Parameters.AddWithValue(account);
            await update.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new HotSpringOutcome(true, before, after, recovered, weeklyShare);
    }
}
