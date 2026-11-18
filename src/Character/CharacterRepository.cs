using Npgsql;

namespace Character;

/// <summary>建号落库的结果。主线场次与立仙/立魔由库给 NULL，这里如实带回。</summary>
public sealed record CreatedCharacter(
    long Id,
    string Name,
    string Race,
    string Appearance,
    string ClassId,
    string Branch,
    int Level,
    string Realm,
    bool MainStageEmpty,
    bool AlignmentEmpty);

/// <summary>
/// 建号的结果。三种：建成了、账号角色位已满（slot_full）、账号不在（account_not_found）。
/// 名额判定与插入在同一笔事务里，两笔并发建角不会各自数到「还差一个」。
/// </summary>
public sealed record CreateOutcome(bool Ok, string Reason, CreatedCharacter? Created);

/// <summary>
/// 改名的结果。CountLeft 是改完之后背包里还剩几张改名卡；
/// 没走到扣卡那一步时是 -1（不是 0，0 是「扣完了一张不剩」这个真事实）。
/// </summary>
public sealed record RenameOutcome(bool Ok, string Reason, string Name, int CountLeft);

/// <summary>
/// 角色表的唯一写入点。网关只能转请求过来，自己不写库。
/// 建号、改名与名字登记都在这一个类里；背包、货币、经验不在这里。
/// Q12/K3 起：名字唯一性问的是 <c>"角色".character_name</c>（跨账号全局唯一），
/// 不再是 (account, name) 那条账号内唯一的索引。
/// 改名要扣卡、要抢事务号，这两个协作方按方法传入，不给构造加依赖——
/// 只读核对（probe）建这个仓储时不该被迫装配一整套发放链。
/// </summary>
public sealed class CharacterRepository(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// 建一个角色。账号角色位（P3-4b 第二角色栏）在**同一笔事务**里判：
    /// 锁账号行 → 读这个账号的名额（库里那一行，没有就按 baseSlots）→ 数已有角色数 →
    /// 到上限就 slot_full，否则插入。锁在同一行上，两个并发建角会排队，不会都数到「还差一个」。
    /// </summary>
    public async Task<CreateOutcome> CreateAsync(
        string account,
        string name,
        string nameKey,
        string race,
        string appearance,
        string classId,
        string? branch,
        int baseSlots,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 锁账号行：名额与人数都在这把锁里读，读完到插入之间别人进不来。
        await using (var lockAccount = new NpgsqlCommand(
            "select account from \"角色\".account where account = $1 for update",
            connection, transaction))
        {
            lockAccount.Parameters.AddWithValue(account);
            if (await lockAccount.ExecuteScalarAsync(stopping) is null)
            {
                await transaction.RollbackAsync(stopping);
                return new CreateOutcome(false, "account_not_found", null);
            }
        }

        var slots = baseSlots;
        await using (var readSlots = new NpgsqlCommand(
            "select slots from \"角色\".character_slot where account = $1",
            connection, transaction))
        {
            readSlots.Parameters.AddWithValue(account);
            var value = await readSlots.ExecuteScalarAsync(stopping);
            if (value is not null and not DBNull)
            {
                slots = Convert.ToInt32(value);
            }
        }

        int used;
        await using (var readUsed = new NpgsqlCommand(
            "select count(*) from \"角色\".character where account = $1",
            connection, transaction))
        {
            readUsed.Parameters.AddWithValue(account);
            var value = await readUsed.ExecuteScalarAsync(stopping);
            used = value is null or DBNull ? 0 : Convert.ToInt32(value);
        }

        if (used >= slots)
        {
            // 一个账号默认一位；第二位要买第二角色栏（方案 12.1 第 810 行）。
            await transaction.RollbackAsync(stopping);
            return new CreateOutcome(false, "slot_full", null);
        }

        const string sql = """
            insert into "角色".character (account, name, race, appearance, class_id, branch)
            values ($1, $2, $3, $4::jsonb, $5, $6)
            returning id, name, race, appearance::text, class_id, coalesce(branch, ''),
                      level, realm, main_stage is null, alignment is null
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(race);
        command.Parameters.AddWithValue(appearance);
        command.Parameters.AddWithValue(classId);
        command.Parameters.AddWithValue(branch is null ? DBNull.Value : branch);

        CreatedCharacter created;
        await using (var reader = await command.ExecuteReaderAsync(stopping))
        {
            await reader.ReadAsync(stopping);
            created = new CreatedCharacter(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9));
        }

        // Q12/K3：名字登记与建号在同一笔事务里。两个人同时建同名时，
        // character_name 的 name_key 主键是最后一道闸，撞了整笔回滚。
        try
        {
            await using var claim = new NpgsqlCommand(
                "insert into \"角色\".character_name (name_key, name, character_id) "
                + "values ($1, $2, $3)",
                connection, transaction);
            claim.Parameters.AddWithValue(nameKey);
            claim.Parameters.AddWithValue(name);
            claim.Parameters.AddWithValue(created.Id);
            await claim.ExecuteNonQueryAsync(stopping);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(stopping);
            return new CreateOutcome(false, "name_taken", null);
        }

        await transaction.CommitAsync(stopping);
        return new CreateOutcome(true, "", created);
    }

    /// <summary>
    /// Q12/K3：名字在全服是否已被占用。按归一后的 key 查登记表，
    /// 与账号无关——换个账号也不能用同一个名字。
    /// </summary>
    public async Task<bool> NameTakenAsync(string nameKey, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select 1 from \"角色\".character_name where name_key = $1");
        command.Parameters.AddWithValue(nameKey);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        return await reader.ReadAsync(stopping);
    }

    /// <summary>只给核对用：某个角色当前占着的名字（没有登记行就返回 null）。</summary>
    public async Task<string?> RegisteredNameAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select name from \"角色\".character_name where character_id = $1");
        command.Parameters.AddWithValue(characterId);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>只给核对用：登记表里一共有几行（一个角色一行）。</summary>
    public async Task<int> NameRegistryCountAsync(CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select count(*) from \"角色\".character_name");
        var value = await command.ExecuteScalarAsync(stopping);
        return Convert.ToInt32(value);
    }

    /// <summary>
    /// Q12/K3：改名。一笔事务里做完五件事：锁角色行、抢事务号、扣一张改名卡、
    /// 改 character.name、换名字登记行（删旧 + 插新）。任一步不成立就整笔回滚——
    /// 不会出现「卡扣了名字没改」或「名字改了旧名还占着」。
    /// 拒绝码：character_not_found / rename_card_missing / name_taken /
    /// name_unchanged / duplicate。
    /// </summary>
    public async Task<RenameOutcome> RenameAsync(
        InventoryRepository inventory,
        GrantRepository grants,
        string account,
        long characterId,
        string newName,
        string newNameKey,
        string itemId,
        string txnId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        string currentName;
        await using (var lockRow = new NpgsqlCommand(
            "select name from \"角色\".character "
            + "where id = $1 and ($2 = '' or account = $2) for update",
            connection, transaction))
        {
            lockRow.Parameters.AddWithValue(characterId);
            lockRow.Parameters.AddWithValue(account);
            var value = await lockRow.ExecuteScalarAsync(stopping);
            if (value is null or DBNull)
            {
                await transaction.RollbackAsync(stopping);
                return new RenameOutcome(false, "character_not_found", "", -1);
            }

            currentName = (string)value;
        }

        // 抢号在「同名判定」与扣卡之前：同一个 txn 重放时这里就挡住，
        // 既不会二次扣卡，也不会把重放误报成 name_unchanged。
        if (!await grants.TryClaimTxnInTransactionAsync(
                connection, transaction, characterId, txnId, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return new RenameOutcome(false, "duplicate", currentName, -1);
        }

        // 归一后同名等于没改：不扣卡，免得白白花掉一张（号已抢，本次请求到此为止）。
        if (string.Equals(
                NameRulesTable.Normalize(currentName), newNameKey, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(stopping);
            return new RenameOutcome(false, "name_unchanged", currentName, -1);
        }

        if (!await inventory.ConsumeInTransactionAsync(
                connection, transaction, characterId, itemId, 1, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return new RenameOutcome(false, "rename_card_missing", currentName, 0);
        }

        await using (var rename = new NpgsqlCommand(
            "update \"角色\".character set name = $2 where id = $1",
            connection, transaction))
        {
            rename.Parameters.AddWithValue(characterId);
            rename.Parameters.AddWithValue(newName);
            await rename.ExecuteNonQueryAsync(stopping);
        }

        // 先删旧行再插新行：character_id 上有唯一索引，留着旧行插不进去；
        // 删了之后如果新名被别人抢了（并发），整笔回滚、旧行随事务恢复。
        await using (var release = new NpgsqlCommand(
            "delete from \"角色\".character_name where character_id = $1",
            connection, transaction))
        {
            release.Parameters.AddWithValue(characterId);
            await release.ExecuteNonQueryAsync(stopping);
        }

        try
        {
            await using var claim = new NpgsqlCommand(
                "insert into \"角色\".character_name (name_key, name, character_id) "
                + "values ($1, $2, $3)",
                connection, transaction);
            claim.Parameters.AddWithValue(newNameKey);
            claim.Parameters.AddWithValue(newName);
            claim.Parameters.AddWithValue(characterId);
            await claim.ExecuteNonQueryAsync(stopping);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(stopping);
            return new RenameOutcome(false, "name_taken", currentName, -1);
        }

        int left;
        await using (var readLeft = new NpgsqlCommand(
            "select coalesce(sum(count), 0) from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2",
            connection, transaction))
        {
            readLeft.Parameters.AddWithValue(characterId);
            readLeft.Parameters.AddWithValue(itemId);
            var value = await readLeft.ExecuteScalarAsync(stopping);
            left = value is null or DBNull ? 0 : Convert.ToInt32(value);
        }

        await transaction.CommitAsync(stopping);
        return new RenameOutcome(true, "", newName, left);
    }

    /// <summary>账号必须在，否则外键会炸；先查一次好给出明确的错误码。</summary>
    public async Task<bool> AccountExistsAsync(string account, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select 1 from \"角色\".account where account = $1");
        command.Parameters.AddWithValue(account);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        return await reader.ReadAsync(stopping);
    }

    /// <summary>只给核对用：这一行是不是空的主线与空的分路。不打印别的列。</summary>
    public async Task<(bool MainStageEmpty, bool AlignmentEmpty)> NullabilityOfAsync(
        string name, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select main_stage is null, alignment is null from \"角色\".character where name = $1");
        command.Parameters.AddWithValue(name);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return (false, false);
        }

        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    /// <summary>只给核对用：角色总数。</summary>
    public async Task<int> CountAsync(CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand("select count(*) from \"角色\".character");
        var value = await command.ExecuteScalarAsync(stopping);
        return Convert.ToInt32(value);
    }

    public sealed record CharacterReport(
        long Id,
        string Race,
        string ClassId,
        string Branch,
        int Level,
        string Realm,
        bool MainStageEmpty,
        bool AlignmentEmpty,
        int AppearanceLength);

    /// <summary>只给核对用：按名字读回一行，看落库的东西与请求一致。</summary>
    public async Task<CharacterReport?> ReportAsync(string name, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select id, race, class_id, coalesce(branch, ''), level, realm, "
            + "main_stage is null, alignment is null, length(appearance::text) "
            + "from \"角色\".character where name = $1");
        command.Parameters.AddWithValue(name);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return new CharacterReport(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetBoolean(6),
            reader.GetBoolean(7),
            reader.GetInt32(8));
    }

    /// <summary>核对收尾用：按名字删掉测试角色。</summary>
    public async Task<int> DeleteAsync(string name, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand("delete from \"角色\".character where name = $1");
        command.Parameters.AddWithValue(name);
        return await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>
    /// 写等级。只改自己账号下的角色；带账号进 where，别人的编号改不动。
    /// 返回 null 表示这行不归这个账号（或不存在），由调用方转成错误码。
    /// </summary>
    public async Task<int?> SetLevelAsync(
        string account,
        long characterId,
        int level,
        CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set level = $3 "
            + "where id = $2 and account = $1 returning level");
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(level);

        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return reader.GetInt32(0);
    }

    /// <summary>
    /// 只给核对用：绕过接口直写库，验库层那道 CHECK 是不是真的在。
    /// 正常路径不能走这里，写成 101 必须被数据库拒掉。
    /// </summary>
    public async Task RawWriteLevelAsync(long characterId, int level, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set level = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(level);
        await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>只读：单角色的当前境界。突破与进本的版本开关都按这一列判。</summary>
    public async Task<string?> RealmOfAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select realm from \"角色\".character where id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        return await reader.ReadAsync(stopping) ? reader.GetString(0) : null;
    }

    /// <summary>
    /// 只给核对用：绕过突破流程直写境界，验开关关着时不能进副本、开了以后才放行。
    /// 正常路径不能走这里；境界只由 P3-1a2 的突破那一笔事务写。
    /// </summary>
    public async Task RawWriteRealmAsync(long characterId, string realm, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set realm = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(realm);
        await command.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>
    /// P2-4a：二转只把 15 级选定的那条分支往前推一步。一笔事务锁角色行 → 校验
    /// 等级 / 已有分支 / 目标分支是否同一条 → 把 branch_step 写成 2。
    /// 分支归属（目标分支是不是这个职业的）由调用方按配置先验，这里只管库里的状态。
    /// </summary>
    public async Task<(string Reason, string Branch, int Step, int Level, bool Duplicate)>
        AdvanceBranchAsync(long characterId, string branchId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        string branch;
        int step;
        int level;
        // 先把这一行读完再回滚/提交：reader 没关就 Rollback 会撞 Npgsql 的子通道错误。
        await using (var read = new NpgsqlCommand(
            "select coalesce(branch, ''), branch_step, level from \"角色\".character where id = $1 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                await reader.DisposeAsync();
                await transaction.RollbackAsync(stopping);
                return ("character_not_found", "", 0, 0, false);
            }

            branch = reader.GetString(0);
            step = reader.GetInt32(1);
            level = reader.GetInt32(2);
        }

        // 目标必须是已经选定的那条：换一条分支，不论几转都是 branch_locked。
        if (!string.Equals(branch, branchId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(stopping);
            return (branch.Length == 0 ? "branch_missing" : "branch_locked", branch, step, level, false);
        }

        if (step >= 2)
        {
            await transaction.RollbackAsync(stopping);
            return ("", branch, step, level, true);
        }

        if (level < BranchAdvanceLevel)
        {
            await transaction.RollbackAsync(stopping);
            return ("not_enough_level", branch, step, level, false);
        }

        await using (var write = new NpgsqlCommand(
            "update \"角色\".character set branch_step = 2 where id = $1", connection, transaction))
        {
            write.Parameters.AddWithValue(characterId);
            await write.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return ("", branch, 2, level, false);
    }

    /// <summary>方案写定二转在 45 级，不跟着境界往后挪；这条是规则常量，不是玩法数值。</summary>
    public const int BranchAdvanceLevel = 45;

    /// <summary>只给核对用：读回分支与二转步数。</summary>
    public async Task<(string Branch, int Step, int Level, string ClassId)?> BranchStateAsync(
        long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select coalesce(branch, ''), branch_step, level, class_id"
            + " from \"角色\".character where id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3));
    }

    /// <summary>
    /// P2-3b：战场要的只读身份——职业、分支、等级。战场属性按职业表 + 装备折算，
    /// 装备那条线由强化总级给（EnhanceRepository.TotalsAsync）。这里不写库。
    /// </summary>
    public async Task<(string ClassId, string Branch, int Level, int BranchStep)?> BattleIdentityAsync(
        long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select class_id, coalesce(branch, ''), level, branch_step from \"角色\".character where id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    /// <summary>只给核对用：读一行的等级与归属账号。</summary>
    public async Task<(string Account, int Level)?> LevelOfAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select account, level from \"角色\".character where id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetInt32(1));
    }

    /// <summary>疲劳与周份额的现值。主线进本不该动这两个数。</summary>
    public async Task<(int Fatigue, int WeeklyShare)?> ReservesAsync(
        long characterId,
        CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select fatigue, weekly_share from \"角色\".character where id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>记一次进本。次数与份额的扣减不在这里，主线本来就不扣。</summary>
    public async Task<long> RecordEntryAsync(
        long characterId,
        string dungeonId,
        int partySize,
        CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "insert into \"角色\".dungeon_entry (character_id, dungeon_id, party_size) "
            + "values ($1, $2, $3) returning id");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(dungeonId);
        command.Parameters.AddWithValue(partySize);
        var value = await command.ExecuteScalarAsync(stopping);
        return Convert.ToInt64(value);
    }
}
