using Npgsql;

namespace Social;

public sealed record HomesteadPlantRow(string SlotId, string CropId, DateTimeOffset PlantedAt, bool Mature);

public sealed record HomesteadPlantOutcome(
    bool Ok, string Reason, bool Duplicate, long CharacterId, string SlotId, string CropId,
    string TxnId, IReadOnlyList<HomesteadPlantRow> Plants, long GrantedHomeCoin, long HomeCoinBalance);

public sealed class HomesteadPlantRepository(
    NpgsqlDataSource dataSource,
    HomesteadPlantTable table,
    HomesteadHarvestGrant harvestGrant)
{
    const int MaxTxnLength = 160;

    public async Task<HomesteadPlantOutcome> PlantAsync(
        string account, ulong characterId, string txnId, string slotId, string cropId,
        CancellationToken stopping)
    {
        var txn = (txnId ?? "").Trim();
        var slot = (slotId ?? "").Trim();
        var crop = (cropId ?? "").Trim();
        if (characterId == 0 || txn.Length == 0 || txn.Length > MaxTxnLength
            || slot.Length == 0 || crop.Length == 0)
        {
            return Reject("request_missing", characterId, slot, crop, txn, 0, 0);
        }

        if (!table.HasSlot(slot))
        {
            return Reject("slot_unknown", characterId, slot, crop, txn, 0, 0);
        }

        if (table.FindCrop(crop) is null)
        {
            return Reject("crop_unknown", characterId, slot, crop, txn, 0, 0);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await ClaimTxnAsync(connection, transaction, txn, (long)characterId, "plant", slot, stopping))
        {
            await transaction.RollbackAsync(stopping);
            var replay = await DescribeRowsOnConnAsync(connection, (long)characterId, stopping);
            return new HomesteadPlantOutcome(true, "duplicate", true, (long)characterId,
                slot, crop, txn, replay, 0, 0);
        }

        if (await SlotHasDecorAsync(connection, transaction, (long)characterId, slot, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return Reject("slot_has_decor", characterId, slot, crop, txn, 0, 0);
        }

        if (await SlotHasPlantAsync(connection, transaction, (long)characterId, slot, stopping))
        {
            await transaction.RollbackAsync(stopping);
            return Reject("slot_has_plant", characterId, slot, crop, txn, 0, 0);
        }

        await using (var ins = new NpgsqlCommand(
            "insert into \"社交\".homestead_plant "
            + "(character_id, slot_id, crop_id, plant_txn_id) values ($1, $2, $3, $4)",
            connection, transaction))
        {
            ins.Parameters.AddWithValue((long)characterId);
            ins.Parameters.AddWithValue(slot);
            ins.Parameters.AddWithValue(crop);
            ins.Parameters.AddWithValue(txn);
            await ins.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        var plants = await DescribeRowsAsync((long)characterId, stopping);
        return new HomesteadPlantOutcome(true, "", false, (long)characterId,
            slot, crop, txn, plants, 0, 0);
    }

    public async Task<HomesteadPlantOutcome> HarvestAsync(
        string account, ulong characterId, string txnId, string slotId,
        CancellationToken stopping)
    {
        var txn = (txnId ?? "").Trim();
        var slot = (slotId ?? "").Trim();
        if (characterId == 0 || txn.Length == 0 || txn.Length > MaxTxnLength || slot.Length == 0)
        {
            return Reject("request_missing", characterId, slot, "", txn, 0, 0);
        }

        if (!table.HasSlot(slot))
        {
            return Reject("slot_unknown", characterId, slot, "", txn, 0, 0);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        if (!await ClaimTxnAsync(connection, transaction, txn, (long)characterId, "harvest", slot, stopping))
        {
            await transaction.RollbackAsync(stopping);
            var replay = await DescribeRowsOnConnAsync(connection, (long)characterId, stopping);
            return new HomesteadPlantOutcome(true, "duplicate", true, (long)characterId,
                slot, "", txn, replay, 0, 0);
        }

        DateTimeOffset plantedAt;
        string cropId;
        await using (var sel = new NpgsqlCommand(
            "select crop_id, planted_at from \"社交\".homestead_plant "
            + "where character_id = $1 and slot_id = $2",
            connection, transaction))
        {
            sel.Parameters.AddWithValue((long)characterId);
            sel.Parameters.AddWithValue(slot);
            await using var reader = await sel.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                await transaction.RollbackAsync(stopping);
                return Reject("slot_empty", characterId, slot, "", txn, 0, 0);
            }

            cropId = reader.GetString(0);
            plantedAt = reader.GetFieldValue<DateTimeOffset>(1);
        }

        var def = table.FindCrop(cropId);
        if (def is null)
        {
            await transaction.RollbackAsync(stopping);
            return Reject("crop_unknown", characterId, slot, cropId, txn, 0, 0);
        }

        var readyAt = plantedAt.AddSeconds(def.GrowSeconds);
        if (DateTimeOffset.UtcNow < readyAt)
        {
            await transaction.RollbackAsync(stopping);
            return Reject("not_mature", characterId, slot, cropId, txn, 0, 0);
        }

        var grant = await harvestGrant.GrantAsync(
            account, characterId, "homestead_harvest:" + txn, def.YieldHomeCoin, stopping);
        if (!grant.Ok)
        {
            await transaction.RollbackAsync(stopping);
            return Reject(grant.Reason, characterId, slot, cropId, txn, 0, 0);
        }

        if (grant.Duplicate)
        {
            await transaction.RollbackAsync(stopping);
            var plantsDup = await DescribeRowsOnConnAsync(connection, (long)characterId, stopping);
            return new HomesteadPlantOutcome(true, "duplicate", true, (long)characterId,
                slot, cropId, txn, plantsDup, def.YieldHomeCoin, grant.HomeCoinBalance);
        }

        await using (var del = new NpgsqlCommand(
            "delete from \"社交\".homestead_plant where character_id = $1 and slot_id = $2",
            connection, transaction))
        {
            del.Parameters.AddWithValue((long)characterId);
            del.Parameters.AddWithValue(slot);
            await del.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);

        var plants = await DescribeRowsAsync((long)characterId, stopping);
        return new HomesteadPlantOutcome(true, "", grant.Duplicate, (long)characterId,
            slot, cropId, txn, plants, def.YieldHomeCoin, grant.HomeCoinBalance);
    }

    public async Task<IReadOnlyList<HomesteadPlantRow>> DescribeAsync(long characterId, CancellationToken stopping)
        => await DescribeRowsAsync(characterId, stopping);

    static async Task<bool> ClaimTxnAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string txnId, long characterId, string action, string slotId, CancellationToken stopping)
    {
        await using var cmd = new NpgsqlCommand(
            "insert into \"社交\".homestead_plant_txn (txn_id, character_id, action, slot_id) "
            + "values ($1, $2, $3, $4) on conflict do nothing",
            connection, transaction);
        cmd.Parameters.AddWithValue(txnId);
        cmd.Parameters.AddWithValue(characterId);
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.AddWithValue(slotId);
        return await cmd.ExecuteNonQueryAsync(stopping) > 0;
    }

    static async Task<bool> SlotHasDecorAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        long characterId, string slotId, CancellationToken stopping)
    {
        await using var cmd = new NpgsqlCommand(
            "select 1 from \"社交\".homestead_placement where character_id = $1 and slot_id = $2",
            connection, transaction);
        cmd.Parameters.AddWithValue(characterId);
        cmd.Parameters.AddWithValue(slotId);
        return await cmd.ExecuteScalarAsync(stopping) is not null;
    }

    static async Task<bool> SlotHasPlantAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, string slotId, CancellationToken stopping)
        => await SlotHasPlantOnConn(connection, transaction, characterId, slotId, stopping);

    public static async Task<bool> SlotHasPlantOnConn(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        long characterId, string slotId, CancellationToken stopping)
    {
        await using var cmd = new NpgsqlCommand(
            "select 1 from \"社交\".homestead_plant where character_id = $1 and slot_id = $2",
            connection, transaction);
        cmd.Parameters.AddWithValue(characterId);
        cmd.Parameters.AddWithValue(slotId);
        return await cmd.ExecuteScalarAsync(stopping) is not null;
    }

    async Task<IReadOnlyList<HomesteadPlantRow>> DescribeRowsAsync(
        long characterId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        return await DescribeRowsOnConnAsync(connection, characterId, stopping);
    }

    async Task<IReadOnlyList<HomesteadPlantRow>> DescribeRowsOnConnAsync(
        NpgsqlConnection connection, long characterId, CancellationToken stopping)
    {
        var list = new List<HomesteadPlantRow>();
        await using var cmd = new NpgsqlCommand(
            "select slot_id, crop_id, planted_at from \"社交\".homestead_plant "
            + "where character_id = $1 order by slot_id",
            connection);
        cmd.Parameters.AddWithValue(characterId);
        await using var reader = await cmd.ExecuteReaderAsync(stopping);
        while (await reader.ReadAsync(stopping))
        {
            var slot = reader.GetString(0);
            var crop = reader.GetString(1);
            var planted = reader.GetFieldValue<DateTimeOffset>(2);
            var def = table.FindCrop(crop);
            var mature = def is not null
                && DateTimeOffset.UtcNow >= planted.AddSeconds(def.GrowSeconds);
            list.Add(new HomesteadPlantRow(slot, crop, planted, mature));
        }

        return list;
    }

    static HomesteadPlantOutcome Reject(
        string reason, ulong characterId, string slot, string crop, string txn,
        long granted, long balance) =>
        new(false, reason, false, (long)characterId, slot, crop, txn,
            Array.Empty<HomesteadPlantRow>(), granted, balance);
}