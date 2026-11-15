using Npgsql;

namespace Social;

public sealed record HomesteadPlacement(string SlotId, string ItemId);

/// <summary>摆 / 撤 / 只读的回包事实值。业务拒绝时 ok=false + reason，未生效字段给空串。</summary>
public sealed record HomesteadOutcome(
    bool Ok, string Reason, bool Duplicate, long CharacterId, string SlotId,
    string ItemId, string TxnId, IReadOnlyList<HomesteadPlacement> Placements);

/// <summary>
/// C3 洞府摆放的唯一写入点：只写社交 schema 的 homestead_placement / homestead_txn，
/// 不 join 角色 schema、不写背包、不改货币、不写任何数值。
///
/// 摆 / 撤都在一笔事务里：先抢事务号 → 只读问角色服确认拥有 → 同件摆件只占一格的
/// 判定 → upsert / delete → 提交。业务拒绝整笔回滚、不占号（与 P1-1b5 的
/// grant_txn 同一口径）；同号重放回 duplicate=true，不改状态也不二次写。
/// 「访客可见」是 DescribeAsync 的纯读口，任何有效令牌都能按洞府主人角色号读事实值。
/// </summary>
public sealed class HomesteadRepository(
    NpgsqlDataSource dataSource, HomesteadTable table, HomesteadInventory inventory)
{
    const int MaxTxnLength = 160;

    public async Task<HomesteadOutcome> PlaceAsync(
        string account, ulong characterId, string txnId, string slotId, string itemId,
        CancellationToken stopping)
    {
        var txn = (txnId ?? "").Trim();
        var slot = (slotId ?? "").Trim();
        var item = (itemId ?? "").Trim();
        if (characterId == 0 || txn.Length == 0 || txn.Length > MaxTxnLength
            || slot.Length == 0 || item.Length == 0)
        {
            return Reject("request_missing", characterId, slot, item, txn);
        }

        // 格位与可摆物品全从可选内容表读，请求里带不进格位表、物品表或任何数值。
        if (!table.HasSlot(slot))
        {
            return Reject("slot_unknown", characterId, slot, item, txn);
        }

        if (!table.Allows(item))
        {
            return Reject("item_not_decor", characterId, slot, item, txn);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 抢事务号（网关生成，客户端带不进来）；同一号重放整笔不做。
        if (!await ClaimTxnAsync(connection, transaction, txn, characterId, slot, stopping))
        {
            await transaction.RollbackAsync(stopping);
            var replay = await DescribeAsync((long)characterId, stopping);
            return new HomesteadOutcome(true, "duplicate", true, (long)characterId,
                slot, item, txn, replay);
        }

        // 2. 摆件必须真的在角色背包里且是绑定态；社交服不碰角色 schema。
        var ownership = await inventory.CheckAsync(account, characterId, item, stopping);
        if (ownership != DecorOwnership.Owned)
        {
            await transaction.RollbackAsync(stopping);
            return Reject(ownership == DecorOwnership.Unavailable
                ? "inventory_unavailable" : "decor_not_owned", characterId, slot, item, txn);
        }

        // 3. 同一件摆件在同一个洞府只占一格（unique 索引是第二道硬约束）。
        await using (var taken = new NpgsqlCommand(
            "select slot_id from \"社交\".homestead_placement "
            + "where character_id = $1 and item_id = $2 and slot_id <> $3",
            connection, transaction))
        {
            taken.Parameters.AddWithValue((long)characterId);
            taken.Parameters.AddWithValue(item);
            taken.Parameters.AddWithValue(slot);
            if (await taken.ExecuteScalarAsync(stopping) is not null)
            {
                await transaction.RollbackAsync(stopping);
                return Reject("item_already_placed", characterId, slot, item, txn);
            }
        }

        // 4. 同格重摆只换展示事实：不消耗背包、不写角色库、不发任何奖励。
        try
        {
            await using var upsert = new NpgsqlCommand(
                "insert into \"社交\".homestead_placement "
                + "(character_id, slot_id, item_id, txn_id) values ($1, $2, $3, $4) "
                + "on conflict (character_id, slot_id) do update set "
                + "item_id = excluded.item_id, txn_id = excluded.txn_id, placed_at = now()",
                connection, transaction);
            upsert.Parameters.AddWithValue((long)characterId);
            upsert.Parameters.AddWithValue(slot);
            upsert.Parameters.AddWithValue(item);
            upsert.Parameters.AddWithValue(txn);
            await upsert.ExecuteNonQueryAsync(stopping);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // 两个并发请求把同一件摆件摆去不同格：唯一索引挡住后一笔。
            await transaction.RollbackAsync(stopping);
            return Reject("item_already_placed", characterId, slot, item, txn);
        }

        await transaction.CommitAsync(stopping);
        var placements = await DescribeAsync((long)characterId, stopping);
        return new HomesteadOutcome(true, "", false, (long)characterId, slot, item, txn, placements);
    }

    public async Task<HomesteadOutcome> RemoveAsync(
        ulong characterId, string txnId, string slotId, CancellationToken stopping)
    {
        var txn = (txnId ?? "").Trim();
        var slot = (slotId ?? "").Trim();
        if (characterId == 0 || txn.Length == 0 || txn.Length > MaxTxnLength || slot.Length == 0)
        {
            return Reject("request_missing", characterId, slot, "", txn);
        }

        if (!table.HasSlot(slot))
        {
            return Reject("slot_unknown", characterId, slot, "", txn);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await ClaimTxnAsync(connection, transaction, txn, characterId, slot, stopping))
        {
            await transaction.RollbackAsync(stopping);
            var replay = await DescribeAsync((long)characterId, stopping);
            return new HomesteadOutcome(true, "duplicate", true, (long)characterId,
                slot, "", txn, replay);
        }

        await using (var remove = new NpgsqlCommand(
            "delete from \"社交\".homestead_placement where character_id = $1 and slot_id = $2",
            connection, transaction))
        {
            remove.Parameters.AddWithValue((long)characterId);
            remove.Parameters.AddWithValue(slot);
            if (await remove.ExecuteNonQueryAsync(stopping) == 0)
            {
                await transaction.RollbackAsync(stopping);
                return Reject("slot_empty", characterId, slot, "", txn);
            }
        }

        await transaction.CommitAsync(stopping);
        var placements = await DescribeAsync((long)characterId, stopping);
        return new HomesteadOutcome(true, "", false, (long)characterId, slot, "", txn, placements);
    }

    /// <summary>访客只读：按洞府主人角色号列出「哪一格摆了哪一件」，不认令牌、不发奖。</summary>
    public async Task<IReadOnlyList<HomesteadPlacement>> DescribeAsync(
        long ownerCharacterId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var read = new NpgsqlCommand(
            "select slot_id, item_id from \"社交\".homestead_placement "
            + "where character_id = $1 order by slot_id", connection);
        read.Parameters.AddWithValue(ownerCharacterId);

        var rows = new List<HomesteadPlacement>();
        await using var reader = await read.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            rows.Add(new HomesteadPlacement(reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    static async Task<bool> ClaimTxnAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string txnId,
        ulong characterId, string slotId, CancellationToken stopping)
    {
        await using var claim = new NpgsqlCommand(
            "insert into \"社交\".homestead_txn (txn_id, character_id, slot_id) "
            + "values ($1, $2, $3) on conflict (txn_id) do nothing",
            connection, transaction);
        claim.Parameters.AddWithValue(txnId);
        claim.Parameters.AddWithValue((long)characterId);
        claim.Parameters.AddWithValue(slotId);
        return await claim.ExecuteNonQueryAsync(stopping) == 1;
    }

    static HomesteadOutcome Reject(
        string reason, ulong characterId, string slotId, string itemId, string txnId)
        => new(false, reason, false, (long)characterId, slotId, itemId, txnId,
            Array.Empty<HomesteadPlacement>());
}
