using Npgsql;

namespace Character;

/// <summary>骑上 / 下坐骑的结果。ok 是「这次请求成立了」（含业务拒绝），拒绝时 reason 非空。</summary>
public sealed record MountOutcome(
    bool Ok, string Reason, string ItemId, string Tier, int SpeedBps, int BagSlot);

/// <summary>背包里的一只坐骑：编号、名字、档位、加成与所在格。</summary>
public sealed record MountedMount(string ItemId, string Name, string Tier, int SpeedBps, int BagSlot);

/// <summary>当前坐骑与背包里全部坐骑的只读快照。没骑时 Equipped 为 null。</summary>
public sealed record MountSnapshot(
    bool Found, MountedMount? Equipped, IReadOnlyList<MountedMount> Owned);

/// <summary>
/// B4 玩法坐骑。方案 9.4：普通和高级只差移动速度，商城坐骑只换外观、
/// 不叠加速度、不进战斗。所以这里只有「骑哪一行背包」这一件事：
///   · 速度不落库，按 config/mount.json 现算，世界服来问时读的是同一个来源；
///   · 一个角色一行（character_mount 主键），骑着的是背包里的那一行，
///     卖 / 丢 / 合并删行跟着外键 CASCADE 走，不留一条指向空行的坐骑。
/// </summary>
public sealed class MountRepository(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// 骑上某一格背包里的坐骑。account 为空表示服间可信调用，只按角色编号核存在。
    /// 拒绝码：character_not_found / slot_empty / not_a_mount / already_mounted / mount_occupied。
    /// </summary>
    public async Task<MountOutcome> MountAsync(
        long characterId,
        string? account,
        int bagSlot,
        ItemTable items,
        MountTable mounts,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await LockCharacterAsync(connection, transaction, characterId, account, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return new MountOutcome(false, "character_not_found", "", "", 0, bagSlot);
        }

        var row = await LockBagSlotAsync(connection, transaction, characterId, bagSlot, stopping);
        if (row is null)
        {
            await transaction.RollbackAsync(stopping);
            return new MountOutcome(true, "slot_empty", "", "", 0, bagSlot);
        }

        var (inventoryId, itemId) = row.Value;
        var goods = items.Find(itemId);
        var mount = mounts.Find(itemId);
        if (goods is null || mount is null || !string.Equals(goods.Kind, "mount", StringComparison.Ordinal))
        {
            // 灵兽、外观、装备、材料都走这一条：只有 kind=mount 且登记在坐骑表里的能骑。
            await transaction.RollbackAsync(stopping);
            return new MountOutcome(true, "not_a_mount", itemId, "", 0, bagSlot);
        }

        var current = await MountedRowAsync(connection, transaction, characterId, stopping);
        if (current is not null)
        {
            await transaction.RollbackAsync(stopping);
            var already = current.Value == inventoryId;
            return new MountOutcome(
                true, already ? "already_mounted" : "mount_occupied",
                itemId, mount.Tier, mount.SpeedBps, bagSlot);
        }

        try
        {
            await using var insert = new NpgsqlCommand(
                "insert into \"角色\".character_mount (character_id, inventory_id) values ($1, $2)",
                connection, transaction);
            insert.Parameters.AddWithValue(characterId);
            insert.Parameters.AddWithValue(inventoryId);
            await insert.ExecuteNonQueryAsync(stopping);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 主键兜底：并发下同一角色被抢先骑上时，这一笔整笔不做。
            await transaction.RollbackAsync(stopping);
            return new MountOutcome(true, "mount_occupied", itemId, mount.Tier, mount.SpeedBps, bagSlot);
        }

        await transaction.CommitAsync(stopping);
        return new MountOutcome(true, "", itemId, mount.Tier, mount.SpeedBps, bagSlot);
    }

    /// <summary>
    /// 下坐骑。背包行一直没动过，所以这里只是删掉那一条记录。
    /// 没骑时 reason=not_mounted，不算错误。
    /// </summary>
    public async Task<MountOutcome> DismountAsync(
        long characterId,
        string? account,
        MountTable mounts,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await LockCharacterAsync(connection, transaction, characterId, account, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return new MountOutcome(false, "character_not_found", "", "", 0, 0);
        }

        var row = await MountedRowWithSlotAsync(connection, transaction, characterId, stopping);
        if (row is null)
        {
            await transaction.RollbackAsync(stopping);
            return new MountOutcome(true, "not_mounted", "", "", 0, 0);
        }

        var (inventoryId, itemId, bagSlot) = row.Value;
        var mount = mounts.Find(itemId);

        await using (var delete = new NpgsqlCommand(
            "delete from \"角色\".character_mount where character_id = $1 and inventory_id = $2",
            connection, transaction))
        {
            delete.Parameters.AddWithValue(characterId);
            delete.Parameters.AddWithValue(inventoryId);
            await delete.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new MountOutcome(
            true, "", itemId, mount?.Tier ?? "", mount?.SpeedBps ?? 0, (int)bagSlot);
    }

    /// <summary>
    /// 这个世界服来问的那一个数：骑着的坐骑给多少万分比加成。
    /// 没骑、物品不在表里、或那一行已经不是坐骑，一律回 0（当没有加成）。
    /// </summary>
    public async Task<int> SpeedBpsAsync(
        long characterId, MountTable mounts, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var command = new NpgsqlCommand(
            "select i.item_id from \"角色\".character_mount cm "
            + "join \"角色\".inventory i on i.id = cm.inventory_id "
            + "where cm.character_id = $1", connection);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return 0;
        }

        return mounts.SpeedBpsFor(reader.GetString(0));
    }

    /// <summary>读当前坐骑与背包里全部坐骑（屏上「换一只」的候选也来自这一份）。</summary>
    public async Task<MountSnapshot> DescribeAsync(
        long characterId,
        string? account,
        ItemTable items,
        MountTable mounts,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);

        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1"
            : "where id = $1 and account = $2";
        await using (var command = new NpgsqlCommand(
            "select 1 from \"角色\".character " + owned, connection))
        {
            command.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                command.Parameters.AddWithValue(account);
            }

            if (await command.ExecuteScalarAsync(stopping) is null)
            {
                return new MountSnapshot(false, null, []);
            }
        }

        long? mountedRow = null;
        await using (var command = new NpgsqlCommand(
            "select inventory_id from \"角色\".character_mount where character_id = $1", connection))
        {
            command.Parameters.AddWithValue(characterId);
            mountedRow = await command.ExecuteScalarAsync(stopping) as long?;
        }

        var list = new List<MountedMount>();
        MountedMount? equipped = null;
        await using (var command = new NpgsqlCommand(
            "select id, slot, item_id from \"角色\".inventory where character_id = $1 order by slot",
            connection))
        {
            command.Parameters.AddWithValue(characterId);
            await using var reader = await command.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                var inventoryId = reader.GetInt64(0);
                var bagSlot = reader.GetInt16(1);
                var itemId = reader.GetString(2);
                var goods = items.Find(itemId);
                var mount = mounts.Find(itemId);
                if (goods is null || mount is null || !string.Equals(goods.Kind, "mount", StringComparison.Ordinal))
                {
                    continue;
                }

                var entry = new MountedMount(itemId, goods.Name, mount.Tier, mount.SpeedBps, bagSlot);
                list.Add(entry);
                if (mountedRow is not null && mountedRow.Value == inventoryId)
                {
                    equipped = entry;
                }
            }
        }

        return new MountSnapshot(true, equipped, list);
    }

    static async Task<bool> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string? account,
        CancellationToken stopping)
    {
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        await using var command = new NpgsqlCommand(
            "select 1 from \"角色\".character " + owned, connection, transaction);
        command.Parameters.AddWithValue(characterId);
        if (!string.IsNullOrEmpty(account))
        {
            command.Parameters.AddWithValue(account);
        }

        return await command.ExecuteScalarAsync(stopping) is not null;
    }

    static async Task<(long Id, string ItemId)?> LockBagSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        int slot,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select id, item_id from \"角色\".inventory "
            + "where character_id = $1 and slot = $2 for update", connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue((short)slot);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetInt64(0), reader.GetString(1));
    }

    static async Task<long?> MountedRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select inventory_id from \"角色\".character_mount where character_id = $1 for update",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        return await command.ExecuteScalarAsync(stopping) as long?;
    }

    static async Task<(long Id, string ItemId, long BagSlot)?> MountedRowWithSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select cm.inventory_id, i.item_id, i.slot from \"角色\".character_mount cm "
            + "join \"角色\".inventory i on i.id = cm.inventory_id "
            + "where cm.character_id = $1 for update of cm", connection, transaction);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetInt64(0), reader.GetString(1), reader.GetInt16(2));
    }
}
