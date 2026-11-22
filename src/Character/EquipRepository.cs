using Npgsql;

namespace Character;

/// <summary>
/// 穿戴一笔的结果。Reason 空串表示做成了；ItemId / Slot / Attack 是做完之后的事实值，
/// 被拒时也给当时的事实（等级、职业），屏上不用再猜。
/// </summary>
public sealed record EquipOutcome(
    bool Found,
    string Reason,
    string ItemId,
    string Slot,
    long Attack,
    int Level,
    string ClassId,
    int BagSlot);

/// <summary>
/// B10：已穿装备的三样事实值，一次查询读完。战斗快照与只读核对口都读这一个形状；
/// 折算成伤害系数 / 万分比是调用方的事，仓储只做加法。
/// </summary>
public sealed record WornTotals(long Attack, long SetBonus, int EnhanceTotal, long CrystalFlatAttack)
{
    /// <summary>进伤害的那一截：装备固定攻击 + 套装加成。强化与结晶另算，不在这一格里。</summary>
    public long TotalAttack => Attack + SetBonus;
}

/// <summary>
/// 穿戴的唯一写入口。一条记录 = 「这个角色这个部位穿的是哪一行背包」，
/// 背包行本身不动（不删行、不改格号），所以：
///   · 强化（C4 的 enhance）与结晶（crystal_socket）仍挂在那口背包行上，穿上、脱下都不搬；
///   · 卖出 / 丢弃 / 合并删行时，character_equip 靠外键 ON DELETE CASCADE 一起清掉，
///     槽位不会指向一件已经不存在的装备，强化也不会留在原格子里（行没了，等级跟着走）。
/// 同部位只许一件由主键 (character_id, slot) 保证；先查一次给可读的拒绝码，再让主键兜底。
/// 装备固定攻击（方案 7.2 第一段）在这里按已穿装备求和：数值只在 config/items.json，
/// 角色服只做加法，不落第二份属性。
/// </summary>
public sealed class EquipRepository(NpgsqlDataSource dataSource)
{
    // ------------------------------------------------------------------ 穿

    /// <summary>
    /// 把某一格背包行穿上。部位、等级门槛、职业锁全部从物品表与库里读，
    /// 客户端只给格号。account 为空表示服间可信调用，只按角色编号核存在。
    /// </summary>
    public async Task<EquipOutcome> EquipAsync(
        long characterId,
        string? account,
        int bagSlot,
        ItemTable items,
        BranchTable branches,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        var who = await LockCharacterAsync(connection, transaction, characterId, account, stopping);
        if (who is null)
        {
            await transaction.RollbackAsync(stopping);
            return new EquipOutcome(false, "character_not_found", "", "", 0, 0, "", bagSlot);
        }

        var (level, classId) = who.Value;
        var row = await LockBagSlotAsync(connection, transaction, characterId, bagSlot, stopping);
        if (row is null)
        {
            await transaction.RollbackAsync(stopping);
            return new EquipOutcome(true, "slot_empty", "", "", 0, level, classId, bagSlot);
        }

        var (inventoryId, itemId) = row.Value;
        var item = items.Find(itemId);
        if (item is null)
        {
            await transaction.RollbackAsync(stopping);
            return new EquipOutcome(true, "item_not_found", itemId, "", 0, level, classId, bagSlot);
        }

        var reason = EquipRules.Check(item, level, classId, branches);
        if (reason.Length > 0)
        {
            // 先读出现值再回滚：被拒时屏上仍要知道现在穿着什么、多高的装备攻击。
            var rejected = await AttackAsync(connection, transaction, characterId, items, stopping);
            await transaction.RollbackAsync(stopping);
            return new EquipOutcome(true, reason, itemId, item.Slot, rejected, level, classId, bagSlot);
        }

        // 同部位已经穿着一件：这一件就是它，回 already_equipped（重复点不报错也不重复写）；
        // 换另一件就回 slot_occupied，先脱再穿——不替玩家把旧的退回去。
        var worn = await WornAsync(connection, transaction, characterId, item.Slot, stopping);
        if (worn is not null)
        {
            var already = worn.Value == inventoryId;
            var kept = await AttackAsync(connection, transaction, characterId, items, stopping);
            await transaction.RollbackAsync(stopping);
            return new EquipOutcome(
                true, already ? "already_equipped" : "slot_occupied", itemId, item.Slot,
                kept, level, classId, bagSlot);
        }

        try
        {
            await using var insert = new NpgsqlCommand(
                "insert into \"角色\".character_equip (character_id, slot, inventory_id) values ($1, $2, $3)",
                connection, transaction);
            insert.Parameters.AddWithValue(characterId);
            insert.Parameters.AddWithValue(item.Slot);
            insert.Parameters.AddWithValue(inventoryId);
            await insert.ExecuteNonQueryAsync(stopping);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 主键兜底：并发下同一个部位被抢先穿上时，这一笔整笔不做。
            await transaction.RollbackAsync(stopping);
            return new EquipOutcome(true, "slot_occupied", itemId, item.Slot, 0, level, classId, bagSlot);
        }

        var attack = await AttackAsync(connection, transaction, characterId, items, stopping);
        await transaction.CommitAsync(stopping);
        return new EquipOutcome(true, "", itemId, item.Slot, attack, level, classId, bagSlot);
    }

