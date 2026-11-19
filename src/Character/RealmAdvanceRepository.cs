using Npgsql;

namespace Character;

/// <summary>
/// 一次突破的结果。Found=false 表示角色不在；Executed=true 表示这一笔真的掷过了
/// （成功或失败都算执行，历练与灵石都已按配置扣掉）。Reason 只在没掷的时候给：
/// realm_locked / realm_max / realm_unknown / heart_not_done / drill_not_enough /
/// stone_not_enough / pill_not_enough。Duplicate=true 表示这个事务号已经用过，整笔没做。
/// </summary>
public sealed record RealmAdvanceOutcome(
    bool Found,
    string Reason,
    bool Executed,
    bool Success,
    bool Duplicate,
    string From,
    string Next,
    string Realm,
    long DrillBefore,
    long DrillRequired,
    long DrillCost,
    long DrillAfter,
    string StoneKind,
    long StoneCost,
    long StoneLeft,
    int MainStage,
    int MainStageRequired,
    IReadOnlyList<string> MissingQuests,
    int SuccessPct,
    // B1：这一笔的境界丹（配置值）、要扣几枚、扣完还剩几颗（事实值）。
    // 没配丹的段（B1 之前的临时表）留空 / 0，老调用点不必改。
    string PillKind = "",
    long PillCount = 0,
    long PillLeft = 0);

/// <summary>一次历练结算的结果。Reason 为空表示加上了；duplicate 表示这个号已经结过。</summary>
public sealed record DrillSettlementOutcome(
    bool Found,
    string Reason,
    bool Duplicate,
    string DungeonId,
    long Gain,
    long Drill);

/// <summary>
/// 境界突破与历练结算的唯一写入点（P3-1a2）。方案 8.1：
///   - 修为就是当前境界，成功才把 character.realm 写成下一段；失败停在原境界。
///   - 历练只统计已经能进入的副本，结算时一次写入 character.drill。
///   - 炼心 = 这一段已经打开的主线（character.main_stage）+ 必要支线（character_quest）。
///   - 突破消耗灵石，并扣掉这次要求的历练值；条件不满足时一分都不扣。
///   - B1：还消耗一枚境界丹（筑基丹 / 破镜丹）。丹与灵石、历练同一笔事务：
///     条件全满足才抢事务号，扣丹、扣灵石、扣历练之后才掷；掷失败停在原境界，丹不退。
///
/// 一笔突破 = 一笔事务：锁角色行 → 版本开关 → 炼心 → 历练 → 境界丹够不够 →
/// 抢事务号 → 扣历练 → 扣灵石 → 扣丹 → （成功才）写境界 → 提交。任何一条不满足都整笔回滚，
/// 历练、灵石、丹、境界、流水一个字都不留。随机只在这里掷一次，配置里的
/// success_pct 是唯一概率来源，代码里不写第二个数。
/// </summary>
public sealed class RealmAdvanceRepository(NpgsqlDataSource dataSource, RealmTable realms)
{
    public const int MaxTxnLength = 128;

