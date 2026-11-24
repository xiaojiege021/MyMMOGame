using Npgsql;

namespace Character;

/// <summary>B5 摆摊的业务拒绝。Code 直接回给网关，转成 ok=false + reason。</summary>
public sealed class StallException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record StallOpenOutcome(
    bool Ok,
    string Reason,
    string StallId,
    string OwnerName,
    string RegionId,
    double PosX,
    double PosY,
    string Title,
    int LiveListings,
    int Slots,
    int MinLevel,
    int TaxRatePct);

public sealed record StallCloseOutcome(
    bool Ok,
    string Reason,
    string StallId,
    int CancelledListings);

public sealed record StallListOutcome(
    bool Ok,
    string Reason,
    long ListingId,
    string StallId,
    int Slot,
    string ItemId,
    int Count,
    long PriceGold,
    int LiveListings,
    int Slots,
    bool Duplicate);

public sealed record StallListingRow(
    long ListingId,
    int Slot,
    string ItemId,
    string ItemName,
    int Count,
    long PriceGold,
    bool Bound);

public sealed record StallSnapshot(
    bool Found,
    string StallId,
    long OwnerId,
    string OwnerName,
    string RegionId,
    double PosX,
    double PosY,
    string Title,
    string State,
    int LiveListings,
    int Slots,
    IReadOnlyList<StallListingRow> Listings);

