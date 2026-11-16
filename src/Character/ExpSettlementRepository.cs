using Npgsql;

namespace Character;

/// <summary>一次经验结算的结果。Duplicate 表示这个事务号已经发过，本次没有再发。</summary>
public sealed record ExpSettlementOutcome(
    bool Found,
    bool Duplicate,
    long BaseExp,
    long PartyBonus,
    long SameSectBonus,
    long ExpGain,
    int LevelBefore,
    int Level,
    long Exp,
    int BoosterPct,
    long BoosterExp,
    long DrillGain,
    long Drill,
    string DrillReason,
    int DropLines = 0,
    string DropReason = "");

/// <summary>
/// 等级经验的唯一写入点。活动、副本、任务、竞技场、战场、城战和世界 Boss
/// 都调同一笔：基础经验 + （同一场至少两人时）一截少量加成，只加经验。
/// P1-5c7：限时经验道具生效时，这一笔再乘配置里的一个倍率；到点后回到原倍率。
/// 倍率只从 config/exp_booster.json 读一个，角色库那一行只有到期时刻、没有倍率列，
/// 所以「同时只生效一个、不叠倍率」是结构上落死的，不是靠一句约定。
///
/// 事务顺序与前面的发奖/进本一致：锁角色行 → 读生效状态 → 抢事务号 →
/// 只写 level 与 exp → 提交。抢不到号（重复提交）整笔回滚；并发两笔不同号
/// 冲同一角色由行锁串行，不会把这一笔算丢。等级到 level_cap 就不再升，
/// 库层 CHECK 仍在。
///
/// account 为空表示服间可信调用（战斗服通关后直连），只按角色编号归属；
/// 网关转发时一定带账号，where 里再核一次，别的账号改不到这个角色。
/// P3-1a2 补正：这一笔同时是历练的唯一生产写入点。开发架构「结算离开」写定
/// 战斗服把等级经验和这场击杀累计的历练值一次交给角色服，所以经验与历练在
/// 同一个事务、同一把行锁、同一个事务号下写：重复结算一起回滚，不会只加一样。
/// 历练数值 = config/dungeons.json 的 drill × 战斗服报来的有资格击杀数，
/// 请求里带不进数值；写没写上的 reason 也随这一笔返回，战斗服只打日志。
/// 这一笔只 set level / exp / drill 与写 exp_settlement、realm_drill 两张流水：
/// 疲劳、周份额、货币、背包、进本记录一概不碰；生效判定只 `select`，
/// 不写 exp_booster 那张表。
/// </summary>
public sealed class ExpSettlementRepository(NpgsqlDataSource dataSource)
{
    public sealed class DropSettlementException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
    public const int MaxTxnLength = 128;
    public const int MaxSourceLength = 32;
    public const int MaxDungeonIdLength = 32;

    /// <summary>N8：config/dungeon_drops.json 的键，不是 config/dungeons.json 里的副本。</summary>
    public const string WorldBossDropDungeonId = "world_boss";

    /// <summary>没有生效道具时的结算倍率：乘 100/100 就是原倍率，不是配置数值。</summary>
    public const int OriginalPct = 100;

