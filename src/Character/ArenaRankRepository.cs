using Npgsql;

namespace Character;

/// <summary>一场胜仗的加分行结果。Ok=false 时 Reason 给拒绝码，数值一律留 0。</summary>
public sealed record ArenaRankAward(
    bool Ok, string Reason, string SeasonId, long Score, long ScoreDelta,
    string TierKey, string TierName, bool TierUp, int Wins, bool Duplicate);

/// <summary>这一季这一档在这份只读视图里的事实：到没到、领没领。</summary>
public sealed record ArenaRankTierState(
    string Key, string Name, long MinScore, string ItemId, bool Reached, bool Claimed);

/// <summary>段位只读状态。没打过竞技场时是 0 分、落在首档。</summary>
public sealed record ArenaRankState(
    string SeasonId, long Score, string TierKey, string TierName, int Wins,
    string NextTierKey, long NextTierMinScore, IReadOnlyList<ArenaRankTierState> Tiers);

/// <summary>排行一行：名次、角色号、名字、职业、分数。档位名由配置现取，不入库。</summary>
public sealed record ArenaLeaderRow(
    uint Rank, long CharacterId, string Name, string ClassId, long Score);

/// <summary>领这一档专属外观的结果。Slot 是这一件落进背包的格号。</summary>
public sealed record ArenaAppearanceClaim(
    bool Ok, string Reason, string SeasonId, string TierKey, string ItemId, int Slot, bool Duplicate);

