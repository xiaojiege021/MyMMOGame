using Npgsql;

namespace Character;

/// <summary>
/// A9：一个角色手动加到四属性上的点数（没有加点行时四条都是 0）。
/// 与职业表的基础值相加才是「有效属性」，装备门槛与战斗属性都读这个和。
/// </summary>
public readonly record struct AttrAllocation(int Strength, int Agility, int Spirit, int Vitality)
{
    public static readonly AttrAllocation None = default;

    public long Get(string key) => key switch
    {
        "strength" => Strength,
        "agility" => Agility,
        "spirit" => Spirit,
        "vitality" => Vitality,
        _ => 0,
    };

    public bool IsZero => Strength == 0 && Agility == 0 && Spirit == 0 && Vitality == 0;
}

/// <summary>
/// 加点只读快照：手上还剩多少点（character.attr_points）与四条属性各加了多少。
/// 屏上「基础 + 加点」由调用方按职业表相加；角色服不在这里合成第二份属性数值。
/// </summary>
public sealed record AttrPointSnapshot(
    bool Found,
    string ClassId,
    int Level,
    int Remaining,
    AttrAllocation Allocated);

/// <summary>
/// 一笔加点的结果。Reason 空串表示做成了；被拒时也带回当时的事实值（剩余点数与四属性），
/// duplicate 表示这个事务号之前已经用过、这一次没有再扣点。
/// </summary>
public sealed record AttrAllocateOutcome(
    bool Found,
    string Reason,
    bool Duplicate,
    string ClassId,
    int Level,
    int Remaining,
    AttrAllocation Allocated);

/// <summary>
/// A9：属性加点的唯一写入口。四属性的键白名单在 AttrPointTable，单属性上限在
/// config/attr_points.json，点数事实值在 character.attr_points（A8 发的），
/// 加到的四列在 character_attr_points，一笔一行进 character_attr_ledger。
///
/// 一次加点只做四件事，全在同一个事务里、先锁角色行：
///   ① 事务号重放 → duplicate，不重复扣点；
///   ② 点数不够 / 超过单属性上限 → 拒绝，一个字段都不写；
///   ③ 先抢台账号（txn_id 主键），再扣点数、再加属性——与货币那条「先抢号再动账」同口径；
///   ④ 台账里记的 remaining_after 是这一笔做完之后的事实值，对账时与 character.attr_points 比。
/// 本版没有洗点：表里只有加，没有任何一列记重置；洗点要开新卡改表、改事务。
/// </summary>
public sealed class AttrPointRepository(NpgsqlDataSource dataSource, AttrPointTable table)
{
    public const int MaxTxnLength = 128;