    public async Task<RealmAdvanceOutcome> AdvanceAsync(
        long characterId,
        string? account,
        string txnId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁角色行并核归属。带账号的走网关口径，空账号走服间可信口径。
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        string realm = "";
        long drill = 0;
        var mainStage = 0;
        var realmPhase = 0;
        var found = false;
        await using (var read = new NpgsqlCommand(
            "select realm, drill, coalesce(main_stage, 0), realm_phase from \"角色\".character " + owned,
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                read.Parameters.AddWithValue(account);
            }

            await using (var reader = await read.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    realm = reader.GetString(0);
                    drill = reader.GetInt64(1);
                    mainStage = reader.GetInt32(2);
                    realmPhase = reader.GetInt16(3);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new RealmAdvanceOutcome(
                false, "character_not_found", false, false, false,
                "", "", "", 0, 0, 0, 0, "", 0, 0, 0, 0, [], 0);
        }

        // 2. 同一个事务号已经掷过：不管当前境界变没变，都整笔不做。
        //    先查流水再判条件，重放才不会因为「境界已经进了下一段」被误判成别的拒绝。
        await using (var dup = new NpgsqlCommand(
            "select from_realm, to_realm, drill_cost, stone_cost, pill_kind, pill_count "
            + "from \"角色\".realm_breakthrough where txn_id = $1",
            connection, transaction))
        {
            dup.Parameters.AddWithValue(txnId);
            await using var reader = await dup.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                var doneFrom = reader.GetString(0);
                var doneNext = reader.GetString(1);
                var doneDrill = reader.GetInt64(2);
                var doneStone = reader.GetInt64(3);
                var donePill = reader.GetString(4);
                var donePillCount = reader.GetInt64(5);
                await reader.DisposeAsync();
                await transaction.RollbackAsync(stopping);
                return new RealmAdvanceOutcome(
                    true, "", false, false, true,
                    doneFrom, doneNext, realm, drill, doneDrill, 0, drill,
                    "", doneStone, 0, mainStage, 0, [], 0,
                    donePill, donePillCount, 0);
            }
        }

        // 3. 版本开关：下一段没开就不能突破（P3-1a1 的判定，这里复用同一处）。
        var gate = realms.CheckAdvance(realm);
        if (!gate.Ok)
        {
            await transaction.RollbackAsync(stopping);
            return new RealmAdvanceOutcome(
                true, gate.Reason, false, false, false,
                gate.From, gate.Next, gate.From, drill, 0, 0, drill,
                "", 0, await StoneBalanceAsync(connection, transaction, characterId, "", stopping),
                mainStage, 0, [], 0);
        }

        var requirement = realms.Requirement(realm);
        if (requirement is null)
        {
            // 配置校验已经保证「极境之外每段都有要求」，走到这里只可能是库里的境界不认识。
            await transaction.RollbackAsync(stopping);
            return new RealmAdvanceOutcome(
                true, "realm_unknown", false, false, false,
                gate.From, gate.Next, gate.From, drill, 0, 0, drill,
                "", 0, 0, mainStage, 0, [], 0);
        }

        // R1：突破门槛按当前境界取——炼体要满第 9 阶（index 8），其余要后期（index 2）。
        if (realmPhase < realms.SubPhasesFor(realm).BreakthroughMinIndex)
        {
            // 拒绝路径要先读余额再回滚：回滚后这个事务不能再用，拿它构造命令会抛
            // 「Transaction is already completed」，把一次正常的业务拒绝变成进程崩。
            var stoneAtGate = await StoneBalanceAsync(
                connection, transaction, characterId, requirement.Stone, stopping);
            await transaction.RollbackAsync(stopping);
            return new RealmAdvanceOutcome(
                true, "realm_phase_not_enough", false, false, false,
                requirement.From, requirement.Next, requirement.From, drill,
                requirement.Drill, 0, drill, requirement.Stone, requirement.StoneCost,
                stoneAtGate,
                mainStage, requirement.MainStage, [], requirement.SuccessPct,
                requirement.Pill ?? "", requirement.PillCount,
                await PillBalanceOutsideAsync(characterId, requirement.Pill, stopping));
        }

        // 3. 炼心：这一段已经打开的主线场次，以及这一段列出的必要支线。
        //    没打开的主线不查——main_stage 只记已经走到的那一场。
        // 先读一次灵石余额：任何拒绝路径都在回滚之后返回，回滚后这个事务就不能再读。
        var stoneNow = await StoneBalanceAsync(
            connection, transaction, characterId, requirement.Stone, stopping);
        // B1：丹的余额也先读一次——所有拒绝路径都在回滚之后返回，回滚后这个事务不能再读。
        var pillNow = await PillBalanceAsync(
            connection, transaction, characterId, requirement.Pill, stopping);
        var missing = await MissingQuestsAsync(
            connection, transaction, characterId, requirement.SideQuests, stopping);
        if (mainStage < requirement.MainStage || missing.Count > 0)
        {
            await transaction.RollbackAsync(stopping);
            return new RealmAdvanceOutcome(
                true, "heart_not_done", false, false, false,
                requirement.From, requirement.Next, requirement.From, drill,
                requirement.Drill, 0, drill, requirement.Stone, requirement.StoneCost,
                stoneNow,
                mainStage, requirement.MainStage, missing, requirement.SuccessPct,
                requirement.Pill ?? "", requirement.PillCount, pillNow);
        }

        // 4. 历练够不够。不够就停在原境界，一分灵石也不动。
        if (drill < requirement.Drill)
        {
            await transaction.RollbackAsync(stopping);
            return new RealmAdvanceOutcome(
                true, "drill_not_enough", false, false, false,
                requirement.From, requirement.Next, requirement.From, drill,
                requirement.Drill, 0, drill, requirement.Stone, requirement.StoneCost,
                stoneNow,
                mainStage, requirement.MainStage, [], requirement.SuccessPct,
                requirement.Pill ?? "", requirement.PillCount, pillNow);
        }

        // 4.5 B1：境界丹够不够。方案 12.1 写定筑基丹 / 破镜丹用于境界突破；
        //     不够就停在原境界，历练、灵石、丹一枚都不动，也不抢事务号。
        if (requirement.PillCount > 0
            && pillNow < requirement.PillCount)
        {
            await transaction.RollbackAsync(stopping);
            return new RealmAdvanceOutcome(
                true, "pill_not_enough", false, false, false,
                requirement.From, requirement.Next, requirement.From, drill,
                requirement.Drill, 0, drill, requirement.Stone, requirement.StoneCost,
                stoneNow,
                mainStage, requirement.MainStage, [], requirement.SuccessPct,
                requirement.Pill ?? "", requirement.PillCount, pillNow);
        }

        // 5. 抢事务号。同一个号再提交不掷第二次，也不扣任何东西。
        //    成功与否在抢号之前定下来，流水里记的就是这一笔的结果。
        var success = Random.Shared.Next(100) < requirement.SuccessPct;
        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".realm_breakthrough "
            + "(txn_id, character_id, from_realm, to_realm, success, drill_cost, stone_kind, stone_cost, "
            + "pill_kind, pill_count) "
            + "values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            claim.Parameters.AddWithValue(requirement.From);
            claim.Parameters.AddWithValue(requirement.Next);
            claim.Parameters.AddWithValue(success);
            claim.Parameters.AddWithValue(requirement.Drill);
            claim.Parameters.AddWithValue(requirement.Stone);
            claim.Parameters.AddWithValue(requirement.StoneCost);
            claim.Parameters.AddWithValue(requirement.Pill ?? "");
            claim.Parameters.AddWithValue(requirement.PillCount);
            if (await claim.ExecuteScalarAsync(stopping) is null)
            {
                await transaction.RollbackAsync(stopping);
                return new RealmAdvanceOutcome(
                    true, "", false, false, true,
                    requirement.From, requirement.Next, requirement.From, drill,
                    requirement.Drill, 0, drill, requirement.Stone, requirement.StoneCost,
                    stoneNow,
                    mainStage, requirement.MainStage, [], requirement.SuccessPct,
                    requirement.Pill ?? "", requirement.PillCount, pillNow);
            }
        }

