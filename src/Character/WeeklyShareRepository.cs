using Npgsql;

namespace Character;

/// <summary>一次占周份额的进本结果。被拒时 Used 是当时的有效已用份额，EntryId 为 0。</summary>
public sealed record WeeklyShareOutcome(
    bool Allowed,
    string Reason,
    int Used,
    int Remaining,
    int Cost,
    int RewardShares,
    int WeeklyCap,
    int Fatigue,
    long EntryId);

/// <summary>
/// 占周份额的副本进本（巢穴）。方案第 10.1 节：每人本次产出与扣掉的份额
/// 都乘以进本人数；单人一次 1 份，4 人一次每人 4 份。每周 12 个单人份，
/// 所以 4 人打一次后已用 4、剩余 8。
///
/// 跨周归零、按人数扣份额、记进本与本次应得份数，全在同一个数据库事务里；
/// select ... for update 锁住角色行，并发两笔冲同一周上限只有一笔能成。
/// 份额不足时整笔回滚：不扣份额、不发份数、不留进本记录。
///
/// weekly_share 记的是「本周已经用掉的单人份」，不是剩余额度；上限只在
/// config/dungeons.json 的 weekly_share_cap，库层只守非负。周界按数据库
/// date_trunc('week', current_date)，不按客户端时间。
/// 这一笔只写 weekly_share、weekly_share_day 与进本记录：疲劳、等级、货币、
/// 背包一概不碰，巢穴与疲劳解绑。
/// </summary>
public sealed class WeeklyShareRepository(NpgsqlDataSource dataSource)
{
    const string WeekStart = "date_trunc('week', current_date::timestamp)::date";

    public async Task<WeeklyShareOutcome> EnterWithWeeklyShareAsync(
        long characterId,
        string account,
        DungeonDef dungeon,
        int partySize,
        int weeklyCap,
        CancellationToken stopping)
    {
        // 一份就是单人通关一次的那一份产出，没有额外系数：4 人一次 = 4 份。
        var cost = partySize;

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁这一行，读「这一周的」有效已用份额：周界变了就当 0。
        //    同时再核对账号归属，别的账号的令牌改不到这个角色。
        int effective;
        int fatigue;
        await using (var read = new NpgsqlCommand(
            "select (case when weekly_share_day <> " + WeekStart + " then 0 else weekly_share end), fatigue "
            + "from \"角色\".character where id = $1 and account = $2 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue(account);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                await transaction.RollbackAsync(stopping);
                return new WeeklyShareOutcome(
                    false, "character_not_found", 0, weeklyCap, 0, 0, weeklyCap, 0, 0);
            }

            effective = reader.GetInt32(0);
            fatigue = reader.GetInt32(1);
        }

        // 2. 超过周上限：整笔不做。恰好扣到上限允许（4 人从 8 到 12 走下面）。
        if (effective + cost > weeklyCap)
        {
            await transaction.RollbackAsync(stopping);
            return new WeeklyShareOutcome(
                false, "weekly_share_exhausted", effective, weeklyCap - effective,
                0, 0, weeklyCap, fatigue, 0);
        }

        // 3. 落账。周界一起刷成本周周首，新的一周自然从 0 起算。
        var used = effective + cost;
        await using (var charge = new NpgsqlCommand(
            "update \"角色\".character set weekly_share = $2, weekly_share_day = " + WeekStart + " "
            + "where id = $1 and account = $3",
            connection, transaction))
        {
            charge.Parameters.AddWithValue(characterId);
            charge.Parameters.AddWithValue(used);
            charge.Parameters.AddWithValue(account);
            await charge.ExecuteNonQueryAsync(stopping);
        }

        // 4. 记进本与本次应得份数，同一个事务。产出的实物种类属掉落表，后面再发。
        long entryId;
        await using (var entry = new NpgsqlCommand(
            "insert into \"角色\".dungeon_entry (character_id, dungeon_id, party_size, reward_shares) "
            + "values ($1, $2, $3, $4) returning id",
            connection, transaction))
        {
            entry.Parameters.AddWithValue(characterId);
            entry.Parameters.AddWithValue(dungeon.Id);
            entry.Parameters.AddWithValue(partySize);
            entry.Parameters.AddWithValue(cost);
            entryId = Convert.ToInt64(await entry.ExecuteScalarAsync(stopping));
        }

        await transaction.CommitAsync(stopping);
        return new WeeklyShareOutcome(
            true, "", used, weeklyCap - used, cost, cost, weeklyCap, fatigue, entryId);
    }

    /// <summary>只给核对用：读已用份额、剩余份额、周界与疲劳，不经过任何进本逻辑。</summary>
    public async Task<(int Used, int Remaining, int Fatigue, DateOnly Week)?> ShareStateAsync(
        long characterId, int weeklyCap, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select (case when weekly_share_day <> " + WeekStart + " then 0 else weekly_share end), "
            + "fatigue, weekly_share_day from \"角色\".character where id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        var used = reader.GetInt32(0);
        return (used, weeklyCap - used, reader.GetInt32(1), DateOnly.FromDateTime(reader.GetDateTime(2)));
    }

    /// <summary>
    /// 只给核对用：把周界改到别的周首，验跨周归零。不是游戏的运维手段。
    /// </summary>
    public async Task SetWeeklyShareDayAsync(
        long characterId, DateOnly day, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set weekly_share_day = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(day.ToDateTime(TimeOnly.MinValue));
        await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>
    /// 只给核对用：绕过接口直写已用份额，造并发现场或验库层的非负 CHECK。
    /// </summary>
    public async Task RawWriteShareAsync(long characterId, int used, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set weekly_share = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(used);
        await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>只给核对用：这个角色在某个副本累计应得多少单人份。</summary>
    public async Task<long> SharesTotalAsync(
        long characterId, string dungeonId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select coalesce(sum(reward_shares), 0) from \"角色\".dungeon_entry "
            + "where character_id = $1 and dungeon_id = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(dungeonId);
        var value = await command.ExecuteScalarAsync(stopping);
        return Convert.ToInt64(value);
    }
}