    /// <summary>只读：剩余点数与四属性加点。屏与核对口共用这一处。</summary>
    public async Task<AttrPointSnapshot> DescribeAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select c.class_id, c.level, c.attr_points,"
            + " coalesce(a.strength, 0), coalesce(a.agility, 0),"
            + " coalesce(a.spirit, 0), coalesce(a.vitality, 0)"
            + " from \"角色\".character c"
            + " left join \"角色\".character_attr_points a on a.character_id = c.id"
            + " where c.id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return new AttrPointSnapshot(false, "", 0, 0, AttrAllocation.None);
        }

        return new AttrPointSnapshot(
            true, reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
            new AttrAllocation(
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
    }

    /// <summary>
    /// 只给穿戴判定用：这一个角色已经加到四属性上的点数。没有加点行回 0，
    /// 装备门槛因此与 A9 之前逐字相同（没写 require_attr 的装备本来就走不到这一格）。
    /// </summary>
    public async Task<AttrAllocation> AllocationAsync(long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select strength, agility, spirit, vitality"
            + " from \"角色\".character_attr_points where character_id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return AttrAllocation.None;
        }

        return new AttrAllocation(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    public async Task<AttrAllocateOutcome> AllocateAsync(
        long characterId,
        string? account,
        string txnId,
        string attribute,
        int points,
        CancellationToken stopping)
    {
        var txn = (txnId ?? "").Trim();
        if (txn.Length == 0 || txn.Length > MaxTxnLength)
        {
            return new AttrAllocateOutcome(false, "bad_txn", false, "", 0, 0, AttrAllocation.None);
        }

        var key = (attribute ?? "").Trim();
        if (!AttrPointTable.IsAttribute(key))
        {
            return new AttrAllocateOutcome(false, "bad_attribute", false, "", 0, 0, AttrAllocation.None);
        }

        if (points < 1)
        {
            return new AttrAllocateOutcome(false, "bad_points", false, "", 0, 0, AttrAllocation.None);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        var found = false;
        var remaining = 0;
        var classId = "";
        var level = 0;
        var allocated = AttrAllocation.None;
        await using (var read = new NpgsqlCommand(
            "select class_id, level, attr_points from \"角色\".character " + owned,
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                read.Parameters.AddWithValue(account);
            }

            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                found = true;
                classId = reader.GetString(0);
                level = reader.GetInt32(1);
                remaining = reader.GetInt32(2);
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new AttrAllocateOutcome(
                false, "character_not_found", false, "", 0, 0, AttrAllocation.None);
        }

        allocated = await AllocationInTransactionAsync(connection, transaction, characterId, stopping);

        await using (var dup = new NpgsqlCommand(
            "select attribute, points from \"角色\".character_attr_ledger where txn_id = $1",
            connection, transaction))
        {
            dup.Parameters.AddWithValue(txn);
            await using var reader = await dup.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                // 同一个事务号重放：不再扣点、不再加属性，回当时的事实值。
                await reader.DisposeAsync();
                await transaction.RollbackAsync(stopping);
                return new AttrAllocateOutcome(true, "", true, classId, level, remaining, allocated);
            }
        }

        if (remaining < points)
        {
            await transaction.RollbackAsync(stopping);
            return new AttrAllocateOutcome(true, "not_enough_points", false, classId, level, remaining, allocated);
        }

        // 单属性上限：0 表示不设上限，只受手上点数约束。
        if (table.PerAttrCap > 0 && allocated.Get(key) + points > table.PerAttrCap)
        {
            await transaction.RollbackAsync(stopping);
            return new AttrAllocateOutcome(true, "attr_cap_reached", false, classId, level, remaining, allocated);
        }

        var left = remaining - points;
        try
        {
            // 先抢台账号：同一个事务号只加一次（主键兜底并发）。
            await using var claim = new NpgsqlCommand(
                "insert into \"角色\".character_attr_ledger"
                + " (txn_id, character_id, attribute, points, remaining_after)"
                + " values ($1, $2, $3, $4, $5) on conflict do nothing",
                connection, transaction);
            claim.Parameters.AddWithValue(txn);
            claim.Parameters.AddWithValue(characterId);
            claim.Parameters.AddWithValue(key);
            claim.Parameters.AddWithValue(points);
            claim.Parameters.AddWithValue(left);
            if (await claim.ExecuteNonQueryAsync(stopping) == 0)
            {
                await transaction.RollbackAsync(stopping);
                return new AttrAllocateOutcome(true, "", true, classId, level, remaining, allocated);
            }

            await using (var deduct = new NpgsqlCommand(
                "update \"角色\".character set attr_points = attr_points - $2 where id = $1",
                connection, transaction))
            {
                deduct.Parameters.AddWithValue(characterId);
                deduct.Parameters.AddWithValue(points);
                await deduct.ExecuteNonQueryAsync(stopping);
            }

            // 列名来自上面的白名单，不是用户原串；四列各一格，不动态拼列名以外的部分。
            await using (var add = new NpgsqlCommand(
                "insert into \"角色\".character_attr_points"
                + " (character_id, strength, agility, spirit, vitality)"
                + " values ($1, $2, $3, $4, $5)"
                + " on conflict (character_id) do update set"
                + " strength = \"角色\".character_attr_points.strength + excluded.strength,"
                + " agility = \"角色\".character_attr_points.agility + excluded.agility,"
                + " spirit = \"角色\".character_attr_points.spirit + excluded.spirit,"
                + " vitality = \"角色\".character_attr_points.vitality + excluded.vitality,"
                + " updated_at = now()",
                connection, transaction))
            {
                add.Parameters.AddWithValue(characterId);
                add.Parameters.AddWithValue(key == "strength" ? points : 0);
                add.Parameters.AddWithValue(key == "agility" ? points : 0);
                add.Parameters.AddWithValue(key == "spirit" ? points : 0);
                add.Parameters.AddWithValue(key == "vitality" ? points : 0);
                await add.ExecuteNonQueryAsync(stopping);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            // 库层 CHECK 兜底：算错了也写不进去，整笔不做，reason 直说点数不合法。
            await transaction.RollbackAsync(stopping);
            return new AttrAllocateOutcome(true, "bad_points", false, classId, level, remaining, allocated);
        }

        await transaction.CommitAsync(stopping);
        var after = new AttrAllocation(
            allocated.Strength + (key == "strength" ? points : 0),
            allocated.Agility + (key == "agility" ? points : 0),
            allocated.Spirit + (key == "spirit" ? points : 0),
            allocated.Vitality + (key == "vitality" ? points : 0));
        return new AttrAllocateOutcome(true, "", false, classId, level, left, after);
    }

    /// <summary>在一笔已开的事务里读加点（穿戴判定与加点共用同一套读法）。</summary>
    public async Task<AttrAllocation> AllocationInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select strength, agility, spirit, vitality"
            + " from \"角色\".character_attr_points where character_id = $1",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return AttrAllocation.None;
        }

        return new AttrAllocation(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    /// <summary>只给核对用：直写剩余点数，验加点门槛与库层 CHECK。</summary>
    public async Task RawWritePointsAsync(long characterId, int points, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set attr_points = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(points);
        await command.ExecuteNonQueryAsync(stopping);
    }
}
