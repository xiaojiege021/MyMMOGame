using Npgsql;

namespace Character;

/// <summary>
/// 逃跑惩罚的一条事实值。「还要等多久」全部按到期时刻现算，不存剩余秒数——
/// 剩余量是时间的函数，存下来就会在进程重启后变成错的。
/// </summary>
public sealed record DeserterFact(
    bool Found,
    long RemainingMs,
    long UntilMs,
    long SessionId,
    string Reason);

/// <summary>
/// Q9/H4：逃跑惩罚只有这一张表、只有这一个写入口。
///
/// 写进来的东西只有「哪一场、为什么、要等多久」；金币、经验、疲劳、周份额、
/// 进本记录一个都不碰——惩罚的作用是挡住下一次进本，不是扣东西。
/// 同一个人重复判罚取较晚的到期时刻（不叠加），并保留较晚那一次的原因。
/// </summary>
public sealed class DeserterRepository(NpgsqlDataSource dataSource)
{
    /// <summary>还要等多久。没被罚过、已经过期都是 0；`found` 只说明有没有这一行。</summary>
    public async Task<DeserterFact> DescribeAsync(long characterId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var command = new NpgsqlCommand(
            "select until_ms, session_id, reason from \"角色\".deserter_penalty where character_id = $1",
            connection);
        command.Parameters.AddWithValue(characterId);

        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return new DeserterFact(false, 0, 0, 0, "");
        }

        var untilMs = reader.GetInt64(0);
        var sessionId = reader.GetInt64(1);
        var reason = reader.GetString(2);
        return new DeserterFact(true, Remaining(untilMs), untilMs, sessionId, reason);
    }

    /// <summary>
    /// 记一次判罚。penalty_ms &lt;= 0 时一律不写（配置关掉惩罚就是这个口子）；
    /// reason 只认 voluntary / timeout，与库里的 CHECK 一致，别的地方写不进来。
    /// </summary>
    public async Task<DeserterFact> RecordAsync(
        long characterId, long sessionId, string reason, long penaltyMs, CancellationToken stopping)
    {
        if (penaltyMs <= 0)
        {
            return await DescribeAsync(characterId, stopping);
        }

        if (reason is not ("voluntary" or "timeout"))
        {
            return new DeserterFact(false, 0, 0, 0, "");
        }

        var untilMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + penaltyMs;

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var command = new NpgsqlCommand(
            "insert into \"角色\".deserter_penalty (character_id, until_ms, session_id, reason, recorded_at) "
            + "values ($1, $2, $3, $4, now()) "
            + "on conflict (character_id) do update set "
            + "until_ms = greatest(\"角色\".deserter_penalty.until_ms, excluded.until_ms), "
            + "session_id = case when excluded.until_ms >= \"角色\".deserter_penalty.until_ms "
            + "then excluded.session_id else \"角色\".deserter_penalty.session_id end, "
            + "reason = case when excluded.until_ms >= \"角色\".deserter_penalty.until_ms "
            + "then excluded.reason else \"角色\".deserter_penalty.reason end, "
            + "recorded_at = now() "
            + "returning until_ms, session_id, reason",
            connection);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(untilMs);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(reason);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                return new DeserterFact(false, 0, 0, 0, "");
            }

            var stored = reader.GetInt64(0);
            var storedSession = reader.GetInt64(1);
            var storedReason = reader.GetString(2);
            return new DeserterFact(true, Remaining(stored), stored, storedSession, storedReason);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // 角色不存在：不写惩罚，回「没这一行」让调用方按 character_not_found 处理。
            return new DeserterFact(false, 0, 0, 0, "");
        }
    }

    static long Remaining(long untilMs)
    {
        var left = untilMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return left > 0 ? left : 0;
    }
}
