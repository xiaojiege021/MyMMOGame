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
/// 角色表的唯一写入点。网关只能转请求过来，自己不写库。
/// 这一行只做「建一个角色」；查角色、改角色、背包与货币都不在这里。
/// </summary>
public sealed class CharacterRepository(NpgsqlDataSource dataSource)
{
    public async Task<CreatedCharacter> CreateAsync(
        string account,
        string name,
        string race,
        string appearance,
        string classId,
        string? branch,
        CancellationToken stopping)
    {
        const string sql = """
            insert into "角色".character (account, name, race, appearance, class_id, branch)
            values ($1, $2, $3, $4::jsonb, $5, $6)
            returning id, name, race, appearance::text, class_id, coalesce(branch, ''),
                      level, realm, main_stage is null, alignment is null
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(race);
        command.Parameters.AddWithValue(appearance);
        command.Parameters.AddWithValue(classId);
        command.Parameters.AddWithValue(branch is null ? DBNull.Value : branch);

        await using var reader = await command.ExecuteReaderAsync(stopping);
        await reader.ReadAsync(stopping);
        return new CreatedCharacter(
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

    /// <summary>同一个账号下名字不重。撞了返回 false，由调用方转成错误码。</summary>
    public async Task<bool> NameTakenAsync(string account, string name, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select 1 from \"角色\".character where account = $1 and name = $2");
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue(name);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        return await reader.ReadAsync(stopping);
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