    // ------------------------------------------------------------------ 脱

    /// <summary>
    /// 按部位脱。背包行一直没动过，所以脱下只是删掉这一条记录，格号就是它原来那一格。
    /// </summary>
    public async Task<EquipOutcome> UnequipAsync(
        long characterId,
        string? account,
        string slot,
        ItemTable items,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        var who = await LockCharacterAsync(connection, transaction, characterId, account, stopping);
        if (who is null)
        {
            await transaction.RollbackAsync(stopping);
            return new EquipOutcome(false, "character_not_found", "", slot, 0, 0, "", 0);
        }

        var (level, classId) = who.Value;
        long inventoryId;
        await using (var drop = new NpgsqlCommand(
            "delete from \"角色\".character_equip where character_id = $1 and slot = $2 returning inventory_id",
            connection, transaction))
        {
            drop.Parameters.AddWithValue(characterId);
            drop.Parameters.AddWithValue(slot);
            var returned = await drop.ExecuteScalarAsync(stopping);
            if (returned is not long id)
            {
                var kept = await AttackAsync(connection, transaction, characterId, items, stopping);
                await transaction.RollbackAsync(stopping);
                return new EquipOutcome(true, "slot_empty", "", slot, kept, level, classId, 0);
            }

            inventoryId = id;
        }

        var bagSlot = await BagSlotOfAsync(connection, transaction, inventoryId, stopping);
        var itemId = bagSlot > 0 ? await ItemOfAsync(connection, transaction, inventoryId, stopping) : "";
        var attack = await AttackAsync(connection, transaction, characterId, items, stopping);
        await transaction.CommitAsync(stopping);
        return new EquipOutcome(true, "", itemId, slot, attack, level, classId, bagSlot);
    }

    // ------------------------------------------------------------------ 只读

    /// <summary>全身已穿装备的固定攻击之和。战斗服的副本伤害与客户端的穿戴屏都读这一个数；
    /// 数值只来自 config/items.json 的 attack，这里只做加法。
    /// </summary>
    public async Task<long> EquipAttackAsync(long characterId, ItemTable items, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        return await AttackAsync(connection, null, characterId, items, stopping);
    }

