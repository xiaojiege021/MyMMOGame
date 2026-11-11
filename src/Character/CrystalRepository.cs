using Npgsql;

namespace Character;

/// <summary>
/// 一次装上 / 拆下的结果。Reason 为空表示这一笔做成了；Duplicate=true 表示
/// 事务号用过、一笔没做。装上和拆下都不掷点、不扣金币，也没有「部分生效」。
/// </summary>
public sealed record CrystalOutcome(
    bool Duplicate,
    string Reason,
    bool Installed,
    int Slot,
    int Socket,
    string CrystalItem,
    string Category,
    string Part,
    int FilledBefore,
    int Filled,
    int CrystalLeft,
    long Gold,
    int EnhanceLevel,
    bool Bound);

/// <summary>只给核对用：不看回包，从库里独立读回镶嵌状态、结晶余量、金币、强化与绑定。</summary>
public sealed record CrystalState(
    int Filled, string Installed, int CrystalLeft, long Gold, int EnhanceLevel, bool Bound);

/// <summary>
/// 能量结晶装上 / 拆下是角色服的一笔数据库事务。方案第 9 章写定的规则全在这里判：
/// 攻击 / 防御 / 功能三类按部位限定（部位不对回 wrong_part）、装上后可拆下、
/// 拆下把结晶放回背包复用；9.1 写定结晶不改制作时掷定的基础属性，所以这里
/// 一行都不碰强化等级、绑定、金币和装备本身。
/// 类别、部位、固定值、镶嵌位数量都只在服务端从配置读，请求里带不进来。
/// </summary>
public sealed class CrystalRepository(
    NpgsqlDataSource dataSource,
    CrystalTable table,
    ItemTable items,
    CharacterConfig config)
{
    public async Task<CrystalOutcome> InstallAsync(
        long characterId,
        int slot,
        int crystalSlot,
        int socket,
        string txnId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await ClaimAsync(connection, transaction, characterId, slot, socket,
                "install", txnId, stopping))
        {
            await transaction.RollbackAsync(stopping);
            var state = await StateAsync(characterId, slot, "", CancellationToken.None);
            return Empty(true, "duplicate", true, slot, socket, state);
        }

        // 镶嵌位号由配置决定，服务端校验；客户端不能指定库里不存在的位。
        if (socket < 1 || socket > table.SocketCount)
        {
            throw new EnhanceException("invalid_socket");
        }

        var rows = await ReadInventoryAsync(connection, transaction, characterId,
            [slot, crystalSlot], stopping);
        // reader 关掉之后再判业务规则：Npgsql 里带着未读完的 reader 回滚会炸成 gRPC 502。

        if (!rows.TryGetValue(slot, out var equipment))
        {
            throw new EnhanceException("slot_empty");
        }

        if (items.Find(equipment.ItemId) is not { } equipItem
            || !string.Equals(equipItem.Kind, "equipment", StringComparison.Ordinal))
        {
            throw new EnhanceException("not_equipment");
        }

        if (!rows.TryGetValue(crystalSlot, out var crystalRow))
        {
            throw new EnhanceException("crystal_slot_empty");
        }

        var def = table.Find(crystalRow.ItemId);
        if (def is null || !string.Equals(items.Find(crystalRow.ItemId)?.Kind, "crystal", StringComparison.Ordinal))
        {
            throw new EnhanceException("not_crystal");
        }

        var part = table.PartOf(equipment.ItemId)
            ?? throw new EnhanceException("unknown_part");
        if (!table.Allows(def.Category, part))
        {
            // 第 14 节第 38 行的完成标准：部位不对的结晶被拒绝，且装备与背包都不动。
            throw new EnhanceException("wrong_part");
        }

        if (await SocketItemAsync(connection, transaction, characterId, slot, socket, stopping) is not null)
        {
            throw new EnhanceException("socket_occupied");
        }

        // 写入前先记下已装数量，回包里的 filled_before 才是真正的「装之前」。
        var filledBefore = await FilledAsync(connection, transaction, characterId, slot, stopping);

        // 扣一颗结晶。inventory.count 有 CHECK (count > 0)：扣到 0 要先删行，
        // 不能先 update 成 0 再删（强化 / 转移两处都踩过这个坑）。
        if (crystalRow.Count == 1)
        {
            await using var remove = new NpgsqlCommand(
                "delete from \"角色\".inventory where character_id = $1 and slot = $2",
                connection, transaction);
            remove.Parameters.AddWithValue(characterId);
            remove.Parameters.AddWithValue((short)crystalSlot);
            await remove.ExecuteNonQueryAsync(stopping);
        }
        else
        {
            await using var consume = new NpgsqlCommand(
                "update \"角色\".inventory set count = count - 1 "
                + "where character_id = $1 and slot = $2",
                connection, transaction);
            consume.Parameters.AddWithValue(characterId);
            consume.Parameters.AddWithValue((short)crystalSlot);
            await consume.ExecuteNonQueryAsync(stopping);
        }

        // 装上时把这颗结晶的绑定状态一起记下，拆下时原样还回去，不让绑定凭空消失。
        await using (var install = new NpgsqlCommand(
            "insert into \"角色\".crystal_socket "
            + "(character_id, slot, socket, crystal_item_id, bound, bound_at) "
            + "values ($1, $2, $3, $4, $5, $6)",
            connection, transaction))
        {
            install.Parameters.AddWithValue(characterId);
            install.Parameters.AddWithValue((short)slot);
            install.Parameters.AddWithValue((short)socket);
            install.Parameters.AddWithValue(def.Id);
            install.Parameters.AddWithValue(crystalRow.Bound);
            if (crystalRow.Bound)
            {
                install.Parameters.AddWithValue(crystalRow.BoundAt ?? DateTimeOffset.UtcNow);
            }
            else
            {
                install.Parameters.AddWithValue(DBNull.Value);
            }
            await install.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        var after = await StateAsync(characterId, slot, def.Id, CancellationToken.None);

        return new CrystalOutcome(false, "", true, slot, socket, def.Id, def.Category, part,
            filledBefore, after.Filled, after.CrystalLeft, after.Gold,
            after.EnhanceLevel, after.Bound);
    }

    public async Task<CrystalOutcome> RemoveAsync(
        long characterId,
        int slot,
        int socket,
        string txnId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await ClaimAsync(connection, transaction, characterId, slot, socket,
                "remove", txnId, stopping))
        {
            await transaction.RollbackAsync(stopping);
            var state = await StateAsync(characterId, slot, "", CancellationToken.None);
            return Empty(true, "duplicate", false, slot, socket, state);
        }

        if (socket < 1 || socket > table.SocketCount)
        {
            throw new EnhanceException("invalid_socket");
        }

        var rows = await ReadInventoryAsync(connection, transaction, characterId, [slot], stopping);
        if (!rows.TryGetValue(slot, out var equipment))
        {
            throw new EnhanceException("slot_empty");
        }

        if (items.Find(equipment.ItemId) is not { } equipItem
            || !string.Equals(equipItem.Kind, "equipment", StringComparison.Ordinal))
        {
            throw new EnhanceException("not_equipment");
        }

        // 锁住要拆的这一行，同时读出结晶编号与装上时的绑定状态。
        string crystalItemId;
        var bound = false;
        DateTimeOffset? boundAt = null;
        await using (var read = new NpgsqlCommand(
            "select crystal_item_id, bound, bound_at from \"角色\".crystal_socket "
            + "where character_id = $1 and slot = $2 and socket = $3 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue((short)slot);
            read.Parameters.AddWithValue((short)socket);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                throw new EnhanceException("socket_empty");
            }

            crystalItemId = reader.GetString(0);
            bound = reader.GetBoolean(1);
            if (!await reader.IsDBNullAsync(2, stopping))
            {
                boundAt = reader.GetFieldValue<DateTimeOffset>(2);
            }
        }

        var def = table.Find(crystalItemId)
            ?? throw new EnhanceException("unknown_crystal");
        var part = table.PartOf(equipment.ItemId)
            ?? throw new EnhanceException("unknown_part");

        // 删除前记下已装数量，回包里的 filled_before 才是真正的「拆之前」。
        var filledBefore = await FilledAsync(connection, transaction, characterId, slot, stopping);

        await using (var clear = new NpgsqlCommand(
            "delete from \"角色\".crystal_socket "
            + "where character_id = $1 and slot = $2 and socket = $3",
            connection, transaction))
        {
            clear.Parameters.AddWithValue(characterId);
            clear.Parameters.AddWithValue((short)slot);
            clear.Parameters.AddWithValue((short)socket);
            await clear.ExecuteNonQueryAsync(stopping);
        }

        // 拆卸带走：优先叠回已有同编号且未堆满的格子，否则找一个最小空格。
        // 背包满了整笔回滚，结晶既不消失也不留在装备上。
        var stackMax = items.Find(def.Id)?.StackMax ?? 1;
        var targetSlot = await FindStackSlotAsync(connection, transaction, characterId,
            def.Id, stackMax, stopping);
        if (targetSlot is null)
        {
            targetSlot = await FindEmptySlotAsync(connection, transaction, characterId, stopping);
        }

        if (targetSlot is null)
        {
            throw new EnhanceException("bag_full");
        }

        if (await ItemCountAtAsync(connection, transaction, characterId, targetSlot.Value,
                def.Id, stopping) is not null)
        {
            await using var stack = new NpgsqlCommand(
                "update \"角色\".inventory set count = count + 1 "
                + "where character_id = $1 and slot = $2",
                connection, transaction);
            stack.Parameters.AddWithValue(characterId);
            stack.Parameters.AddWithValue((short)targetSlot.Value);
            await stack.ExecuteNonQueryAsync(stopping);
        }
        else
        {
            await using (var give = new NpgsqlCommand(
                "insert into \"角色\".inventory "
                + "(character_id, slot, item_id, count, bound, bound_at) "
                + "values ($1, $2, $3, 1, $4, $5)",
                connection, transaction))
            {
                give.Parameters.AddWithValue(characterId);
                give.Parameters.AddWithValue((short)targetSlot.Value);
                give.Parameters.AddWithValue(def.Id);
                give.Parameters.AddWithValue(bound);
                if (bound)
                {
                    give.Parameters.AddWithValue(boundAt ?? DateTimeOffset.UtcNow);
                }
                else
                {
                    give.Parameters.AddWithValue(DBNull.Value);
                }
                await give.ExecuteNonQueryAsync(stopping);
            }
        }

        await transaction.CommitAsync(stopping);
        var after = await StateAsync(characterId, slot, def.Id, CancellationToken.None);

        return new CrystalOutcome(false, "", false, slot, socket, def.Id, def.Category, part,
            filledBefore, after.Filled, after.CrystalLeft, after.Gold,
            after.EnhanceLevel, after.Bound);
    }

    static CrystalOutcome Empty(bool duplicate, string reason, bool installed,
        int slot, int socket, CrystalState state) =>
        new(duplicate, reason, installed, slot, socket, "", "", "",
            0, state.Filled, state.CrystalLeft, state.Gold, state.EnhanceLevel, state.Bound);

    static async Task<bool> ClaimAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, int slot, int socket, string action, string txnId,
        CancellationToken stopping)
    {
        await using var claim = new NpgsqlCommand(
            "insert into \"角色\".crystal_txn (txn_id, character_id, slot, socket, action) "
            + "values ($1, $2, $3, $4, $5) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction);
        claim.Parameters.AddWithValue(txnId);
        claim.Parameters.AddWithValue(characterId);
        claim.Parameters.AddWithValue((short)slot);
        claim.Parameters.AddWithValue((short)socket);
        claim.Parameters.AddWithValue(action);
        return await claim.ExecuteScalarAsync(stopping) is not null;
    }

    sealed record InventoryRow(string ItemId, int Count, bool Bound, DateTimeOffset? BoundAt);

    /// <summary>按格位升序一次锁住这些格，避免两笔反方向操作互相等锁。</summary>
    static async Task<Dictionary<int, InventoryRow>> ReadInventoryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, int[] slots, CancellationToken stopping)
    {
        var ordered = slots.Distinct().OrderBy(s => s).ToArray();
        var rows = new Dictionary<int, InventoryRow>();
        await using var read = new NpgsqlCommand(
            "select slot, item_id, count, bound, bound_at from \"角色\".inventory "
            + "where character_id = $1 and slot = any($2) order by slot for update",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(ordered.Select(s => (short)s).ToArray());
        await using var reader = await read.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            DateTimeOffset? at = null;
            if (!await reader.IsDBNullAsync(4, stopping))
            {
                at = reader.GetFieldValue<DateTimeOffset>(4);
            }

            rows[reader.GetInt16(0)] = new InventoryRow(
                reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3), at);
        }

        return rows;
    }

    static async Task<string?> SocketItemAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, int slot, int socket, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select crystal_item_id from \"角色\".crystal_socket "
            + "where character_id = $1 and slot = $2 and socket = $3",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue((short)slot);
        read.Parameters.AddWithValue((short)socket);
        return await read.ExecuteScalarAsync(stopping) as string;
    }

    static async Task<int> FilledAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, int slot, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select count(*) from \"角色\".crystal_socket "
            + "where character_id = $1 and slot = $2",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue((short)slot);
        return Convert.ToInt32(await read.ExecuteScalarAsync(stopping));
    }

    static async Task<int?> FindStackSlotAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, string itemId, int stackMax, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select slot from \"角色\".inventory "
            + "where character_id = $1 and item_id = $2 and count < $3 and bound = false "
            + "order by slot limit 1 for update",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(itemId);
        read.Parameters.AddWithValue(stackMax);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null ? null : Convert.ToInt32(value);
    }

    async Task<int?> FindEmptySlotAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select min(g) from generate_series(1, $2) g "
            + "where not exists (select 1 from \"角色\".inventory i "
            + "where i.character_id = $1 and i.slot = g)",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue(config.BagSlots);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    static async Task<int?> ItemCountAtAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, int slot, string itemId, CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select count from \"角色\".inventory "
            + "where character_id = $1 and slot = $2 and item_id = $3",
            connection, transaction);
        read.Parameters.AddWithValue(characterId);
        read.Parameters.AddWithValue((short)slot);
        read.Parameters.AddWithValue(itemId);
        var value = await read.ExecuteScalarAsync(stopping);
        return value is null ? null : Convert.ToInt32(value);
    }

    /// <summary>只给核对用：不看回包，从库里独立读回镶嵌状态与几个不该被改动的字段。</summary>
    public async Task<CrystalState> StateAsync(
        long characterId, int slot, string crystalItemId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        return await ReadStateAsync(connection, null, characterId, slot, crystalItemId, stopping);
    }

    async Task<CrystalState> ReadStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        long characterId, int slot, string crystalItemId, CancellationToken stopping)
    {
        // 定长输出：没装的位写 "-"，脚本不用猜这一格有没有值。
        var installed = new string[table.SocketCount];
        for (var i = 0; i < installed.Length; i++)
        {
            installed[i] = (i + 1) + ":-";
        }

        var filled = 0;
        await using (var read = new NpgsqlCommand(
            "select socket, crystal_item_id from \"角色\".crystal_socket "
            + "where character_id = $1 and slot = $2 order by socket",
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue((short)slot);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                var socket = reader.GetInt16(0);
                if (socket >= 1 && socket <= installed.Length)
                {
                    installed[socket - 1] = socket + ":" + reader.GetString(1);
                    filled++;
                }
            }
        }

        var left = 0;
        if (crystalItemId.Length > 0)
        {
            await using var count = new NpgsqlCommand(
                "select coalesce(sum(count), 0) from \"角色\".inventory "
                + "where character_id = $1 and item_id = $2",
                connection, transaction);
            count.Parameters.AddWithValue(characterId);
            count.Parameters.AddWithValue(crystalItemId);
            left = Convert.ToInt32(await count.ExecuteScalarAsync(stopping));
        }

        long gold = 0;
        await using (var money = new NpgsqlCommand(
            "select coalesce(sum(amount), 0) from \"角色\".currency "
            + "where character_id = $1 and kind = 'gold'",
            connection, transaction))
        {
            money.Parameters.AddWithValue(characterId);
            gold = Convert.ToInt64(await money.ExecuteScalarAsync(stopping));
        }

        var level = 0;
        var bound = false;
        await using (var equip = new NpgsqlCommand(
            "select i.bound, coalesce(e.level, 0) from \"角色\".inventory i "
            + "left join \"角色\".enhance e on e.character_id = i.character_id and e.slot = i.slot "
            + "where i.character_id = $1 and i.slot = $2",
            connection, transaction))
        {
            equip.Parameters.AddWithValue(characterId);
            equip.Parameters.AddWithValue((short)slot);
            await using var reader = await equip.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                bound = reader.GetBoolean(0);
                level = reader.GetInt16(1);
            }
        }

        return new CrystalState(filled, string.Join(",", installed), left, gold, level, bound);
    }

    /// <summary>只给核对用：这个结晶事务号在表里有几行。</summary>
    public async Task<int> TxnCountAsync(string txnId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select count(*) from \"角色\".crystal_txn where txn_id = $1");
        command.Parameters.AddWithValue(txnId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(stopping));
    }
}
