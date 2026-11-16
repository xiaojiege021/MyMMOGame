using Npgsql;

namespace Character;

/// <summary>交任务的结果。拒绝与重放都不带任何「这一手生效了」的数值。</summary>
public sealed record MainlineTurnInResult(
    bool Ok,
    string Reason,
    bool Duplicate,
    int Stage,
    int MainStage,
    string Alignment,
    int DropLines);

/// <summary>只读：角色上的主线场次、立仙或立魔，以及等级与境界。</summary>
public sealed record MainlineState(
    bool Found,
    int MainStage,
    string Alignment,
    int Level,
    string Realm);

/// <summary>
/// C1 主线十二场进度 + N7 交任务发奖。N7：与那场主线战斗的 SettleExp 共用 txn_id，
/// 须先有同号 exp_settlement，再在同一笔数据库事务里抢 grant_txn、按 mainline_drops 入包、
/// 写 main_stage。表外物品与灵石币种在加载期挡掉。缺 mainline_drops 时退回 C1（不校验收奖、不发物品）。
/// </summary>
public sealed class MainlineRepository(
    NpgsqlDataSource dataSource,
    MainlineTable table,
    MainlineDropTable dropTable,
    GrantRepository grants,
    ItemTable items,
    CharacterConfig config)
{
    public const int MaxTxnLength = ExpSettlementRepository.MaxTxnLength;

    public async Task<MainlineState> StateAsync(
        long characterId, string? account, CancellationToken stopping)
    {
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1"
            : "where id = $1 and account = $2";
        await using var command = dataSource.CreateCommand(
            "select coalesce(main_stage, 0), coalesce(alignment, ''), level, realm "
            + "from \"角色\".character " + owned);
        command.Parameters.AddWithValue(characterId);
        if (!string.IsNullOrEmpty(account))
        {
            command.Parameters.AddWithValue(account);
        }

        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return new MainlineState(false, 0, "", 0, "");
        }

        return new MainlineState(
            true, reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3));
    }

    public async Task<MainlineTurnInResult> TurnInAsync(
        long characterId,
        string? account,
        string txnId,
        int stage,
        string alignment,
        CancellationToken stopping)
    {
        var awardsEnabled = !dropTable.IsMissing;

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        var found = false;
        var level = 0;
        var realm = "";
        var mainStage = 0;
        var alignmentNow = "";
        await using (var read = new NpgsqlCommand(
            "select level, realm, coalesce(main_stage, 0), coalesce(alignment, '') "
            + "from \"角色\".character " + owned,
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
                    level = reader.GetInt32(0);
                    realm = reader.GetString(1);
                    mainStage = reader.GetInt32(2);
                    alignmentNow = reader.GetString(3);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(false, "character_not_found", false, stage, 0, "", 0);
        }

        if (awardsEnabled
            && !await SettlementExistsAsync(connection, transaction, characterId, txnId, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(
                false, "settlement_required", false, stage, mainStage, alignmentNow, 0);
        }

        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".grant_txn (txn_id, character_id) values ($1, $2) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            var claimed = await claim.ExecuteScalarAsync(stopping);
            if (claimed is null)
            {
                await transaction.RollbackAsync(stopping);
                var state = await ReadStateAsync(connection, null, characterId, stopping);
                return new MainlineTurnInResult(
                    true, "", true, stage, state.MainStage, state.Alignment, 0);
            }
        }

        if (table.IsMissing)
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(false, "mainline_missing", false, stage, mainStage, alignmentNow, 0);
        }

        var def = table.Find(stage);
        if (def is null)
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(false, "stage_unknown", false, stage, mainStage, alignmentNow, 0);
        }

        if (stage != mainStage + 1)
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(false, "stage_out_of_order", false, stage, mainStage, alignmentNow, 0);
        }

        if (level < def.LevelMin)
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(false, "level_not_enough", false, stage, mainStage, alignmentNow, 0);
        }

        if (def.RequiresBreakthrough.Length > 0
            && !await BreakthroughDoneAsync(
                connection, transaction, characterId, def.RequiresBreakthrough, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(
                false, "breakthrough_not_done", false, stage, mainStage, alignmentNow, 0);
        }

        var choice = alignment.Trim();
        if (def.AlignmentChoice)
        {
            if (!table.AlignmentChoices.Contains(choice, StringComparer.Ordinal))
            {
                await transaction.RollbackAsync(stopping);
                return new MainlineTurnInResult(
                    false, "alignment_required", false, stage, mainStage, alignmentNow, 0);
            }
        }
        else if (choice.Length > 0)
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(
                false, "alignment_not_this_stage", false, stage, mainStage, alignmentNow, 0);
        }

        var dropLines = 0;
        if (awardsEnabled)
        {
            var drops = dropTable.LinesFor(stage);
            if (drops.Count > 0)
            {
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
                    return new MainlineTurnInResult(
                        false, ex.Code, false, stage, mainStage, alignmentNow, 0);
                }
            }
        }

        if (def.AlignmentChoice)
        {
            await using var write = new NpgsqlCommand(
                "update \"角色\".character set main_stage = $2, alignment = $3 where id = $1",
                connection, transaction);
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue(stage);
            write.Parameters.AddWithValue(choice);
            await write.ExecuteNonQueryAsync(stopping);
        }
        else
        {
            await using var write = new NpgsqlCommand(
                "update \"角色\".character set main_stage = $2 where id = $1",
                connection, transaction);
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue(stage);
            await write.ExecuteNonQueryAsync(stopping);
        }

        var after = await ReadStateAsync(connection, transaction, characterId, stopping);
        await transaction.CommitAsync(stopping);
        return new MainlineTurnInResult(true, "", false, stage, after.MainStage, after.Alignment, dropLines);
    }

    static async Task<bool> SettlementExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string txnId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select 1 from \"角色\".exp_settlement where txn_id = $1 and character_id = $2 limit 1",
            connection, transaction);
        command.Parameters.AddWithValue(txnId);
        command.Parameters.AddWithValue(characterId);
        return await command.ExecuteScalarAsync(stopping) is not null;
    }

    static async Task<bool> BreakthroughDoneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string fromRealm,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select 1 from \"角色\".realm_breakthrough "
            + "where character_id = $1 and from_realm = $2 and success = true limit 1",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(fromRealm);
        return await command.ExecuteScalarAsync(stopping) is not null;
    }

    static async Task<MainlineState> ReadStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long characterId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select coalesce(main_stage, 0), coalesce(alignment, ''), level, realm "
            + "from \"角色\".character where id = $1",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return new MainlineState(false, 0, "", 0, "");
        }

        return new MainlineState(
            true, reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3));
    }
}