        // 6. 扣历练。带余额条件的 update：并发两笔只有一笔能过，扣不成整笔回滚。
        long drillAfter;
        await using (var deductDrill = new NpgsqlCommand(
            "update \"角色\".character set drill = drill - $2 "
            + "where id = $1 and drill >= $2 returning drill",
            connection, transaction))
        {
            deductDrill.Parameters.AddWithValue(characterId);
            deductDrill.Parameters.AddWithValue(requirement.Drill);
            var value = await deductDrill.ExecuteScalarAsync(stopping);
            if (value is null || value is DBNull)
            {
                await transaction.RollbackAsync(stopping);
                return new RealmAdvanceOutcome(
                    true, "drill_not_enough", false, false, false,
                    requirement.From, requirement.Next, requirement.From, drill,
                    requirement.Drill, 0, drill, requirement.Stone, requirement.StoneCost,
                    stoneNow,
                    mainStage, requirement.MainStage, [], requirement.SuccessPct,
                    requirement.Pill ?? "", requirement.PillCount, pillNow);
            }

            drillAfter = Convert.ToInt64(value);
        }

        // 7. 扣灵石。与 P1-1b4 同一套写法：扣钱只用带余额条件的 update。
        long stoneLeft;
        await using (var deductStone = new NpgsqlCommand(
            "update \"角色\".currency set amount = amount - $3, updated_at = now() "
            + "where character_id = $1 and kind = $2 and amount >= $3 returning amount",
            connection, transaction))
        {
            deductStone.Parameters.AddWithValue(characterId);
            deductStone.Parameters.AddWithValue(requirement.Stone);
            deductStone.Parameters.AddWithValue(requirement.StoneCost);
            var value = await deductStone.ExecuteScalarAsync(stopping);
            if (value is null || value is DBNull)
            {
                await transaction.RollbackAsync(stopping);
                return new RealmAdvanceOutcome(
                    true, "stone_not_enough", false, false, false,
                    requirement.From, requirement.Next, requirement.From, drill,
                    requirement.Drill, 0, drill, requirement.Stone, requirement.StoneCost,
                    stoneNow,
                    mainStage, requirement.MainStage, [], requirement.SuccessPct,
                    requirement.Pill ?? "", requirement.PillCount, pillNow);
            }

            stoneLeft = Convert.ToInt64(value);
        }