/// <summary>
/// B5 摆摊的持久化：摊位本身在「交易」.stall，挂单仍是「交易」.auction_listing
/// 的一行（channel = 'stall'）。成交不经过这里——它走 AuctionRepository.SettleAsync
/// 那一笔事务，税按 channel 取 config/stall.json；世界服只拿到显示用的摘要。
///
/// 摊位归属、等级门槛、格数、「一摊一件一格」都在这一层判，客户端带不进价格以外的东西。
/// </summary>
public sealed class StallRepository(
    NpgsqlDataSource dataSource,
    StallTable table,
    ItemTable items,
    CharacterConfig config)
{
    public async Task<StallOpenOutcome> OpenAsync(
        long characterId, string regionId, double posX, double posY, string title,
        string stallId, CancellationToken stopping)
    {
        var region = (regionId ?? "").Trim();
        if (region.Length == 0)
        {
            return Reject("region_missing");
        }

        var sign = (title ?? "").Trim();
        if (sign.Length > table.TitleMaxLength)
        {
            return Reject("bad_title");
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);

        string name = "";
        var level = 0;
        var found = false;
        await using (var read = new NpgsqlCommand(
            "select name, level from \"角色\".character where id = $1", connection))
        {
            read.Parameters.AddWithValue(characterId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                name = reader.GetString(0);
                level = reader.GetInt32(1);
                found = true;
            }
        }

        if (!found)
        {
            return Reject("character_not_found");
        }

        if (level < table.MinLevel)
        {
            return Reject("level_too_low");
        }

        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 一个摊主同时只开一个摊：撞 stall_live_owner 就按 stall_exists 拒。
        var inserted = 0;
        try
        {
            await using var insert = new NpgsqlCommand(
                "insert into \"交易\".stall "
                + "(stall_id, owner_id, owner_name, region_id, pos_x, pos_y, title, state) "
                + "values ($1, $2, $3, $4, $5, $6, $7, 'open') "
                + "on conflict (stall_id) do nothing", connection, transaction);
            insert.Parameters.AddWithValue(stallId);
            insert.Parameters.AddWithValue(characterId);
            insert.Parameters.AddWithValue(name);
            insert.Parameters.AddWithValue(region);
            insert.Parameters.AddWithValue(posX);
            insert.Parameters.AddWithValue(posY);
            insert.Parameters.AddWithValue(sign);
            inserted = await insert.ExecuteNonQueryAsync(stopping);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(stopping);
            return Reject("stall_exists");
        }

        if (inserted == 0)
        {
            // 同一笔请求重放：摊位号已经在库里，摊主一致且还没关就按成功回。
            var (ownerId, state) = await ReadOwnerAsync(connection, transaction, stallId, stopping);
            await transaction.RollbackAsync(stopping);
            if (ownerId != characterId || state != "open")
            {
                return Reject("stall_exists");
            }

            var live = await LiveCountAsync(connection, null, stallId, stopping);
            return new StallOpenOutcome(
                true, "", stallId, name, region, posX, posY, sign, live,
                table.Slots, table.MinLevel, table.TaxRatePct);
        }

        await transaction.CommitAsync(stopping);

        return new StallOpenOutcome(
            true, "", stallId, name, region, posX, posY, sign, 0,
            table.Slots, table.MinLevel, table.TaxRatePct);
    }

    public async Task<StallCloseOutcome> CloseAsync(
        long characterId, string stallId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        var (ownerId, state) = await ReadOwnerAsync(connection, transaction, stallId, stopping);
        if (ownerId == 0)
        {
            await transaction.RollbackAsync(stopping);
            return new StallCloseOutcome(false, "stall_not_found", stallId, 0);
        }

        if (ownerId != characterId)
        {
            await transaction.RollbackAsync(stopping);
            return new StallCloseOutcome(false, "not_owner", stallId, 0);
        }

        if (state != "open")
        {
            await transaction.RollbackAsync(stopping);
            return new StallCloseOutcome(false, "stall_closed", stallId, 0);
        }

        await using (var update = new NpgsqlCommand(
            "update \"交易\".stall set state = 'closed', closed_at = now() "
            + "where stall_id = $1 and state = 'open'", connection, transaction))
        {
            update.Parameters.AddWithValue(stallId);
            await update.ExecuteNonQueryAsync(stopping);
        }

        // 关摊即撤单：挂单只改状态，物品不搬、仍留在卖方那一格。
        var cancelled = 0;
        await using (var cancel = new NpgsqlCommand(
            "update \"交易\".auction_listing set state = 'cancelled' "
            + "where stall_id = $1 and state = 'listed'", connection, transaction))
        {
            cancel.Parameters.AddWithValue(stallId);
            cancelled = await cancel.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new StallCloseOutcome(true, "", stallId, cancelled);
    }

    public async Task<StallListOutcome> ListAsync(
        long characterId, string stallId, int slot, int count, long priceGold,
        string txnId, CancellationToken stopping)
    {
        if (priceGold < 1)
        {
            return new StallListOutcome(false, "price_invalid", 0, stallId, slot, "", 0, 0,
                0, table.Slots, false);
        }

        if (count < 1)
        {
            return new StallListOutcome(false, "count_invalid", 0, stallId, slot, "", 0, 0,
                0, table.Slots, false);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        var (ownerId, state) = await ReadOwnerAsync(connection, transaction, stallId, stopping);
        if (ownerId == 0)
        {
            await transaction.RollbackAsync(stopping);
            return new StallListOutcome(false, "stall_not_found", 0, stallId, slot, "", 0, 0,
                0, table.Slots, false);
        }

        if (ownerId != characterId)
        {
            await transaction.RollbackAsync(stopping);
            return new StallListOutcome(false, "not_owner", 0, stallId, slot, "", 0, 0,
                0, table.Slots, false);
        }

        if (state != "open")
        {
            await transaction.RollbackAsync(stopping);
            return new StallListOutcome(false, "stall_closed", 0, stallId, slot, "", 0, 0,
                0, table.Slots, false);
        }

        var live = await LiveCountAsync(connection, transaction, stallId, stopping);
        if (live >= table.Slots)
        {
            await transaction.RollbackAsync(stopping);
            return new StallListOutcome(false, "stall_full", 0, stallId, slot, "", 0, 0,
                live, table.Slots, false);
        }

        var bagSlots = await BagExpandRepository.SlotsAsync(
            connection, transaction, characterId, config, stopping);
        var itemId = "";
        var owned = 0;
        var bound = false;
        if (slot >= 1 && slot <= bagSlots)
        {
            await using var read = new NpgsqlCommand(
                "select item_id, count, bound from \"角色\".inventory "
                + "where character_id = $1 and slot = $2 for update", connection, transaction);
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue((short)slot);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                itemId = reader.GetString(0);
                owned = reader.GetInt32(1);
                bound = reader.GetBoolean(2);
            }
        }

        if (itemId.Length == 0)
        {
            await transaction.RollbackAsync(stopping);
            return new StallListOutcome(false, "slot_empty", 0, stallId, slot, "", 0, 0,
                live, table.Slots, false);
        }

        // 绑定物品不能上架（P1-1b3）：与拍卖成交时的 bound_item 同一条口径，
        // 这里提前拒，免得挂上去才发现卖不掉。
        if (bound)
        {
            await transaction.RollbackAsync(stopping);
            return new StallListOutcome(false, "bound_item", 0, stallId, slot, itemId, 0, 0,
                live, table.Slots, false);
        }

        var item = items.Find(itemId);
        if (item is null)
        {
            await transaction.RollbackAsync(stopping);
            return new StallListOutcome(false, "unknown_item", 0, stallId, slot, itemId, 0, 0,
                live, table.Slots, false);
        }

        if (count > owned || count > item.StackMax)
        {
            await transaction.RollbackAsync(stopping);
            return new StallListOutcome(false, "count_invalid", 0, stallId, slot, itemId, 0, 0,
                live, table.Slots, false);
        }

        long listingId = 0;
        try
        {
            await using var insert = new NpgsqlCommand(
                "insert into \"交易\".auction_listing "
                + "(list_txn, seller_id, slot, item_id, count, price_gold, state, channel, stall_id) "
                + "values ($1, $2, $3, $4, $5, $6, 'listed', 'stall', $7) "
                + "on conflict (list_txn) do nothing returning listing_id", connection, transaction);
            insert.Parameters.AddWithValue(txnId);
            insert.Parameters.AddWithValue(characterId);
            insert.Parameters.AddWithValue((short)slot);
            insert.Parameters.AddWithValue(itemId);
            insert.Parameters.AddWithValue(count);
            insert.Parameters.AddWithValue(priceGold);
            insert.Parameters.AddWithValue(stallId);
            var value = await insert.ExecuteScalarAsync(stopping);
            listingId = value is null || value is DBNull ? 0 : Convert.ToInt64(value);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 同一格已经挂着（拍卖或摆摊都算）：auction_listing_live_slot 管着。
            await transaction.RollbackAsync(stopping);
            return new StallListOutcome(false, "slot_listed", 0, stallId, slot, itemId, count,
                priceGold, live, table.Slots, false);
        }

        if (listingId == 0)
        {
            // 同一笔请求重放：找回上一次的挂单号，件数不重复加。
            await using var read = new NpgsqlCommand(
                "select listing_id from \"交易\".auction_listing where list_txn = $1",
                connection, transaction);
            read.Parameters.AddWithValue(txnId);
            var value = await read.ExecuteScalarAsync(stopping);
            listingId = value is null || value is DBNull ? 0 : Convert.ToInt64(value);
            await transaction.RollbackAsync(stopping);
            return new StallListOutcome(listingId != 0, listingId == 0 ? "duplicate" : "",
                listingId, stallId, slot, itemId, count, priceGold, live, table.Slots, true);
        }

        live++;
        await transaction.CommitAsync(stopping);
        return new StallListOutcome(true, "", listingId, stallId, slot, itemId, count,
            priceGold, live, table.Slots, false);
    }

    /// <summary>看摊：只读，不锁行。卖家关摊之后仍能查到历史挂单，但只回 listed 的。</summary>
    public async Task<StallSnapshot> ViewAsync(string stallId, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);

        long ownerId = 0;
        var ownerName = "";
        var region = "";
        double posX = 0;
        double posY = 0;
        var sign = "";
        var state = "";
        await using (var read = new NpgsqlCommand(
            "select owner_id, owner_name, region_id, pos_x, pos_y, title, state "
            + "from \"交易\".stall where stall_id = $1", connection))
        {
            read.Parameters.AddWithValue(stallId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (!await reader.ReadAsync(stopping))
            {
                return new StallSnapshot(false, stallId, 0, "", "", 0, 0, "", "",
                    0, table.Slots, Array.Empty<StallListingRow>());
            }

            ownerId = reader.GetInt64(0);
            ownerName = reader.GetString(1);
            region = reader.GetString(2);
            posX = reader.GetDouble(3);
            posY = reader.GetDouble(4);
            sign = reader.GetString(5);
            state = reader.GetString(6);
        }

        var rows = new List<StallListingRow>();
        await using (var read = new NpgsqlCommand(
            "select l.listing_id, l.slot, l.item_id, l.count, l.price_gold, "
            + "coalesce(i.bound, false) "
            + "from \"交易\".auction_listing l "
            + "left join \"角色\".inventory i "
            + "on i.character_id = l.seller_id and i.slot = l.slot "
            + "where l.stall_id = $1 and l.state = 'listed' order by l.slot", connection))
        {
            read.Parameters.AddWithValue(stallId);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                var itemId = reader.GetString(2);
                rows.Add(new StallListingRow(
                    reader.GetInt64(0), reader.GetInt16(1), itemId,
                    items.Find(itemId)?.Name ?? itemId,
                    reader.GetInt32(3), reader.GetInt64(4), reader.GetBoolean(5)));
            }
        }

        return new StallSnapshot(true, stallId, ownerId, ownerName, region, posX, posY,
            sign, state, rows.Count, table.Slots, rows);
    }

    static async Task<int> LiveCountAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string stallId,
        CancellationToken stopping)
    {
        await using var count = new NpgsqlCommand(
            "select count(*) from \"交易\".auction_listing "
            + "where stall_id = $1 and state = 'listed'", connection, transaction);
        count.Parameters.AddWithValue(stallId);
        var value = await count.ExecuteScalarAsync(stopping);
        return value is null || value is DBNull ? 0 : Convert.ToInt32(value);
    }

    static async Task<(long OwnerId, string State)> ReadOwnerAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string stallId,
        CancellationToken stopping)
    {
        await using var read = new NpgsqlCommand(
            "select owner_id, state from \"交易\".stall where stall_id = $1 for update",
            connection, transaction);
        read.Parameters.AddWithValue(stallId);
        await using var reader = await read.ExecuteReaderAsync(stopping);
        if (await reader.ReadAsync(stopping))
        {
            return (reader.GetInt64(0), reader.GetString(1));
        }

        return (0, "");
    }

    static StallOpenOutcome Reject(string reason) =>
        new(false, reason, "", "", "", 0, 0, "", 0, 0, 0, 0);
}
