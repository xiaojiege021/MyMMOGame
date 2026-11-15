using Npgsql;

namespace Character;

/// <summary>交任务的结果。拒绝与重放都不带任何「这一手生效了」的数值。</summary>
public sealed record MainlineTurnInResult(
    bool Ok,
    string Reason,
    bool Duplicate,
    int Stage,
    int MainStage,
    string Alignment);

/// <summary>只读：角色上的主线场次、立仙或立魔，以及等级与境界。</summary>
public sealed record MainlineState(
    bool Found,
    int MainStage,
    string Alignment,
    int Level,
    string Realm);

/// <summary>
/// C1 主线十二场：主线进度的唯一写入点。方案 2.8「主线和支线现在不做成系统，
/// 角色服先留下这三处。后面的任务只加配置，并提交已经有的那笔发奖事务」；
/// 开发架构第 627 行「交任务使用 P1-1b5 的事务号」。
///
/// 一笔事务里做完：锁角色行 → 抢 "角色".grant_txn 的事务号 → 校验顺序、等级、
/// 突破门槛与立仙立魔 → 写 character.main_stage（第 10 场另写 alignment）→
/// 事务内读回 → 提交。同号重放整笔不做（duplicate），任何拒绝都整体回滚，
/// 已经 insert 的事务号一并退回，不会占号。
///
/// 这里不发任何数值奖励：十二场战斗仍走 P1-2a 既有的进本结算（等级经验由
/// SettleExp 那一笔写），掉落 / 基础装备 / 发奖链路没有承接卡，本卡不发明第二份。
/// 也不碰境界、疲劳、周份额、背包与货币；境界只用来读第 10/11/12 场的门槛事实。
/// </summary>
public sealed class MainlineRepository(NpgsqlDataSource dataSource, MainlineTable table)
{
    public const int MaxTxnLength = 64;

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
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁角色行并核归属。带账号的走网关口径，空账号走服间可信口径。
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
            return new MainlineTurnInResult(false, "character_not_found", false, stage, 0, "");
        }

        // 2. 抢 P1-1b5 的事务号。这个号已经用过就整笔不做，回滚后用同一连接重读事实值。
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
                    true, "", true, stage, state.MainStage, state.Alignment);
            }
        }

        // 3. 判定全部在这一笔事务里，任何一条不满足整笔回滚（含刚抢到的事务号）。
        if (table.IsMissing)
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(false, "mainline_missing", false, stage, mainStage, alignmentNow);
        }

        var def = table.Find(stage);
        if (def is null)
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(false, "stage_unknown", false, stage, mainStage, alignmentNow);
        }

        if (stage != mainStage + 1)
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(false, "stage_out_of_order", false, stage, mainStage, alignmentNow);
        }

        if (level < def.LevelMin)
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(false, "level_not_enough", false, stage, mainStage, alignmentNow);
        }

        if (def.RequiresBreakthrough.Length > 0
            && !await BreakthroughDoneAsync(
                connection, transaction, characterId, def.RequiresBreakthrough, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(
                false, "breakthrough_not_done", false, stage, mainStage, alignmentNow);
        }

        var choice = alignment.Trim();
        if (def.AlignmentChoice)
        {
            // 方案 2.8：立仙或立魔只在第 10 场写入；选择本身不分路、不加属性。
            if (!table.AlignmentChoices.Contains(choice, StringComparer.Ordinal))
            {
                await transaction.RollbackAsync(stopping);
                return new MainlineTurnInResult(
                    false, "alignment_required", false, stage, mainStage, alignmentNow);
            }
        }
        else if (choice.Length > 0)
        {
            await transaction.RollbackAsync(stopping);
            return new MainlineTurnInResult(
                false, "alignment_not_this_stage", false, stage, mainStage, alignmentNow);
        }

        // 4. 只写角色上的剧情进度。境界 / 等级 / 疲劳 / 次数 / 背包一个字都不动。
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

        // 5. 事务内读回再提交：回包给的是库里的这一刻，不是请求里带进来的值。
        var after = await ReadStateAsync(connection, transaction, characterId, stopping);
        await transaction.CommitAsync(stopping);
        return new MainlineTurnInResult(true, "", false, stage, after.MainStage, after.Alignment);
    }

    /// <summary>第 10/11/12 场：等对应境界突破成功。判据是角色库里那条成功流水，
    /// 版本开关仍由 P3-1a2 既有链路管，本卡不改开关、不自己放行。</summary>
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