    public async Task<ExpSettlementOutcome> SettleAsync(
        long characterId,
        string? account,
        string txnId,
        string source,
        int partySize,
        bool allSameSect,
        ExpTable table,
        ExpBoosterTable boosterTable,
        DungeonDef? dungeon,
        int qualifiedKills,
        RealmTable realms,
        GrantRepository grants,
        ItemTable items,
        CharacterConfig config,
        IReadOnlyList<DropLineDef> drops,
        CancellationToken stopping)
    {
        var baseExp = table.BaseExp;
        var partyBonus = table.PartyBonus(partySize);
        // P2-1c：同宗门这一截独立字段，叠在组队加成之外；幅度由配置决定。
        var sameSectBonus = table.SameSectBonus(partySize, allSameSect);
        // base + 组队 + 同门，整笔再被限时道具倍率作用（P1-5c7）。
        var rawGain = baseExp + partyBonus + sameSectBonus;

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁角色行并核归属。带账号的走网关口径，空账号走服间口径。
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        int levelBefore = 0;
        long expBefore = 0;
        string realm = "";
        long drillBefore = 0;
        var found = false;
        await using (var read = new NpgsqlCommand(
            "select level, exp, realm, drill from \"角色\".character " + owned,
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                read.Parameters.AddWithValue(account);
            }

            // 先把 reader 读完关掉再回滚：连接上还挂着 reader 时发 rollback
            // 会抛异常，那样「别人的角色」会被当成服务端内部错误而不是 404。
            await using (var reader = await read.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    levelBefore = reader.GetInt32(0);
                    expBefore = reader.GetInt64(1);
                    realm = reader.GetString(2);
                    drillBefore = reader.GetInt64(3);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new ExpSettlementOutcome(
                false, false, 0, 0, 0, 0, 0, 0, 0, OriginalPct, 0,
                0, 0, "character_not_found", 0, "");
        }

        // 历练只在「这一场确实在一个有资格的副本里、并且有击杀」时才算。
        // 境界门槛在持有行锁的同一个事务里复核，不另开一次查询：突破和结算
        // 撞在一起时，以这一刻库里的境界为准。数值只有一个来源 dungeons.json。
        long drillGain = 0;
        long drillAfter = drillBefore;
        var drillReason = "";
        if (dungeon is null)
        {
            drillReason = "no_dungeon";
        }
        else if (qualifiedKills <= 0)
        {
            drillReason = "no_kills";
        }
        else
        {
            var gate = realms.CheckDungeon(dungeon.Realm, realm);
            if (!gate.Ok)
            {
                drillReason = gate.Reason;
            }
            else if (dungeon.Drill > 0 && qualifiedKills > long.MaxValue / dungeon.Drill)
            {
                // 不写死「一场最多几只怪」这种玩法数字，只挡算术溢出。
                drillReason = "kills_invalid";
            }
            else
            {
                drillGain = dungeon.Drill * qualifiedKills;
            }
        }

        // 限时经验道具（P1-5c7）：生效判定放在这一笔事务里，用数据库当前时刻，
        // 不用客户端时间，也不让战斗服自己算倍率。库里那一行只存到期时刻，
        // 到期 / 没有行都取原倍率；倍率只从配置读一个，再多张也不会累乘。
        var boosterPct = OriginalPct;
        await using (var boost = new NpgsqlCommand(
            "select (b.expires_at > now()) from \"角色\".exp_booster b "
            + "where b.character_id = $1",
            connection, transaction))
        {
            boost.Parameters.AddWithValue(characterId);
            var active = await boost.ExecuteScalarAsync(stopping);
            if (active is not null && Convert.ToBoolean(active))
            {
                boosterPct = boosterTable.ExpBooster.GainPct;
            }
        }

        // 只乘「结算这一笔」：基础 + 组队加成算完再乘倍率，别的一分不加。
        var gain = rawGain * boosterPct / OriginalPct;
        var boosterExp = gain - rawGain;
        var (level, exp) = table.Apply(levelBefore, expBefore, gain);

        // 2. 抢事务号。同一个号已经发过就整笔不做，返回当时的状态。
        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".exp_settlement "
            + "(txn_id, character_id, source, party_size, base_exp, party_bonus, "
            + " same_sect_bonus, exp_gain, level_before, level_after) "
            + "values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            claim.Parameters.AddWithValue(source);
            claim.Parameters.AddWithValue(partySize);
            claim.Parameters.AddWithValue(baseExp);
            claim.Parameters.AddWithValue(partyBonus);
            claim.Parameters.AddWithValue(sameSectBonus);
            claim.Parameters.AddWithValue(gain);
            claim.Parameters.AddWithValue(levelBefore);
            claim.Parameters.AddWithValue(level);
            var claimed = await claim.ExecuteScalarAsync(stopping);
            if (claimed is null)
            {
                await transaction.RollbackAsync(stopping);
                // 抢不到号整笔回滚：经验与历练都没有写，回包里的历练是库里的当前事实值。
                return new ExpSettlementOutcome(
                    true, true, 0, 0, 0, 0, levelBefore, levelBefore, expBefore,
                    OriginalPct, 0, 0, drillBefore, "duplicate", 0, "");
            }
        }

        var dropLines = 0;
        var dropReason = "";
        if (drops.Count > 0)
        {
            if (!await grants.TryClaimTxnInTransactionAsync(
                    connection, transaction, characterId, txnId, stopping))
            {
                await transaction.RollbackAsync(stopping);
                return new ExpSettlementOutcome(
                    true, true, 0, 0, 0, 0, levelBefore, levelBefore, expBefore,
                    OriginalPct, 0, 0, drillBefore, "duplicate", 0, "grant_duplicate");
            }

            try
            {
                var slots = await grants.ReadSlotsForTransactionAsync(
                    connection, transaction, characterId, stopping);
                var deltas = GrantRepository.PlanItemDeltas(slots, drops, items, config.BagSlots);
                await grants.ApplyItemDeltasInTransactionAsync(
                    connection, transaction, characterId, deltas, stopping);
                dropLines = drops.Count;
            }
            catch (GrantException ex)
            {
                await transaction.RollbackAsync(stopping);
                throw new DropSettlementException(ex.Code);
            }
        }

        // 3. 只写这三列。越界等级写不进去，事务回滚后流水也不会留下。
        await using (var write = new NpgsqlCommand(
            "update \"角色\".character set level = $2, exp = $3 where id = $1",
            connection, transaction))
        {
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue(level);
            write.Parameters.AddWithValue(exp);
            await write.ExecuteNonQueryAsync(stopping);
        }

        // 4. 同一事务里写历练流水与余额。realm_drill 与 exp_settlement 是两张表，
        // 同号不冲突；抢号已经在上面挡住重放，所以这里不会把同一场加两次。
        // 万一另一条写入者抢先用了同一个号，只跳过历练、经验照给，并回报原因，
        // 不让整笔经验结算被别人的流水号拖垮。
        if (drillGain > 0)
        {
            await using (var drillClaim = new NpgsqlCommand(
                "insert into \"角色\".realm_drill "
                + "(txn_id, character_id, dungeon_id, drill) "
                + "values ($1, $2, $3, $4) "
                + "on conflict (txn_id) do nothing returning txn_id",
                connection, transaction))
            {
                drillClaim.Parameters.AddWithValue(txnId);
                drillClaim.Parameters.AddWithValue(characterId);
                drillClaim.Parameters.AddWithValue(dungeon!.Id);
                drillClaim.Parameters.AddWithValue(drillGain);
                if (await drillClaim.ExecuteScalarAsync(stopping) is null)
                {
                    drillGain = 0;
                    drillReason = "drill_txn_conflict";
                }
            }

            if (drillGain > 0)
            {
                await using (var drillWrite = new NpgsqlCommand(
                    "update \"角色\".character set drill = drill + $2 "
                    + "where id = $1 returning drill", connection, transaction))
                {
                    drillWrite.Parameters.AddWithValue(characterId);
                    drillWrite.Parameters.AddWithValue(drillGain);
                    drillAfter = Convert.ToInt64(await drillWrite.ExecuteScalarAsync(stopping));
                }
            }
        }

        await transaction.CommitAsync(stopping);
        return new ExpSettlementOutcome(
            true, false, baseExp, partyBonus, sameSectBonus, gain, levelBefore, level, exp,
            boosterPct, boosterExp, drillGain, drillAfter, drillReason, dropLines, dropReason);
    }

