using Npgsql;

namespace Character;

/// <summary>背包的一格。</summary>
public sealed record BagSlot(int Slot, string ItemId, int Count, bool Bound);

/// <summary>
/// 背包的读写。绑定规则由物品表决定，写库时落 bound 与 bound_at；
/// 交易判定只看 bound，绑定就不给直接交易。
/// </summary>
public sealed class InventoryRepository(NpgsqlDataSource dataSource)
{
    /// <summary>角色是不是这个账号的。不是就按不存在处理，不泄漏别人的角色。</summary>
    public async Task<bool> OwnsAsync(string account, long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select 1 from \"角色\".character where id = $1 and account = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(account);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        return await reader.ReadAsync(stopping);
    }

    /// <summary>
    /// 复核归属的同时读出角色名，只给表世界的名字牌用。不是这个账号的角色回 null，
    /// 和 OwnsAsync 一样按不存在处理，不泄漏别人的名字。
    /// </summary>
    public async Task<string?> DisplayNameAsync(string account, long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select name from \"角色\".character where id = $1 and account = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(account);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        return await reader.ReadAsync(stopping) ? reader.GetString(0) : null;
    }

    /// <summary>
    /// 复核归属的同时读出当前境界，只给表世界的飞行与跨区判定用。和 DisplayNameAsync
    /// 同一套归属条件，不是这个账号的角色回 null。这里只读，不写境界；
    /// 突破与版本开关在后面的卡，角色服仍是境界唯一写入方。
    /// </summary>
    public async Task<string?> RealmAsync(string account, long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select realm from \"角色\".character where id = $1 and account = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(account);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        return await reader.ReadAsync(stopping) ? reader.GetString(0) : null;
    }

    public async Task<BagSlot?> GetSlotAsync(long characterId, int slot, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select slot, item_id, count, bound from \"角色\".inventory "
            + "where character_id = $1 and slot = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue((short)slot);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return new BagSlot(reader.GetInt16(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3));
    }

    public async Task<List<BagSlot>> ListAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select slot, item_id, count, bound from \"角色\".inventory "
            + "where character_id = $1 order by slot");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);

        var list = new List<BagSlot>();
        while (await reader.ReadAsync(stopping))
        {
            list.Add(new BagSlot(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetBoolean(3)));
        }

        return list;
    }

    /// <summary>
    /// 往一格放东西。同格同物品在堆叠上限内累加；绑定状态不动（已经绑的不会因为再放而变回不绑）。
    /// 同格不同物品由调用方先挡掉。
    /// </summary>
    public async Task<BagSlot> AddAsync(
        long characterId,
        int slot,
        ItemDef item,
        int count,
        CancellationToken stopping)
    {
        var bound = item.BindsOnArrival;

        const string sql = """
            insert into "角色".inventory (character_id, slot, item_id, count, bound, bound_at)
            values ($1, $2, $3, $4, $5, case when $5 then now() else null end)
            on conflict (character_id, slot) do update
                set count = "角色".inventory.count + excluded.count
            returning slot, item_id, count, bound
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue((short)slot);
        command.Parameters.AddWithValue(item.Id);
        command.Parameters.AddWithValue(count);
        command.Parameters.AddWithValue(bound);

        await using var reader = await command.ExecuteReaderAsync(stopping);
        await reader.ReadAsync(stopping);
        return new BagSlot(reader.GetInt16(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3));
    }

    /// <summary>只给核对用：绑定时刻是不是跟着 bound 一起落对了。</summary>
    public async Task<(bool Bound, bool BoundAtPresent)?> BindingOfAsync(
        string characterName, int slot, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select i.bound, i.bound_at is not null from \"角色\".inventory i "
            + "join \"角色\".character c on c.id = i.character_id "
            + "where c.name = $1 and i.slot = $2");
        command.Parameters.AddWithValue(characterName);
        command.Parameters.AddWithValue((short)slot);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }
}
