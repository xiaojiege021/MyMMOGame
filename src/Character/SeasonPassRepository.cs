using Npgsql;

namespace Character;

/// <summary>买下通行证的结果。Ok=false 时 Reason 给拒绝码，未生效的数值一律留 0。</summary>
public sealed record SeasonPassPurchase(
    bool Ok, string Reason, string SeasonId, bool Paid, long Price, long Balance, bool Duplicate);

/// <summary>领一阶的结果。物品、数量、格子都是这一笔之后的事实值。</summary>
public sealed record SeasonPassClaimResult(
    bool Ok, string Reason, string SeasonId, string Track, int Stage,
    string ItemId, int Count, int Slot, bool Duplicate, bool Claimed);

/// <summary>通行证的只读状态：付费轨开没开、哪几阶领过、背包里现在有什么。</summary>
public sealed record SeasonPassState(
    string SeasonId, bool Paid, IReadOnlyList<SeasonPassClaimRow> Claims);

public sealed record SeasonPassClaimRow(string Track, int Stage, string ItemId, int Count);

/// <summary>通行证被业务规则挡下。Code 直接回给调用方，转成 ok=false + reason。</summary>
public sealed class SeasonPassException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// 赛季通行证的唯一写入点（方案 12.1、开发架构 11 节 P3-4 / 13 节 P3-4a2 / 第 68 行）。
/// 两笔事务，都在角色服：
///
///   · 买：锁角色行 → 先抢事务号 → 本季已经买过就 already_owned（不重复扣）→
///     带余额条件扣元宝 → 写 season_pass 一行（付费轨开了）→ 事务内读回余额 → 提交。
///     通行证在商城按标价卖，价格只从 shop.json 读；买下不往背包放东西。
///   · 领：锁角色行 → 先抢事务号 → 付费轨要真的有 season_pass 那一行 →
///     **本季点数够不够这一阶的门槛（阶号 × points_per_stage，Q6/J4）** → 这一阶没领过 →
///     挑格子入包（数量、堆叠、绑定都按物品表）→ 写 season_pass_claim → 提交。
///
/// 奖励物品、数量、阶号全从 SeasonPassTable 读，请求里只有「哪一轨、第几阶」。
/// 季由调用方从全局服务取来后传进来，这里不猜、也不从配置自己认定。
/// </summary>
public sealed class SeasonPassRepository(
    NpgsqlDataSource dataSource,
    SeasonPassTable table,
    ItemTable items,
    CharacterConfig config,
    SeasonPassProgressTable progress)
{
    public const int MaxTxnLength = 64;

    // ------------------------------------------------------------------ 买

    public async Task<SeasonPassPurchase> BuyAsync(
        long characterId, string seasonId, string txnId, CancellationToken stopping)
    {
        var season = (seasonId ?? "").Trim();
        var txn = (txnId ?? "").Trim();
        var price = table.PriceYuanbao;

        if (season.Length == 0)
        {
            return new SeasonPassPurchase(false, "season_unavailable", "", false, price, 0, false);
        }

        if (txn.Length == 0 || txn.Length > MaxTxnLength)
        {
            return new SeasonPassPurchase(false, "txn_missing", season, false, price, 0, false);
        }

        if (table.Find(season) is null)
        {
            // 配置里没有这一季的通行证：拒绝，不按别的季凑一份。
            return new SeasonPassPurchase(false, "unknown_season", season, false, price, 0, false);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        try
        {
            await LockCharacterAsync(connection, transaction, characterId, stopping);

            // 先抢事务号：同一个号重放整笔不做，绝不能走到「已买过」被误判成 already_owned。
            if (!await ClaimTxnAsync(connection, transaction, characterId, txn, stopping))
            {
                await transaction.RollbackAsync(stopping);
                // 回滚之后不能再拿这个事务去读（P3-1a2 踩坑①）：余额从同一连接重读。
                var after = await BalanceAsync(connection, null, characterId, stopping);
                return new SeasonPassPurchase(false, "duplicate", season, false, price, after, true);
            }

            if (await OwnsPassAsync(connection, transaction, characterId, season, stopping))
            {
                throw new SeasonPassException("already_owned");
            }

            if (!await DebitYuanbaoAsync(connection, transaction, characterId, price, stopping))
            {
                throw new SeasonPassException("not_enough_yuanbao");
            }

            await using (var insert = new NpgsqlCommand(
                "insert into \"角色\".season_pass "
                + "(character_id, season_id, paid, price_yuanbao, txn_id) "
                + "values ($1, $2, true, $3, $4)",
                connection, transaction))
            {
                insert.Parameters.AddWithValue(characterId);
                insert.Parameters.AddWithValue(season);
                insert.Parameters.AddWithValue(price);
                insert.Parameters.AddWithValue(txn);
                await insert.ExecuteNonQueryAsync(stopping);
            }

            var balance = await BalanceAsync(connection, transaction, characterId, stopping);
            await transaction.CommitAsync(stopping);

            return new SeasonPassPurchase(true, "", season, true, price, balance, false);
        }
        catch (SeasonPassException ex)
        {
            await transaction.RollbackAsync(stopping);
            var after = await BalanceAsync(connection, null, characterId, stopping);
            return new SeasonPassPurchase(false, ex.Code, season, false, price, after, false);
        }
    }

    // ------------------------------------------------------------------ 领

    public async Task<SeasonPassClaimResult> ClaimAsync(
        long characterId, string seasonId, string track, int stage, string txnId,
        CancellationToken stopping)
    {
        var season = (seasonId ?? "").Trim();
        var rail = (track ?? "").Trim();
        var txn = (txnId ?? "").Trim();

        if (season.Length == 0)
        {
            return Rejected("season_unavailable", season, rail, stage);
        }

        if (txn.Length == 0 || txn.Length > MaxTxnLength)
        {
            return Rejected("txn_missing", season, rail, stage);
        }

        if (!SeasonPassTable.IsKnownTrack(rail))
        {
            return Rejected("unknown_track", season, rail, stage);
        }

        var pass = table.Find(season);
        if (pass is null)
        {
            return Rejected("unknown_season", season, rail, stage);
        }

        // 阶号与这一阶领什么全从配置读；表外的阶一律拒绝。
        var stages = pass.Track(rail);
        SeasonPassRewardDef? reward = null;
        foreach (var candidate in stages)
        {
            if (candidate.Stage == stage)
            {
                reward = candidate.Rewards[0];
                break;
            }
        }

        if (reward is null)
        {
            return Rejected("unknown_stage", season, rail, stage);
        }

        var goods = items.Find(reward.ItemId)
            ?? throw new InvalidOperationException("通行证配置校验漏了物品：" + reward.ItemId);

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var tx = await connection.BeginTransactionAsync(stopping);

        try
        {
            await LockCharacterAsync(connection, tx, characterId, stopping);

            if (!await ClaimTxnAsync(connection, tx, characterId, txn, stopping))
            {
                await tx.RollbackAsync(stopping);
                return new SeasonPassClaimResult(
                    false, "duplicate", season, rail, stage,
                    reward.ItemId, reward.Count, 0, true, true);
            }

            if (string.Equals(rail, SeasonPassTable.PaidTrack, StringComparison.Ordinal)
                && !await OwnsPassAsync(connection, tx, characterId, season, stopping))
            {
                // 付费轨要先买过这一季的通行证；免费轨不看这一行。
                throw new SeasonPassException("pass_not_owned");
            }

            // Q6/J4：阶门槛只认服务端记下的本季点数。点数不够就拒，绝不「先领后补」；
            // 点数是只增不减的事实值，读它不需要锁本季点数行。
            if (progress.IsMissing)
            {
                throw new SeasonPassException("progress_table_missing");
            }

            var points = await SeasonPassPointsAsync(connection, tx, characterId, season, stopping);
            if (points < progress.RequiredPoints(stage))
            {
                throw new SeasonPassException("points_not_enough");
            }

            if (await ClaimedAsync(connection, tx, characterId, season, rail, stage, stopping))
            {
                throw new SeasonPassException("already_claimed");
            }

            var slot = await AllocateSlotAsync(connection, tx, characterId, goods, reward.Count, stopping);
            if (slot == 0)
            {
                throw new SeasonPassException("bag_full");
            }

            await using (var insert = new NpgsqlCommand(
                "insert into \"角色\".season_pass_claim "
                + "(character_id, season_id, track, stage, item_id, item_count, txn_id) "
                + "values ($1, $2, $3, $4, $5, $6, $7)",
                connection, tx))
            {
                insert.Parameters.AddWithValue(characterId);
                insert.Parameters.AddWithValue(season);
                insert.Parameters.AddWithValue(rail);
                insert.Parameters.AddWithValue(stage);
                insert.Parameters.AddWithValue(reward.ItemId);
                insert.Parameters.AddWithValue(reward.Count);
                insert.Parameters.AddWithValue(txn);
                await insert.ExecuteNonQueryAsync(stopping);
            }

            await tx.CommitAsync(stopping);

            return new SeasonPassClaimResult(
                true, "", season, rail, stage, reward.ItemId, reward.Count, slot, false, true);
        }
        catch (SeasonPassException ex)
        {
            await tx.RollbackAsync(stopping);
            return Rejected(ex.Code, season, rail, stage);
        }
    }

    // ------------------------------------------------------------------ 只读

    public async Task<SeasonPassState> StateAsync(
        long characterId, string seasonId, CancellationToken stopping)
    {
        var season = (seasonId ?? "").Trim();
        await using var connection = await dataSource.OpenConnectionAsync(stopping);

        var paid = false;
        if (season.Length > 0)
        {
            await using (var read = new NpgsqlCommand(
                "select paid from \"角色\".season_pass where character_id = $1 and season_id = $2",
                connection))
            {
                read.Parameters.AddWithValue(characterId);
                read.Parameters.AddWithValue(season);
                var value = await read.ExecuteScalarAsync(stopping);
                paid = value is true;
            }
        }

        var claims = new List<SeasonPassClaimRow>();
        await using (var read = new NpgsqlCommand(
            "select season_id, track, stage, item_id, item_count "
            + "from \"角色\".season_pass_claim where character_id = $1 order by season_id, track, stage",
            connection))
        {
            read.Parameters.AddWithValue(characterId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                if (!string.Equals(reader.GetString(0), season, StringComparison.Ordinal))
                {
                    continue;
                }

                claims.Add(new SeasonPassClaimRow(
                    reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4)));
            }
        }

        return new SeasonPassState(season, paid, claims);
    }

    // ------------------------------------------------------------------ 内部

    static SeasonPassClaimResult Rejected(string reason, string season, string track, int stage) =>
        new(false, reason, season, track, stage, "", 0, 0, false, false);

    static async Task LockCharacterAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select id from \"角色\".character where id = $1 for update", connection, transaction);
        read.Parameters.AddWithValue(characterId);
        var found = await read.ExecuteScalarAsync(stopping);
        if (found is null)
        {
            throw new SeasonPassException("character_not_found");
        }
    }

    /// <summary>抢本笔的事务号。冲突说明这个号用过了，调用方按 duplicate 处理。</summary>
    static async Task<bool> ClaimTxnAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        string txnId, CancellationToken stopping)
    {
        await using var claim = new NpgsqlCommand(
            "insert into \"角色\".grant_txn (txn_id, character_id) values ($1, $2) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction);
        claim.Parameters.AddWithValue(txnId);
        claim.Parameters.AddWithValue(characterId);
        return await claim.ExecuteScalarAsync(stopping) is not null;
    }

    static async Task<bool> OwnsPassAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        string seasonId, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select paid from \"角色\".season_pass where character_id = $1 and season_id = $2",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(seasonId);
        return await read.ExecuteScalarAsync(stopping) is true;
    }

    static async Task<bool> ClaimedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        string seasonId, string track, int stage, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select 1 from \"角色\".season_pass_claim "
            + "where character_id = $1 and season_id = $2 and track = $3 and stage = $4",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(seasonId);
        read.Parameters.AddWithValue(track);
        read.Parameters.AddWithValue(stage);
        return await read.ExecuteScalarAsync(stopping) is not null;
    }

    /// <summary>本季点了多少点。Q6/J4 的阶门槛只读这一个事实值。</summary>
    static async Task<long> SeasonPassPointsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
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

    /// <summary>带余额条件扣元宝。扣不动说明不够，整笔回滚。</summary>
    static async Task<bool> DebitYuanbaoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        long price, CancellationToken stopping)
    {
        await using var debit = new NpgsqlCommand(
            "update \"角色\".currency set amount = amount - $2, updated_at = now() "
            + "where character_id = $1 and kind = $3 and amount >= $2 returning amount",
            connection, transaction);
        debit.Parameters.AddWithValue(characterId);
        debit.Parameters.AddWithValue(price);
        debit.Parameters.AddWithValue(ShopTable.WrittenCurrency);
        return await debit.ExecuteScalarAsync(stopping) is not null;
    }

    static async Task<long> BalanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long characterId,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select amount from \"角色\".currency where character_id = $1 and kind = $2",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(ShopTable.WrittenCurrency);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>
    /// 在事务里挑一格放入这一件：先叠到同物品且没满的格，再找空格。
    /// 找不到回 0（调用方按 bag_full 拒绝）。绑定按物品表的规则落。
    /// </summary>
    async Task<int> AllocateSlotAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        ItemDef goods, int count, CancellationToken stopping)
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
                    && reader.GetInt32(2) + count <= goods.StackMax)
                {
                    mergeSlot = slot;
                }
            }
        }

        if (mergeSlot != 0)
        {
            await using var update = new NpgsqlCommand(
                "update \"角色\".inventory set count = count + $3 "
                + "where character_id = $1 and slot = $2",
                connection, transaction);
            update.Parameters.AddWithValue(characterId);
            update.Parameters.AddWithValue((short)mergeSlot);
            update.Parameters.AddWithValue(count);
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
            + "values ($1, $2, $3, $4, $5, case when $5 then now() else null end)",
            connection, transaction))
        {
            insert.Parameters.AddWithValue(characterId);
            insert.Parameters.AddWithValue((short)free);
            insert.Parameters.AddWithValue(goods.Id);
            insert.Parameters.AddWithValue(count);
            insert.Parameters.AddWithValue(bound);
            await insert.ExecuteNonQueryAsync(stopping);
        }

        return free;
    }
}