    /// <summary>只给核对用：读等级、当前级经验、结算笔数与累计入账经验。</summary>
    public async Task<(int Level, long Exp, int Rows, long TotalGain)?> ExpStateAsync(
        long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select c.level, c.exp, "
            + "(select count(*) from \"角色\".exp_settlement s where s.character_id = c.id), "
            + "(select coalesce(sum(s.exp_gain), 0) from \"角色\".exp_settlement s where s.character_id = c.id) "
            + "from \"角色\".character c where c.id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3));
    }

    /// <summary>只给核对用：绕过接口直写经验，验库层的非负 CHECK。</summary>
    public async Task RawWriteExpAsync(long characterId, long exp, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set exp = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(exp);
        await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>
    /// P1-5c6：用掉一张复活卷轴。方案 8.3 / 12.1 写定「拉起只扣当前等级已有经验
    /// 的一部分，当前级经验是零时这次不扣，不掉级」「冷却记在当前玩家身上，
    /// 跨场保留」「比例按一转基础，不吃生息二转减免」。
    ///
    /// 等级经验的写入全部集中在这个 Repository：上面 SettleAsync 是「加」，
    /// 这里是「减」，两条都只写 `character.exp`，**都不写 level**——等级不会掉，
    /// 上一等级的经验也不会被动到。经验比例从 config/classes.json 的一转基础比例
    /// 传进来（调用方读配置），这里不写第二套数。
    ///
    /// 一张卷轴 = 一笔事务：锁角色行 → 抢事务号 → 查全局冷却 → 扣一张 →
    /// 扣当前级经验 → 写冷却到期 → 提交。角色行锁把同一角色的两次用卷串行，
    /// 所以「两个人同时在两场里各点一张」也只有第一笔能过冷却检查。
    /// 任何一条拒绝路径都在扣之前回滚：卷轴、经验、冷却一个字都不写。
    /// </summary>
    public async Task<ReviveScrollOutcome> UseReviveScrollAsync(
        long characterId,
        string? account,
        string txnId,
        string itemId,
        int expCostPct,
        int cooldownMs,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁角色行并核归属。带账号的走网关口径，空账号走服间口径。
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        int level = 0;
        long exp = 0;
        var found = false;
        await using (var read = new NpgsqlCommand(
            "select level, exp from \"角色\".character " + owned,
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                read.Parameters.AddWithValue(account);
            }

            // 先把 reader 读完关掉再回滚，连接上挂着 reader 时 rollback 会抛异常。
            await using (var reader = await read.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    level = reader.GetInt32(0);
                    exp = reader.GetInt64(1);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new ReviveScrollOutcome(false, "character_not_found", 0, 0, 0, 0, 0, false);
        }

        // 2. 抢事务号。同一个号已经用过就整笔不做，返回「重放」让调用方别再加血。
        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".grant_txn (txn_id, character_id) values ($1, $2) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            if (await claim.ExecuteScalarAsync(stopping) is null)
            {
                await transaction.RollbackAsync(stopping);
                return new ReviveScrollOutcome(
                    true, "duplicate", 0, 0, level, exp, 0, true);
            }
        }

        // 3. 全局冷却：这一行在角色库里，所以换一场、换进程、重启都还在。
        long readyAtMs = 0;
        await using (var cooldown = new NpgsqlCommand(
            "select extract(epoch from r.ready_at) * 1000, "
            + "(r.ready_at > now()) "
            + "from \"角色\".revive_scroll_cooldown r where r.character_id = $1",
            connection, transaction))
        {
            cooldown.Parameters.AddWithValue(characterId);
            var cooling = false;
            await using (var reader = await cooldown.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    readyAtMs = (long)Math.Round(Convert.ToDouble(reader.GetValue(0)));
                    cooling = Convert.ToBoolean(reader.GetValue(1));
                }
            }

            if (cooling)
            {
                // 冷却没到：卷轴、经验、冷却都不动，整笔回滚。
                await transaction.RollbackAsync(stopping);
                return new ReviveScrollOutcome(
                    true, "cooldown", 0, 0, level, exp, readyAtMs, false);
            }
        }

        // 4. 扣一张：count > 1 就减一，等于 1 直接删（表上有 CHECK (count > 0)）。
        int slot;
        int count;
        await using (var bag = new NpgsqlCommand(
            "select slot, count from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2 order by slot limit 1 for update",
            connection, transaction))
        {
            bag.Parameters.AddWithValue(characterId);
            bag.Parameters.AddWithValue(itemId);
            var empty = false;
            slot = 0;
            count = 0;
            await using (var reader = await bag.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    slot = reader.GetInt16(0);
                    count = reader.GetInt32(1);
                }
                else
                {
                    empty = true;
                }
            }

            if (empty)
            {
                await transaction.RollbackAsync(stopping);
                return new ReviveScrollOutcome(
                    true, "no_scroll", 0, 0, level, exp, readyAtMs, false);
            }
        }

