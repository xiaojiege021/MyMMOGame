using Npgsql;

namespace Social;

public sealed partial class SectRepository
{
    // ---- 只读：--sect-list / --sect-state / --sect-members / DescribeSect ----
    public async Task<List<SectState>> ListAsync(CancellationToken stopping)
    {
        var list = new List<SectState>();
        await using var command = dataSource.CreateCommand(
            "select s.id, s.name, s.level, "
            + "(select count(*) from \"社交\".sect_member m where m.sect_id = s.id) "
            + "from \"社交\".sect s order by s.id");
        await using var reader = await command.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            var level = reader.GetInt32(2);
            list.Add(new SectState(true, reader.GetInt64(0), reader.GetString(1), level,
                CapOrZero(level), Convert.ToInt32(reader.GetValue(3))));
        }

        return list;
    }

    public async Task<SectState> StateAsync(long sectId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select s.id, s.name, s.level, "
            + "(select count(*) from \"社交\".sect_member m where m.sect_id = s.id) "
            + "from \"社交\".sect s where s.id = $1");
        command.Parameters.AddWithValue(sectId);
        return await ReadOneAsync(command, sectId, stopping);
    }

    public async Task<SectState> StateByNameAsync(string name, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select s.id, s.name, s.level, "
            + "(select count(*) from \"社交\".sect_member m where m.sect_id = s.id) "
            + "from \"社交\".sect s where s.name = $1");
        command.Parameters.AddWithValue(name);
        return await ReadOneAsync(command, 0, stopping);
    }

    async Task<SectState> ReadOneAsync(
        NpgsqlCommand command, long missingId, CancellationToken stopping)
    {
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return new SectState(false, missingId, "", 0, 0, 0);
        }

        var level = reader.GetInt32(2);
        return new SectState(true, reader.GetInt64(0), reader.GetString(1), level,
            CapOrZero(level), Convert.ToInt32(reader.GetValue(3)));
    }

    public async Task<List<long>> MembersAsync(long sectId, CancellationToken stopping)
    {
        var list = new List<long>();
        await using var command = dataSource.CreateCommand(
            "select character_id from \"社交\".sect_member "
            + "where sect_id = $1 order by joined_at, character_id");
        command.Parameters.AddWithValue(sectId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            list.Add(reader.GetInt64(0));
        }

        return list;
    }

    /// <summary>
    /// N2：名册逐条只读。每行是角色号和职位，按角色号排序。
    /// 掌门可以不在 sect_member（创建者不入册），这时按 master_character_id 补一条 position=master。
    /// 已经在册的人沿用库里的 position，这里不改写。不任免、不数席位上限、不写库。
    /// </summary>
    public sealed record RosterMember(long CharacterId, string Position);

    public sealed record Roster(bool Found, long SectId, IReadOnlyList<RosterMember> Members);

    public async Task<Roster> RosterAsync(long sectId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var head = new NpgsqlCommand(
            "select master_character_id from \"社交\".sect where id = $1", connection);
        head.Parameters.AddWithValue(sectId);
        var masterObj = await head.ExecuteScalarAsync(stopping);
        if (masterObj is null or DBNull)
        {
            return new Roster(false, sectId, Array.Empty<RosterMember>());
        }

        var master = Convert.ToInt64(masterObj);
        var rows = new List<RosterMember>();
        var seenMaster = false;
        await using (var read = new NpgsqlCommand(
            "select character_id, position from \"社交\".sect_member "
            + "where sect_id = $1 order by character_id", connection))
        {
            read.Parameters.AddWithValue(sectId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                var id = reader.GetInt64(0);
                if (id == master)
                {
                    seenMaster = true;
                }

                rows.Add(new RosterMember(id, reader.GetString(1)));
            }
        }

        if (!seenMaster)
        {
            rows.Add(new RosterMember(master, "master"));
            rows.Sort((a, b) => a.CharacterId.CompareTo(b.CharacterId));
        }

        return new Roster(true, sectId, rows);
    }

    /// <summary>
    /// P2-1c：只读查一个角色当前归属的宗门编号。建宗门的人不进名册
    /// （册里只有弟子），所以判定要同时看 sect_member.sect_id 与
    /// sect.master_character_id：任一条命中就算在这个宗门。只回宗门编号，
    /// 不写库。角色服据此判「整队是否同宗门」。
    /// </summary>
    public sealed record CharacterSect(bool Found, long SectId);

    public async Task<CharacterSect> CharacterSectAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select sm.sect_id from \"社交\".sect_member sm where sm.character_id = $1 "
            + "union "
            + "select s.id from \"社交\".sect s where s.master_character_id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (await reader.ReadAsync(stopping))
        {
            return new CharacterSect(true, reader.GetInt64(0));
        }

        return new CharacterSect(false, 0);
    }
}