        // 7.5 B1：扣境界丹。与灵石同口径——抢到事务号之后才扣，掷失败也不退。
        //      预判已经拦过「不够」，这里再按行扣一次是为了并发时不出现「掷了但丹没扣」。
        if (requirement.PillCount > 0)
        {
            var deducted = await DeductItemsAsync(
                connection, transaction, characterId, requirement.Pill,
                requirement.PillCount, stopping);
            if (!deducted)
            {
                await transaction.RollbackAsync(stopping);
                return new RealmAdvanceOutcome(
                    true, "pill_not_enough", false, false, false,
                    requirement.From, requirement.Next, requirement.From, drill,
                    requirement.Drill, 0, drill, requirement.Stone, requirement.StoneCost,
                    stoneNow,
                    mainStage, requirement.MainStage, [], requirement.SuccessPct,
                    requirement.Pill ?? "", requirement.PillCount,
                    await PillBalanceOutsideAsync(characterId, requirement.Pill, stopping));
            }
        }

        var pillLeft = await PillBalanceAsync(
            connection, transaction, characterId, requirement.Pill, stopping);

        // 8. 成功才写境界；失败停在原境界。历练、灵石与丹三种结果都扣。
        if (success)
        {
            await using var write = new NpgsqlCommand(
                "update \"角色\".character set realm = $2, realm_phase = 0 where id = $1",
                connection, transaction);
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue(requirement.Next);
            await write.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new RealmAdvanceOutcome(
            true, "", true, success, false,
            requirement.From, requirement.Next,
            success ? requirement.Next : requirement.From,
            drill, requirement.Drill, requirement.Drill, drillAfter,
            requirement.Stone, requirement.StoneCost, stoneLeft,
            mainStage, requirement.MainStage, [], requirement.SuccessPct,
            requirement.Pill ?? "", requirement.PillCount, pillLeft);
    }

    /// <summary>
    /// 历练结算（方案 8.1 / 技术架构第 7 节）：只统计已经能进入的副本。
    /// 写了 realm 的新副本仍按「这一段开没开、个人境界够不够」判；旧副本没有门槛。
    /// 一场通关一个事务号，重复结算不加第二次。
    /// </summary>
    public async Task<DrillSettlementOutcome> SettleDrillAsync(
        long characterId,
        string? account,
        string dungeonId,
        string? dungeonRealm,
        long gain,
        string txnId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        string realm = "";
        long drill = 0;
        var found = false;
        await using (var read = new NpgsqlCommand(
            "select realm, drill from \"角色\".character " + owned, connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                read.Parameters.AddWithValue(account);
            }

            await using (var reader = await read.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    realm = reader.GetString(0);
                    drill = reader.GetInt64(1);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new DrillSettlementOutcome(false, "character_not_found", false, dungeonId, 0, 0);
        }

        var gate = realms.CheckDungeon(dungeonRealm, realm);
        if (!gate.Ok)
        {
            await transaction.RollbackAsync(stopping);
            return new DrillSettlementOutcome(true, gate.Reason, false, dungeonId, 0, drill);
        }

        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".realm_drill (txn_id, character_id, dungeon_id, drill) "
            + "values ($1, $2, $3, $4) on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            claim.Parameters.AddWithValue(dungeonId);
            claim.Parameters.AddWithValue(gain);
            if (await claim.ExecuteScalarAsync(stopping) is null)
            {
                await transaction.RollbackAsync(stopping);
                return new DrillSettlementOutcome(true, "", true, dungeonId, 0, drill);
            }
        }

        long after;
        await using (var write = new NpgsqlCommand(
            "update \"角色\".character set drill = drill + $2 where id = $1 returning drill",
            connection, transaction))
        {
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue(gain);
            after = Convert.ToInt64(await write.ExecuteScalarAsync(stopping));
        }

        await transaction.CommitAsync(stopping);
        return new DrillSettlementOutcome(true, "", false, dungeonId, gain, after);
    }