        await using (var take = new NpgsqlCommand(
            count > 1
                ? "update \"角色\".inventory set count = count - 1 "
                  + "where character_id = $1 and slot = $2 and count > 1"
                : "delete from \"角色\".inventory "
                  + "where character_id = $1 and slot = $2 and count = 1",
            connection, transaction))
        {
            take.Parameters.AddWithValue(characterId);
            take.Parameters.AddWithValue((short)slot);
            if (await take.ExecuteNonQueryAsync(stopping) != 1)
            {
                await transaction.RollbackAsync(stopping);
                return new ReviveScrollOutcome(
                    true, "no_scroll", 0, 0, level, exp, readyAtMs, false);
            }
        }

        // 5. 只扣当前等级已有经验的一部分。当前级经验是零时 cost = 0，
        //    这一句就是 exp = exp - 0；level 一次都不写，所以不掉级。
        var cost = exp * expCostPct / 100;
        await using (var deduct = new NpgsqlCommand(
            "update \"角色\".character set exp = exp - $2 where id = $1",
            connection, transaction))
        {
            deduct.Parameters.AddWithValue(characterId);
            deduct.Parameters.AddWithValue(cost);
            await deduct.ExecuteNonQueryAsync(stopping);
        }

        // 6. 写冷却到期。到期时刻按数据库时钟算，不受战斗服时钟影响；
        //    同一个角色再点一张时，上面第 3 步会读到这一行并回滚整笔。
        await using (var writeCooldown = new NpgsqlCommand(
            "insert into \"角色\".revive_scroll_cooldown "
            + "(character_id, ready_at, used_at, last_txn_id) "
            + "values ($1, now() + ($2::double precision * interval '1 millisecond'), now(), $3) "
            + "on conflict (character_id) do update set "
            + "ready_at = excluded.ready_at, used_at = excluded.used_at, "
            + "last_txn_id = excluded.last_txn_id",
            connection, transaction))
        {
            writeCooldown.Parameters.AddWithValue(characterId);
            writeCooldown.Parameters.AddWithValue(cooldownMs);
            writeCooldown.Parameters.AddWithValue(txnId);
            await writeCooldown.ExecuteNonQueryAsync(stopping);
        }

