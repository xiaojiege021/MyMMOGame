using Npgsql;

namespace Character;

/// <summary>一次加点的结果。Ok=false 时点数一分没动，PointsBefore 就是当时的事实值。</summary>
public sealed record SeasonPassAward(
    bool Ok, string Reason, string SourceId, string SeasonId, string PeriodKey,
    int Points, long PointsBefore, long PointsAfter, bool Duplicate);

/// <summary>通行证进度的一行流水：哪个来源、哪个周期键、加了多少点。</summary>
public sealed record SeasonPassPointRow(
    string TxnId, string SeasonId, string SourceId, string Kind, string PeriodKey,
    string RefId, int Points);

/// <summary>一个来源在本季已经给了几次 / 上限几次（面板与核对用）。</summary>
public sealed record SeasonPassSourceCount(string SourceId, int Done, int MaxPerPeriod);

/// <summary>只读进度：这一季的点数、阶门槛、每个来源用了几次。</summary>
public sealed record SeasonPassPointState(
    string SeasonId, long Points, int PointsPerStage,
    IReadOnlyList<SeasonPassSourceCount> Counts);

/// <summary>
/// 通行证点数的唯一写入点（Q6/J4）。点数不是货币：它不进 "角色".currency，也不走
/// grant_txn 的发放入口；幂等只认 season_pass_point_ledger 的 txn_id 主键。
///
/// 一笔加点是一笔事务：锁角色行 → 抢流水号（同号重放回 duplicate）→ 种 / 锁本季点数行 →
/// 数这一周期已经给了几次 → 到上限就整笔退回 → 写流水 → 点数加上 → 一次提交。
/// 拒绝路径上流水与点数必须是 0 变化。
///
/// 周期键只由 config/calendar.json 算（与 Q3 / Q4 / Q5 同一套）：
///   daily → 业务日；weekly → 业务周首；once → 固定 once（跨季也只给一次）。
/// daily / weekly 的次数按「本季 + 本周期」数，换季后重新能拿；once 按角色数，换季不重置。
/// </summary>
public sealed class SeasonPassProgressRepository(
    NpgsqlDataSource dataSource, SeasonPassProgressTable table, CalendarTable calendar)
{
    public const int MaxTxnLength = 64;

    public int PointsPerStage => table.PointsPerStage;

    public int RequiredPoints(int stage) => table.RequiredPoints(stage);

    /// <summary>阶段门槛：够不够领这一阶。只读，真正的拒绝在领取那笔事务里。</summary>
    public bool Unlocked(long points, int stage) => points >= RequiredPoints(stage);

    public async Task<SeasonPassAward> AwardAsync(
        long characterId, string seasonId, string sourceId, string refId, string txnId,
        DateTimeOffset now, CancellationToken stopping)
    {
        var season = (seasonId ?? "").Trim();
        var sourceKey = (sourceId ?? "").Trim();
        var reference = (refId ?? "").Trim();
        var txn = (txnId ?? "").Trim();

        if (season.Length == 0)
        {
            return Rejected("season_unavailable", sourceKey, season, 0);
        }

        if (txn.Length == 0 || txn.Length > MaxTxnLength)
        {
            return Rejected("txn_missing", sourceKey, season, 0);
        }

        var source = table.Find(sourceKey);
        if (source is null)
        {
            return Rejected("source_unknown", sourceKey, season, 0);
        }

        if (!RefMatches(source, reference))
        {
            return Rejected("ref_invalid", sourceKey, season, 0);
        }

        var periodKey = PeriodKey(source, now);

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var tx = await connection.BeginTransactionAsync(stopping);

        try
        {
            await LockCharacterAsync(connection, tx, characterId, stopping);

            if (await LedgerExistsAsync(connection, tx, txn, stopping))
            {
                var unchanged = await PointsAsync(connection, tx, characterId, season, stopping);
                await tx.RollbackAsync(stopping);
                return new SeasonPassAward(
                    false, "duplicate", source.Id, season, periodKey, source.Points,
                    unchanged, unchanged, true);
            }

            var before = await SeedAndLockPointsAsync(connection, tx, characterId, season, stopping);

            var done = await CountAsync(
                connection, tx, characterId, season, source, periodKey, stopping);
            if (done >= source.MaxPerPeriod)
            {
                throw new SeasonPassException("limit_reached");
            }

            await using (var insert = new NpgsqlCommand(
                "insert into \"角色\".season_pass_point_ledger "
                + "(txn_id, character_id, season_id, source_id, kind, period_key, ref_id, points) "
                + "values ($1, $2, $3, $4, $5, $6, $7, $8)",
                connection, tx))
            {
                insert.Parameters.AddWithValue(txn);
                insert.Parameters.AddWithValue(characterId);
                insert.Parameters.AddWithValue(season);
                insert.Parameters.AddWithValue(source.Id);
                insert.Parameters.AddWithValue(source.Kind);
                insert.Parameters.AddWithValue(periodKey);
                insert.Parameters.AddWithValue(reference);
                insert.Parameters.AddWithValue(source.Points);
                await insert.ExecuteNonQueryAsync(stopping);
            }

            var after = before + source.Points;
            await using (var update = new NpgsqlCommand(
                "update \"角色\".season_pass_points set points = $3, updated_at = now() "
                + "where character_id = $1 and season_id = $2",
                connection, tx))
            {
                update.Parameters.AddWithValue(characterId);
                update.Parameters.AddWithValue(season);
                update.Parameters.AddWithValue(after);
                await update.ExecuteNonQueryAsync(stopping);
            }

            await tx.CommitAsync(stopping);

            return new SeasonPassAward(
                true, "", source.Id, season, periodKey, source.Points, before, after, false);
        }
        catch (SeasonPassException ex)
        {
            await tx.RollbackAsync(stopping);
            var current = await PointsAsync(connection, null, characterId, season, stopping);
            return new SeasonPassAward(
                false, ex.Code, source.Id, season, periodKey, source.Points,
                current, current, false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 两笔同一个事务号同时进来：主键挡住的那一笔按 duplicate 回，点数不加。
            await tx.RollbackAsync(stopping);
            var current = await PointsAsync(connection, null, characterId, season, stopping);
            return new SeasonPassAward(
                false, "duplicate", source.Id, season, periodKey, source.Points,
                current, current, true);
        }
    }

    /// <summary>只读面板：本季点数、每阶门槛、每个来源在本周期用了几次。</summary>
    public async Task<SeasonPassPointState> StateAsync(
        long characterId, string seasonId, DateTimeOffset now, CancellationToken stopping)
    {
        var season = (seasonId ?? "").Trim();
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        var points = season.Length == 0
            ? 0
            : await PointsAsync(connection, null, characterId, season, stopping);

        var counts = new List<SeasonPassSourceCount>(table.Sources.Count);
        if (season.Length > 0)
        {
            foreach (var source in table.Sources)
            {
                var key = PeriodKey(source, now);
                var done = await CountAsync(
                    connection, null, characterId, season, source, key, stopping);
                counts.Add(new SeasonPassSourceCount(source.Id, done, source.MaxPerPeriod));
            }
        }

        return new SeasonPassPointState(season, points, table.PointsPerStage, counts);
    }

    /// <summary>本季点数。领取与面板都读这一个事实值，不各自算一遍。</summary>
    public async Task<long> PointsAsync(
        long characterId, string seasonId, CancellationToken stopping)
    {
        var season = (seasonId ?? "").Trim();
        if (season.Length == 0)
        {
            return 0;
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        return await PointsAsync(connection, null, characterId, season, stopping);
    }

    /// <summary>流水：这个角色最近的加点记录，新在前。</summary>
    public async Task<IReadOnlyList<SeasonPassPointRow>> LedgerAsync(
        long characterId, string seasonId, int limit, CancellationToken stopping)
    {
        var rows = new List<SeasonPassPointRow>();
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var read = new NpgsqlCommand(
            "select txn_id, season_id, source_id, kind, period_key, ref_id, points "
            + "from \"角色\".season_pass_point_ledger where character_id = $1 "
            + "and ($2 = '' or season_id = $2) order by awarded_at desc limit $3",
            connection);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue((seasonId ?? "").Trim());
        read.Parameters.AddWithValue(limit);
        await using var reader = await read.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            rows.Add(new SeasonPassPointRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt32(6)));
        }

        return rows;
    }

    /// <summary>这一笔在这个时点算哪个周期键。只由日历表算；产品代码没有第二套日 / 周函数。</summary>
    public string PeriodKey(SeasonPassSourceDef source, DateTimeOffset now) =>
        PeriodKeyOf(source, calendar, now);

    /// <summary>只读入口也用这一份：周期键不能有第二个实现。</summary>
    public static string PeriodKeyOf(
        SeasonPassSourceDef source, CalendarTable calendar, DateTimeOffset now) =>
        source.Period switch
        {
            "daily" => calendar.DayKey(now).ToString("yyyy-MM-dd"),
            "weekly" => calendar.WeekStartDate(now).ToString("yyyy-MM-dd"),
            _ => "once",
        };

    // ------------------------------------------------------------------ 内部

    static SeasonPassAward Rejected(string reason, string source, string season, int points) =>
        new(false, reason, source, season, "", points, 0, 0, false);

    static bool RefMatches(SeasonPassSourceDef source, string reference)
    {
        if (string.Equals(source.Kind, SeasonPassProgressTable.DungeonClear, StringComparison.Ordinal))
        {
            // 副本来源只能由那个副本的通关来加点，别的副本编号带不进来。
            return string.Equals(source.DungeonId, reference, StringComparison.Ordinal);
        }

        // 主线来源：ref 是刚推进到的场次，必须是正整数。
        return int.TryParse(reference, out var stage) && stage >= 1;
    }

    static async Task LockCharacterAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select id from \"角色\".character where id = $1 for update",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        if (await read.ExecuteScalarAsync(stopping) is null)
        {
            throw new SeasonPassException("character_not_found");
        }
    }

    static async Task<bool> LedgerExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string txnId,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select 1 from \"角色\".season_pass_point_ledger where txn_id = $1",
            connection, transaction);
        read.Parameters.AddWithValue(txnId);
        return await read.ExecuteScalarAsync(stopping) is not null;
    }

    /// <summary>种一行本季点数再锁住它：同一角色的两笔加点在这里排队，不会各自 +1 丢掉一笔。</summary>
    static async Task<long> SeedAndLockPointsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        string seasonId, CancellationToken stopping)
    {
        await using (var seed = new NpgsqlCommand(
            "insert into \"角色\".season_pass_points (character_id, season_id, points) "
            + "values ($1, $2, 0) on conflict (character_id, season_id) do nothing",
            connection, transaction))
        {
            seed.Parameters.AddWithValue(characterId);
            seed.Parameters.AddWithValue(seasonId);
            await seed.ExecuteNonQueryAsync(stopping);
        }

        await using var read = new NpgsqlCommand(
            "select points from \"角色\".season_pass_points "
            + "where character_id = $1 and season_id = $2 for update",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(seasonId);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>
    /// 这一周期已经给了几次。daily / weekly 按「本季 + 本周期」数（换季重新能拿）；
    /// once 按角色数（跨季也只给一次，换季不重置）。
    /// </summary>
    static async Task<int> CountAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long characterId,
        string seasonId, SeasonPassSourceDef source, string periodKey,
        CancellationToken stopping)
    {
        var once = string.Equals(source.Period, "once", StringComparison.Ordinal);
        var sql = once
            ? "select count(*) from \"角色\".season_pass_point_ledger "
              + "where character_id = $1 and source_id = $2"
            : "select count(*) from \"角色\".season_pass_point_ledger "
              + "where character_id = $1 and source_id = $2 and season_id = $3 "
              + "and period_key = $4";
        await using var read = new NpgsqlCommand(sql, connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(source.Id);
        if (!once)
        {
            read.Parameters.AddWithValue(seasonId);
            read.Parameters.AddWithValue(periodKey);
        }

        var value = await read.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    static async Task<long> PointsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long characterId,
        string seasonId, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select points from \"角色\".season_pass_points "
            + "where character_id = $1 and season_id = $2",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(seasonId);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }
}