/// <summary>段位被业务规则挡下。Code 直接转成 ok=false + reason。</summary>
public sealed class ArenaRankException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// B11 竞技场赛季段位的唯一写入点。两笔事务，都在角色服：
///
///   · 记分：锁角色行 → 读本季分数与胜场 → 新分数 = 旧分数 + win_points（只增不减）
///     → 先插 arena_rank_ledger 抢幂等号（冲突就是 duplicate，不加第二次）
///     → upsert arena_rank（分数、档位、胜场）→ 提交。加多少分与落到哪一档
///     全从 config/arena_rank.json 现取，请求里带不进分数、档位与季。
///   · 领外观：锁角色行 → 抢事务号 → 这一档分数到了没有 → 本季这一档领过没有
///     → 挑格子入包（绑定按物品表）→ 写 arena_rank_appearance → 提交。
///
/// 季由调用方从全局服务取来后传进来，这里不猜、也不从配置自己认定。
/// 段位不改竞技场模板属性：本类一个字都不碰 config/arena_match.json。
/// </summary>
public sealed class ArenaRankRepository(
    NpgsqlDataSource dataSource, ArenaRankTable table, ItemTable items, CharacterConfig config)
{
    // ------------------------------------------------------------------ 记分

    public async Task<ArenaRankAward> ReportAsync(
        long characterId, string seasonId, string txnId, string mode, string outcome,
        CancellationToken stopping)
    {
        var season = (seasonId ?? "").Trim();
        var txn = (txnId ?? "").Trim();
        var modeId = (mode ?? "").Trim();

        if (table.IsMissing)
        {
            return Rejected("rank_table_missing", season);
        }

        // 方案只写了赛季段位，没写失败惩罚：这一版只认赢，散场不记分也不扣分。
        if (!string.Equals((outcome ?? "").Trim(), "win", StringComparison.Ordinal))
        {
            return Rejected("outcome_unsupported", season);
        }

        if (season.Length == 0)
        {
            return Rejected("season_unavailable", season);
        }

        if (txn.Length == 0 || txn.Length > GrantRepository.MaxTxnLength)
        {
            return Rejected("txn_missing", season);
        }

        if (modeId.Length == 0 || modeId.Length > 32)
        {
            return Rejected("mode_missing", season);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var tx = await connection.BeginTransactionAsync(stopping);

        try
        {
            await LockCharacterAsync(connection, tx, characterId, stopping);

            var (oldScore, oldTier, wins) =
                await ReadRankAsync(connection, tx, characterId, season, stopping);

            var score = oldScore + table.WinPoints;
            var tier = table.TierFor(score);
            if (tier is null)
            {
                // 首档 min_score 必须是 0，加载期已经保证；走到这里说明表被改坏了。
                throw new ArenaRankException("tier_missing");
            }

            // 幂等：同一个号只加一次。先插流水，冲突就整笔按 duplicate 回。
            await using (var ledger = new NpgsqlCommand(
                "insert into \"角色\".arena_rank_ledger "
                + "(txn_id, character_id, season_id, mode, score_delta, score_after, tier_key) "
                + "values ($1, $2, $3, $4, $5, $6, $7) on conflict (txn_id) do nothing "
                + "returning txn_id",
                connection, tx))
            {
                ledger.Parameters.AddWithValue(txn);
                ledger.Parameters.AddWithValue(characterId);
                ledger.Parameters.AddWithValue(season);
                ledger.Parameters.AddWithValue(modeId);
                ledger.Parameters.AddWithValue((int)table.WinPoints);
                ledger.Parameters.AddWithValue(score);
                ledger.Parameters.AddWithValue(tier.Key);
                if (await ledger.ExecuteScalarAsync(stopping) is null)
                {
                    await tx.RollbackAsync(stopping);
                    return new ArenaRankAward(
                        false, "duplicate", season, oldScore, 0,
                        oldTier, TierName(oldTier), false, wins, true);
                }
            }

            await using (var upsert = new NpgsqlCommand(
                "insert into \"角色\".arena_rank "
                + "(character_id, season_id, score, tier_key, wins, updated_at) "
                + "values ($1, $2, $3, $4, 1, now()) "
                + "on conflict (character_id, season_id) do update "
                + "set score = $3, tier_key = $4, wins = arena_rank.wins + 1, updated_at = now()",
                connection, tx))
            {
                upsert.Parameters.AddWithValue(characterId);
                upsert.Parameters.AddWithValue(season);
                upsert.Parameters.AddWithValue(score);
                upsert.Parameters.AddWithValue(tier.Key);
                await upsert.ExecuteNonQueryAsync(stopping);
            }

            await tx.CommitAsync(stopping);

            return new ArenaRankAward(
                true, "", season, score, table.WinPoints, tier.Key, tier.Name,
                !string.Equals(oldTier, tier.Key, StringComparison.Ordinal), wins + 1, false);
        }
        catch (ArenaRankException ex)
        {
            await tx.RollbackAsync(stopping);
            return Rejected(ex.Code, season);
        }
    }

    // ------------------------------------------------------------------ 只读

    public async Task<ArenaRankState> StateAsync(
        long characterId, string seasonId, CancellationToken stopping)
    {
        var season = (seasonId ?? "").Trim();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        long score = 0;
        var wins = 0;

        await using (var connection = await dataSource.OpenConnectionAsync(stopping))
        {
            if (season.Length > 0)
            {
                await using (var read = new NpgsqlCommand(
                    "select score, wins from \"角色\".arena_rank "
                    + "where character_id = $1 and season_id = $2",
                    connection))
                {
                    read.Parameters.AddWithValue(characterId);
                    read.Parameters.AddWithValue(season);
                    await using var reader = await read.ExecuteReaderAsync(stopping);
                    if (await reader.ReadAsync(stopping))
                    {
                        score = reader.GetInt64(0);
                        wins = reader.GetInt32(1);
                    }
                }

                await using (var read = new NpgsqlCommand(
                    "select tier_key from \"角色\".arena_rank_appearance "
                    + "where character_id = $1 and season_id = $2",
                    connection))
                {
                    read.Parameters.AddWithValue(characterId);
                    read.Parameters.AddWithValue(season);
                    await using var reader = await read.ExecuteReaderAsync(stopping);
                    while (await reader.ReadAsync(stopping))
                    {
                        claimed.Add(reader.GetString(0));
                    }
                }
            }
        }

        return StateOf(season, score, wins, claimed);
    }

    public async Task<(string SeasonId, IReadOnlyList<ArenaLeaderRow> Rows)> LeaderboardAsync(
        string seasonId, int limit, CancellationToken stopping)
    {
        var season = (seasonId ?? "").Trim();
        var rows = new List<ArenaLeaderRow>();
        if (season.Length == 0 || table.IsMissing)
        {
            return (season, rows);
        }

        var n = limit <= 0 || limit > table.TopN ? table.TopN : limit;

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var read = new NpgsqlCommand(
            "select r.character_id, c.name, c.class_id, r.score "
            + "from \"角色\".arena_rank r "
            + "join \"角色\".character c on c.id = r.character_id "
            + "where r.season_id = $1 "
            + "order by r.score desc, r.character_id asc limit $2",
            connection);
        read.Parameters.AddWithValue(season);
        read.Parameters.AddWithValue(n);
        await using var reader = await read.ExecuteReaderAsync(stopping);
        uint rank = 0;
        while (await reader.ReadAsync(stopping))
        {
            rank++;
            rows.Add(new ArenaLeaderRow(
                rank, reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }

        return (season, rows);
    }

    // ------------------------------------------------------------------ 领外观

    public async Task<ArenaAppearanceClaim> ClaimAsync(
        long characterId, string seasonId, string tierKey, string txnId,
        CancellationToken stopping)
    {
        var season = (seasonId ?? "").Trim();
        var txn = (txnId ?? "").Trim();
        var key = (tierKey ?? "").Trim();

        if (table.IsMissing)
        {
            return ClaimRejected("rank_table_missing", season, key);
        }

        if (season.Length == 0)
        {
            return ClaimRejected("season_unavailable", season, key);
        }

        var tier = table.Find(key);
        if (tier is null)
        {
            return ClaimRejected("unknown_tier", season, key);
        }

        if (txn.Length == 0 || txn.Length > GrantRepository.MaxTxnLength)
        {
            return ClaimRejected("txn_missing", season, key);
        }

        var goods = items.Find(tier.AppearanceItemId)
            ?? throw new InvalidOperationException("段位配置校验漏了物品：" + tier.AppearanceItemId);

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var tx = await connection.BeginTransactionAsync(stopping);

        try
        {
            await LockCharacterAsync(connection, tx, characterId, stopping);

            // 先抢号：同一个号重放回 duplicate，不再入包。
            await using (var claim = new NpgsqlCommand(
                "insert into \"角色\".grant_txn (txn_id, character_id) values ($1, $2) "
                + "on conflict (txn_id) do nothing returning txn_id",
                connection, tx))
            {
                claim.Parameters.AddWithValue(txn);
                claim.Parameters.AddWithValue(characterId);
                if (await claim.ExecuteScalarAsync(stopping) is null)
                {
                    await tx.RollbackAsync(stopping);
                    return new ArenaAppearanceClaim(false, "duplicate", season, key, "", 0, true);
                }
            }

            var (score, _, _) = await ReadRankAsync(connection, tx, characterId, season, stopping);
            if (score < tier.MinScore)
            {
                throw new ArenaRankException("tier_not_reached");
            }

            await using (var read = new NpgsqlCommand(
                "select 1 from \"角色\".arena_rank_appearance "
                + "where character_id = $1 and season_id = $2 and tier_key = $3",
                connection, tx))
            {
                read.Parameters.AddWithValue(characterId);
                read.Parameters.AddWithValue(season);
                read.Parameters.AddWithValue(key);
                if (await read.ExecuteScalarAsync(stopping) is not null)
                {
                    throw new ArenaRankException("already_claimed");
                }
            }

            var slot = await AllocateSlotAsync(connection, tx, characterId, goods, stopping);
            if (slot == 0)
            {
                throw new ArenaRankException("bag_full");
            }

            await using (var insert = new NpgsqlCommand(
                "insert into \"角色\".arena_rank_appearance "
                + "(character_id, season_id, tier_key, item_id, txn_id) values ($1, $2, $3, $4, $5)",
                connection, tx))
            {
                insert.Parameters.AddWithValue(characterId);
                insert.Parameters.AddWithValue(season);
                insert.Parameters.AddWithValue(key);
                insert.Parameters.AddWithValue(tier.AppearanceItemId);
                insert.Parameters.AddWithValue(txn);
                await insert.ExecuteNonQueryAsync(stopping);
            }

            await tx.CommitAsync(stopping);
            return new ArenaAppearanceClaim(
                true, "", season, key, tier.AppearanceItemId, slot, false);
        }
        catch (ArenaRankException ex)
        {
            await tx.RollbackAsync(stopping);
            return ClaimRejected(ex.Code, season, key);
        }
    }

    // ------------------------------------------------------------------ 内部

    ArenaRankAward Rejected(string reason, string season) =>
        new(false, reason, season, 0, 0, "", "", false, 0, false);

    static ArenaAppearanceClaim ClaimRejected(string reason, string season, string tier) =>
        new(false, reason, season, tier, "", 0, false);

    string TierName(string key) => table.Find(key)?.Name ?? "";

    /// <summary>把「分数 + 已领档位」拼成只读视图；首档不满足时按 0 分落在首档。</summary>
    ArenaRankState StateOf(
        string season, long score, int wins, HashSet<string> claimed)
    {
        var current = table.TierFor(score);
        var currentKey = current?.Key ?? "";
        var next = current is null ? null : table.NextAfter(current.Key);

        var tiers = new List<ArenaRankTierState>(table.Tiers.Count);
        foreach (var tier in table.Tiers)
        {
            tiers.Add(new ArenaRankTierState(
                tier.Key, tier.Name, tier.MinScore, tier.AppearanceItemId,
                score >= tier.MinScore, claimed.Contains(tier.Key)));
        }

        return new ArenaRankState(
            season, score, currentKey, current?.Name ?? "", wins,
            next?.Key ?? "", next?.MinScore ?? 0, tiers);
    }

    static async Task LockCharacterAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select id from \"角色\".character where id = $1 for update", connection, transaction);
        read.Parameters.AddWithValue(characterId);
        if (await read.ExecuteScalarAsync(stopping) is null)
        {
            throw new ArenaRankException("character_not_found");
        }
    }

    /// <summary>本季这一行的分数、档位、胜场。没有行就是 0 分、首档。</summary>
    async Task<(long Score, string TierKey, int Wins)> ReadRankAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        string seasonId, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select score, tier_key, wins from \"角色\".arena_rank "
            + "where character_id = $1 and season_id = $2 for update",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(seasonId);
        await using var reader = await read.ExecuteReaderAsync(stopping);
        if (await reader.ReadAsync(stopping))
        {
            return (reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2));
        }

        return (0, table.Tiers.Count > 0 ? table.Tiers[0].Key : "", 0);
    }

    /// <summary>
    /// 在事务里挑一格放入这一件：先叠到同物品且没满的格，再找空格。
    /// 找不到回 0（调用方按 bag_full 拒绝）。绑定按物品表的规则落。
    /// </summary>
    async Task<int> AllocateSlotAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        ItemDef goods, CancellationToken stopping)
    {
        var mergeSlot = 0;
        var used = new HashSet<int>();
        await using (var read = new NpgsqlCommand(
            "select slot, item_id, count from \"角色\".inventory "
            + "where character_id = $1 order by slot for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                var slot = reader.GetInt16(0);
                used.Add(slot);
                if (string.Equals(reader.GetString(1), goods.Id, StringComparison.Ordinal)
                    && mergeSlot == 0
                    && reader.GetInt32(2) + 1 <= goods.StackMax)
                {
                    mergeSlot = slot;
                }
            }
        }

        if (mergeSlot != 0)
        {
            await using var update = new NpgsqlCommand(
                "update \"角色\".inventory set count = count + 1 "
                + "where character_id = $1 and slot = $2",
                connection, transaction);
            update.Parameters.AddWithValue(characterId);
            update.Parameters.AddWithValue((short)mergeSlot);
            await update.ExecuteNonQueryAsync(stopping);
            return mergeSlot;
        }

        var free = 0;
        var bagSlots = await BagExpandRepository.SlotsAsync(
            connection, transaction, characterId, config, stopping);
        for (var slot = 1; slot <= bagSlots; slot++)
        {
            if (!used.Contains(slot))
            {
                free = slot;
                break;
            }
        }

        if (free == 0)
        {
            return 0;
        }

        var bound = goods.BindsOnArrival;
        await using (var insert = new NpgsqlCommand(
            "insert into \"角色\".inventory (character_id, slot, item_id, count, bound, bound_at) "
            + "values ($1, $2, $3, 1, $4, case when $4 then now() else null end)",
            connection, transaction))
        {
            insert.Parameters.AddWithValue(characterId);
            insert.Parameters.AddWithValue((short)free);
            insert.Parameters.AddWithValue(goods.Id);
            insert.Parameters.AddWithValue(bound);
            await insert.ExecuteNonQueryAsync(stopping);
        }

        return free;
    }
}