    /// <summary>只给核对用：读境界、历练、主线场次与已完成的必要支线。</summary>
    public async Task<(string Realm, int RealmPhase, long Drill, int MainStage, List<string> Quests)?> StateAsync(
        long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select realm, realm_phase, drill, coalesce(main_stage, 0), "
            + "(select coalesce(json_agg(quest_id order by quest_id)::text, '[]') "
            + " from \"角色\".character_quest q where q.character_id = c.id) "
            + "from \"角色\".character c where c.id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        var quests = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [];
        return (reader.GetString(0), reader.GetInt16(1), reader.GetInt64(2), reader.GetInt32(3), quests);
    }

    /// <summary>只给核对用：直写历练值，验消耗与库层非负 CHECK。</summary>
    public async Task RawWriteDrillAsync(long characterId, long drill, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set drill = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(drill);
        await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>只给核对用：直写主线场次。正常路径由主线卡写，这张卡只读。</summary>
    public async Task RawWriteMainStageAsync(long characterId, int stage, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set main_stage = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(stage);
        await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>只给核对用：记一条必要支线完成。正常路径由任务卡写，这张卡只读。</summary>
    public async Task CompleteQuestAsync(long characterId, string questId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "insert into \"角色\".character_quest (character_id, quest_id) values ($1, $2) "
            + "on conflict (character_id, quest_id) do nothing");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(questId);
        await command.ExecuteNonQueryAsync(stopping);
    }

    static async Task<List<string>> MissingQuestsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        IReadOnlyList<string> required,
        CancellationToken stopping)
    {
        if (required.Count == 0)
        {
            return [];
        }

        var done = new HashSet<string>(StringComparer.Ordinal);
        await using (var read = new NpgsqlCommand(
            "select quest_id from \"角色\".character_quest "
            + "where character_id = $1 and quest_id = any($2)",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue(required.ToArray());
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                done.Add(reader.GetString(0));
            }
        }

        return required.Where(q => !done.Contains(q)).ToList();
    }

    static async Task<long> StoneBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string kind,
        CancellationToken stopping)
    {
        if (kind.Length == 0)
        {
            return 0;
        }

        await using var read = new NpgsqlCommand(
            "select amount from \"角色\".currency where character_id = $1 and kind = $2",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(kind);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>
    /// B1：这个角色背包里有几颗这个丹（同一笔事务里读）。没配丹的段返回 0，
    /// 调用方按 requirement.PillCount 判断要不要查。
    /// </summary>
    static async Task<long> PillBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string? itemId,
        CancellationToken stopping)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return 0;
        }

        await using var read = new NpgsqlCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(itemId);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>
    /// 事务已经回滚之后还要报一次余额时用：回滚后那个事务不能再构造命令，
    /// 换一条独立连接只读（与 R1 的拒绝路径同一个口径）。
    /// </summary>
    async Task<long> PillBalanceOutsideAsync(
        long characterId, string? itemId, CancellationToken stopping)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return 0;
        }

        await using var command = dataSource.CreateCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(itemId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(stopping) ?? 0L);
    }

    /// <summary>
    /// B1：从背包扣掉 N 颗同一种丹。一格一颗就删行（表上有 CHECK (count &gt; 0)），
    /// 一格多颗就减数量；够不够按所有格加起来算，扣到够为止。
    /// 扣不满就返回 false，由调用方整笔回滚——这一笔不留「扣了一半」。
    /// </summary>
    static async Task<bool> DeductItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string? itemId,
        long count,
        CancellationToken stopping)
    {
        if (count <= 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(itemId))
        {
            return false;
        }

        var rows = new List<(short Slot, int Count)>();
        await using (var read = new NpgsqlCommand(
            "select slot, count from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2 order by slot for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue(itemId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                rows.Add((reader.GetInt16(0), reader.GetInt32(1)));
            }
        }

        var need = count;
        foreach (var row in rows)
        {
            if (need <= 0)
            {
                break;
            }

            var take = (int)Math.Min(need, row.Count);
            await using var write = new NpgsqlCommand(
                take >= row.Count
                    ? "delete from \"角色\".inventory "
                      + "where character_id = $1 and slot = $2"
                    : "update \"角色\".inventory set count = count - $3 "
                      + "where character_id = $1 and slot = $2",
                connection, transaction);
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue(row.Slot);
            if (take < row.Count)
            {
                write.Parameters.AddWithValue(take);
            }

            await write.ExecuteNonQueryAsync(stopping);
            need -= take;
        }

        return need <= 0;
    }
}
