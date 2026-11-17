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
    /// 服间直连用：没有账号可核的时候，只确认这个角色存在。
    /// 和 OwnsAsync 的区别只在这一个条件上——有账号就必须核归属，
    /// 没账号是战斗服内部调用，按 P1-2 经验结算那条口径走。
    /// </summary>
    public async Task<bool> ExistsAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select 1 from \"角色\".character where id = $1");
        command.Parameters.AddWithValue(characterId);
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

    /// <summary>
    /// 复核归属的同时读出境界与立仙 / 立魔，只给展示名用（C2）。库里的 realm 仍是顺序表
    /// 规范名；这里的 alignment 只决定第十段显示成金仙还是散仙，不参与任何判定。
    /// 和 RealmAsync 同一套归属条件，不是这个账号的角色回 null。
    /// </summary>
    public async Task<(string Realm, string Alignment)?> RealmFactsAsync(
        string account, long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select realm, coalesce(alignment, '') from \"角色\".character "
            + "where id = $1 and account = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(account);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        return await reader.ReadAsync(stopping)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
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

    /// <summary>
    /// 归属与等级一次读出，只给 K3 的配方目录标「解锁 / 未解锁」用。
    /// 不是这个账号的角色回 null，和 DisplayNameAsync 同一套归属条件。
    /// </summary>
    public async Task<int?> LevelAsync(string account, long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select level from \"角色\".character where id = $1 and account = $2");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(account);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        return await reader.ReadAsync(stopping) ? reader.GetInt32(0) : null;
    }

    /// <summary>
    /// K3：在一笔已打开的事务里扣掉若干件同一种物品，跨格扣、扣到 0 就删行。
    /// 返回 false 表示总数不够——这时**一行都没改**，事务由调用方整笔回滚
    /// （材料、费用、活三样要么全有、要么全无）。
    /// 先锁这一物品的全部格再算总数，同角色的两笔制作不会各自按旧数量通过。
    /// </summary>
    public async Task<bool> ConsumeInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string itemId,
        int count,
        CancellationToken stopping)
    {
        if (count < 1)
        {
            return true;
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

        var total = 0;
        foreach (var row in rows)
        {
            total += row.Count;
        }

        if (total < count)
        {
            return false;
        }

        var left = count;
        foreach (var row in rows)
        {
            if (left == 0)
            {
                break;
            }

            var take = Math.Min(left, row.Count);
            if (take == row.Count)
            {
                // inventory.count 有 CHECK (count > 0)：扣光必须先删行。
                await using var remove = new NpgsqlCommand(
                    "delete from \"角色\".inventory where character_id = $1 and slot = $2",
                    connection, transaction);
                remove.Parameters.AddWithValue(characterId);
                remove.Parameters.AddWithValue(row.Slot);
                await remove.ExecuteNonQueryAsync(stopping);
            }
            else
            {
                await using var consume = new NpgsqlCommand(
                    "update \"角色\".inventory set count = count - $3 "
                    + "where character_id = $1 and slot = $2",
                    connection, transaction);
                consume.Parameters.AddWithValue(characterId);
                consume.Parameters.AddWithValue(row.Slot);
                consume.Parameters.AddWithValue(take);
                await consume.ExecuteNonQueryAsync(stopping);
            }

            left -= take;
        }

        return true;
    }
}
