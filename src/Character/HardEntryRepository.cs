using Npgsql;

namespace Character;

/// <summary>
/// 一次进高难本的结果。被拒时 Used 是本周当时的有效已用次数，EntryId 为 0。
/// RewardShares 是「这一次的份」，不乘人数（方案 10.1 / 10.2）。
/// </summary>
public sealed record HardEntryOutcome(
    bool Allowed,
    string Reason,
    int Used,
    int Remaining,
    int Cap,
    int RewardShares,
    long EntryId);

/// <summary>
/// 高难本的进本次数（龙巢）。方案第 10.1 节与 10.2 节：每人每周 1 次，
/// 进一次扣 1 次，产出就是这一次的份，不按进本人数翻倍；组人多不会把
/// 一周的总产出抬高。次数以角色服里已经写下的进本记录为准——进本那一刻
/// 就算用掉，进程在奖励写入前崩溃也不退回（技术架构第 108 行）。
///
/// 计数口径：同一角色 + 同一副本在「本周」的「角色」.dungeon_entry 行数，
/// 周界按数据库 date_trunc('week', now())，不按客户端时间。不新建表、不新增列。
///
/// 这一笔只写 dungeon_entry 一行：疲劳、周份额、等级、货币、背包一概不碰
/// （高难本不扣疲劳，也不占巢穴的周份额）。
/// select ... for update 锁住角色行：并发两笔同时冲最后一次时只有一笔能成。
/// </summary>
public sealed class HardEntryRepository(NpgsqlDataSource dataSource)
{
    const string WeekStart = "date_trunc('week', now())";

    public async Task<HardEntryOutcome> EnterHardAsync(
        long characterId,
        string account,
        DungeonDef dungeon,
        int partySize,
        CancellationToken stopping)
    {
        var cap = dungeon.EntriesPerWeek;

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 先锁角色行并核对账号归属：别的账号的令牌改不到这个角色。
        await using (var read = new NpgsqlCommand(
            "select 1 from \"角色\".character where id = $1 and account = $2 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue(account);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                await transaction.RollbackAsync(stopping);
                return new HardEntryOutcome(false, "character_not_found", 0, cap, cap, 0, 0);
            }
        }

        // 2. 本周已用次数：只数进本记录，不读任何内存里的计数。
        int used;
        await using (var count = new NpgsqlCommand(
            "select count(*) from \"角色\".dungeon_entry "
            + "where character_id = $1 and dungeon_id = $2 and entered_at >= " + WeekStart,
            connection, transaction))
        {
            count.Parameters.AddWithValue(characterId);
            count.Parameters.AddWithValue(dungeon.Id);
            used = Convert.ToInt32(await count.ExecuteScalarAsync(stopping));
        }

        // 3. 次数用完：整笔不做，不留进本记录，也不改任何账。
        if (used + 1 > cap)
        {
            await transaction.RollbackAsync(stopping);
            return new HardEntryOutcome(false, "hard_entries_exhausted", used, cap - used, cap, 0, 0);
        }

        // 4. 记这一次进本。reward_shares 就是配置里那一个数，不乘进本人数。
        long entryId;
        await using (var entry = new NpgsqlCommand(
            "insert into \"角色\".dungeon_entry (character_id, dungeon_id, party_size, reward_shares) "
            + "values ($1, $2, $3, $4) returning id",
            connection, transaction))
        {
            entry.Parameters.AddWithValue(characterId);
            entry.Parameters.AddWithValue(dungeon.Id);
            entry.Parameters.AddWithValue(partySize);
            entry.Parameters.AddWithValue(dungeon.RewardPerPlayer);
            entryId = Convert.ToInt64(await entry.ExecuteScalarAsync(stopping));
        }

        await transaction.CommitAsync(stopping);
        return new HardEntryOutcome(
            true, "", used + 1, cap - used - 1, cap, dungeon.RewardPerPlayer, entryId);
    }

    /// <summary>
    /// 只给核对用：读本周已用次数与剩余次数，不经过进本逻辑，一个字都不写。
    /// 角色不存在回 null。
    /// </summary>
    public async Task<(int Used, int Remaining, int Cap)?> HardEntryStateAsync(
        long characterId, string dungeonId, int cap, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select (select count(*) from \"角色\".dungeon_entry "
            + "where character_id = $1 and dungeon_id = $2 and entered_at >= " + WeekStart + ") "
            + "from \"角色\".character where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(dungeonId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        var used = Convert.ToInt32(reader.GetInt64(0));
        return (used, cap - used, cap);
    }

    /// <summary>
    /// 只给核对用：把这个角色在这个本的进本记录整体往前挪一周，验跨周归零。
    /// 不是游戏的运维手段，玩法代码不调用。
    /// </summary>
    public async Task ShiftEntryWeekAsync(
        long characterId, string dungeonId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".dungeon_entry set entered_at = entered_at - interval '7 days' "
            + "where character_id = $1 and dungeon_id = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(dungeonId);
        await command.ExecuteNonQueryAsync(stopping);
    }
}