        // 7. 读回这一笔之后的事实值，一起提交。
        long left;
        long newReadyAtMs;
        long expAfter;
        await using (var state = new NpgsqlCommand(
            "select "
            + "(select coalesce(sum(i.count), 0) from \"角色\".inventory i "
            + " where i.character_id = $1 and i.item_id = $2), "
            + "(select extract(epoch from r.ready_at) * 1000 "
            + " from \"角色\".revive_scroll_cooldown r where r.character_id = $1), "
            + "(select c.exp from \"角色\".character c where c.id = $1)",
            connection, transaction))
        {
            state.Parameters.AddWithValue(characterId);
            state.Parameters.AddWithValue(itemId);
            await using var reader = await state.ExecuteReaderAsync(stopping);
            await reader.ReadAsync(stopping);
            left = Convert.ToInt64(reader.GetValue(0));
            newReadyAtMs = (long)Math.Round(Convert.ToDouble(reader.GetValue(1)));
            expAfter = reader.GetInt64(2);
        }

        await transaction.CommitAsync(stopping);
        return new ReviveScrollOutcome(
            true, "", left, cost, level, expAfter, newReadyAtMs, false);
    }
}

/// <summary>
/// 一笔用卷的结果。Reason 空串表示真扣掉了；其余路径整笔没执行，
/// 卷轴、经验、冷却都不动。Level 恒是扣之前的等级（这条不写 level）。
/// </summary>
public sealed record ReviveScrollOutcome(
    bool Found,
    string Reason,
    long CountLeft,
    long ExpCost,
    int Level,
    long ExpAfter,
    long ReadyAtMs,
    bool Duplicate);