    /// <summary>
    /// B10：一次读完已穿装备的三样事实——固定攻击之和、强化等级之和、攻击类结晶固定值之和。
    /// 战斗快照（DescribeBattleProfile）只走这一个口，免得三次连接读到三个时刻的库。
    /// 数值仍各只有一处正本：装备攻击与强化等级来自 items.json / enhance（库里的等级），
    /// 结晶固定值与类别来自 config/crystal.json；这里只做加法，不折算比例、不写配置默认值。
    /// 只统计 category=attack 的结晶：方案 7.2 第二段只有「能量结晶固定值」一项是攻击，
    /// 防御 / 功能两类不进伤害公式。
    /// </summary>
    public async Task<WornTotals> WornTotalsAsync(
        long characterId, ItemTable items, CrystalTable crystals, SetTable sets, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        // 一件装备一行；装了结晶的装备会因为 left join 出现多行，所以装备那一截按格号去重。
        // 强化等级挂在背包行 id 上（C4），结晶挂在格子上（P1-4d 的 (角色, 格, 镶嵌位)）。
        await using var command = new NpgsqlCommand(
            "select i.slot, i.item_id, coalesce(e.level, 0)::int, cs.crystal_item_id "
            + "from \"角色\".character_equip ce "
            + "join \"角色\".inventory i on i.id = ce.inventory_id "
            + "left join \"角色\".enhance e on e.inventory_id = i.id "
            + "left join \"角色\".crystal_socket cs on cs.character_id = ce.character_id and cs.slot = i.slot "
            + "where ce.character_id = $1 order by i.slot, cs.socket",
            connection);
        command.Parameters.AddWithValue(characterId);

        await using var reader = await command.ExecuteReaderAsync(stopping);
        var attack = 0L;
        var enhanceTotal = 0;
        var crystalFlat = 0L;
        var counted = new HashSet<short>();
        // B2：套装效果按「现在穿着几件」现数，不落库、不记历史。同一部位只算一件
        // （character_equip 的主键已经把同部位限成一件），脱下这一行就不在结果里。
        var wornBySet = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(stopping))
        {
            var slot = reader.GetInt16(0);
            if (counted.Add(slot))
            {
                var def = items.Find(reader.GetString(1));
                attack += def?.Attack ?? 0;
                enhanceTotal += reader.GetInt32(2);
                if (def is not null && def.SetId.Length > 0)
                {
                    wornBySet[def.SetId] = wornBySet.GetValueOrDefault(def.SetId) + 1;
                }
            }

            if (reader.IsDBNull(3))
            {
                continue;
            }

            var crystal = crystals.Find(reader.GetString(3));
            if (crystal is not null && string.Equals(crystal.Category, "attack", StringComparison.Ordinal))
            {
                crystalFlat += crystal.FixedValue;
            }
        }

        return new WornTotals(attack, sets.BonusFor(wornBySet), enhanceTotal, crystalFlat);
    }

    /// <summary>
    /// 背包格号 → 部位。背包只读回包与屏都用它标「这一件正穿在身上」。
    /// 只查已有记录，不写库。
    /// </summary>
    public async Task<Dictionary<int, string>> EquippedByBagSlotAsync(
        long characterId, CancellationToken stopping)
    {
        var map = new Dictionary<int, string>();
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var command = new NpgsqlCommand(
            "select i.slot, ce.slot from \"角色\".character_equip ce "
            + "join \"角色\".inventory i on i.id = ce.inventory_id where ce.character_id = $1",
            connection);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            map[reader.GetInt16(0)] = reader.GetString(1);
        }

        return map;
    }

    // ------------------------------------------------------------------ 内部

    /// <summary>锁角色行并读出等级与职业：穿着门槛的判定输入只从库里来。</summary>
    static async Task<(int Level, string ClassId)?> LockCharacterAsync(
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
            "select level, class_id from \"角色\".character " + owned, connection, transaction);
        command.Parameters.AddWithValue(characterId);
        if (!string.IsNullOrEmpty(account))
        {
            command.Parameters.AddWithValue(account);
        }

        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetString(1));
    }

    /// <summary>锁这一格背包行，返回 (背包行 id, 物品编号)。空格返回 null。</summary>
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

    /// <summary>这个部位现在穿着哪一行背包。没穿返回 null。</summary>
    static async Task<long?> WornAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        string slot,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select inventory_id from \"角色\".character_equip where character_id = $1 and slot = $2",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(slot);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is long id ? id : null;
    }

    /// <summary>已穿装备的固定攻击之和。装备行被删的瞬间就查不到了（外键 CASCADE）。</summary>
    static async Task<long> AttackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long characterId,
        ItemTable items,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select i.item_id from \"角色\".character_equip ce "
            + "join \"角色\".inventory i on i.id = ce.inventory_id where ce.character_id = $1",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        var total = 0L;
        while (await reader.ReadAsync(stopping))
        {
            total += items.Find(reader.GetString(0))?.Attack ?? 0;
        }

        return total;
    }

    static async Task<int> BagSlotOfAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inventoryId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select slot from \"角色\".inventory where id = $1", connection, transaction);
        command.Parameters.AddWithValue(inventoryId);
        var value = await command.ExecuteScalarAsync(stopping);
        return value is short slot ? slot : 0;
    }

    static async Task<string> ItemOfAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inventoryId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select item_id from \"角色\".inventory where id = $1", connection, transaction);
        command.Parameters.AddWithValue(inventoryId);
        var value = await command.ExecuteScalarAsync(stopping);
        return value as string ?? "";
    }
}
