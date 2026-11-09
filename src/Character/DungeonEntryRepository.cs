using Npgsql;

namespace Character;

/// <summary>一次进本的结果。被拒时 Fatigue 是当时的有效疲劳，EntryId 为 0。</summary>
public sealed record DungeonEntryOutcome(
    bool Allowed,
    string Reason,
    int Fatigue,
    int WeeklyShare,
    int FatigueCost,
    int DailyCap,
    long EntryId);

/// <summary>
/// 扣疲劳的副本进本（深渊）。跨天归零、按人数扣疲劳、记一次进本，
/// 三件事在同一个数据库事务里：任何一件不成立就整笔退回，不会出现
/// 「扣了疲劳却没进本」或者「进了本却没扣」。
///
/// 并发靠 select ... for update 锁住角色行：两笔同时冲日上限时，
/// 后一个会等到前一个提交，读到新值，再按上限判断，于是只有一笔能成。
/// </summary>
public sealed class DungeonEntryRepository(NpgsqlDataSource dataSource)
{
    public async Task<DungeonEntryOutcome> EnterWithFatigueAsync(
        long characterId,
        string account,
        DungeonDef dungeon,
        int partySize,
        int dailyCap,
        CancellationToken stopping)
    {
        var cost = checked(dungeon.FatiguePerPlayer * partySize);

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁这一行，读「今天的」有效疲劳：日界变了就当 0。
        //    同时再核对账号归属，别的账号的令牌改不到这个角色。
        int effective;
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
                return new DungeonEntryOutcome(
                    false, "character_not_found", 0, 0, 0, dailyCap, 0);
            }

            effective = reader.GetInt32(0);
            weeklyShare = reader.GetInt32(1);
        }

        // 2. 超过日上限：整笔不做，不扣疲劳也不留进本记录。
        //    恰好扣到上限是允许的（effective + cost == cap 走下面）。
        if (effective + cost > dailyCap)
        {
            await transaction.RollbackAsync(stopping);
            return new DungeonEntryOutcome(
                false, "fatigue_exhausted", effective, weeklyShare, 0, dailyCap, 0);
        }

        // 3. 落账。日界一起刷成今天，第二天自然从 0 起算。
        var fatigue = effective + cost;
        await using (var charge = new NpgsqlCommand(
            "update \"角色\".character set fatigue = $2, fatigue_day = current_date "
            + "where id = $1 and account = $3",
            connection, transaction))
        {
            charge.Parameters.AddWithValue(characterId);
            charge.Parameters.AddWithValue(fatigue);
            charge.Parameters.AddWithValue(account);
            await charge.ExecuteNonQueryAsync(stopping);
        }

        // 4. 记进本，同一个事务。
        long entryId;
        await using (var entry = new NpgsqlCommand(
            "insert into \"角色\".dungeon_entry (character_id, dungeon_id, party_size) "
            + "values ($1, $2, $3) returning id",
            connection, transaction))
        {
            entry.Parameters.AddWithValue(characterId);
            entry.Parameters.AddWithValue(dungeon.Id);
            entry.Parameters.AddWithValue(partySize);
            entryId = Convert.ToInt64(await entry.ExecuteScalarAsync(stopping));
        }

        await transaction.CommitAsync(stopping);
        return new DungeonEntryOutcome(
            true, "", fatigue, weeklyShare, cost, dailyCap, entryId);
    }

    /// <summary>只给核对用：读疲劳、周份额与日界，不经过任何进本逻辑。</summary>
    public async Task<(int Fatigue, int WeeklyShare, DateOnly Day)?> FatigueStateAsync(
        long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select fatigue, weekly_share, fatigue_day from \"角色\".character where id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetInt32(1), DateOnly.FromDateTime(reader.GetDateTime(2)));
    }

    /// <summary>
    /// 只给核对用：把日界改到别的一天，验跨天归零。不是游戏的运维手段。
    /// </summary>
    public async Task SetFatigueDayAsync(
        long characterId, DateOnly day, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set fatigue_day = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(day.ToDateTime(TimeOnly.MinValue));
        await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>
    /// 只给核对用：绕过接口直写疲劳，验库层的日上限 CHECK。
    /// </summary>
    public async Task RawWriteFatigueAsync(long characterId, int fatigue, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set fatigue = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(fatigue);
        await command.ExecuteNonQueryAsync(stopping);
    }
}
