using Mmo.Proto.Character;
using Npgsql;

namespace Character;

/// <summary>
/// N9：任务发放时按库里城池行的 tax_pct 记入「社交」.city_tax_ledger。
/// 税只记账、不从玩家钱包扣；与拍卖销毁税是另一张表。税率读运行时 city 行（含 P3-3a4 写回），不读请求。
/// </summary>
public sealed class CityTaxRepository(
    NpgsqlDataSource dataSource,
    CityTable cityTable,
    string goldKind)
{
    public string GoldKind { get; } = goldKind;

    public const string LedgerSource = "task_grant";

    /// <summary>与拍卖行同一口径：整数百分比、向下取整。</summary>
    public static long TaxGoldOf(long grossGold, int taxPct) =>
        grossGold < 1 || taxPct < 1 ? 0 : grossGold * taxPct / 100;

    public static long SumPositiveGold(
        IReadOnlyList<GrantEntry> entries, string goldKind)
    {
        long sum = 0;
        foreach (var entry in entries)
        {
            if (entry.BodyCase != GrantEntry.BodyOneofCase.Currency)
            {
                continue;
            }

            var kind = (entry.Currency.Kind ?? "").Trim();
            if (!string.Equals(kind, goldKind, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Currency.Delta > 0)
            {
                sum += entry.Currency.Delta;
            }
        }

        return sum;
    }

    static bool IsTaskGrantTxn(string txnId) =>
        !txnId.StartsWith("shop_buy:", StringComparison.Ordinal);

    /// <summary>在 GrantRepository.ApplyAsync 已抢到 grant_txn 之后、提交之前调用。</summary>
    public async Task RecordTaskGrantInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string txnId,
        long grossGold,
        CancellationToken stopping)
    {
        if (!IsTaskGrantTxn(txnId) || grossGold < 1)
        {
            return;
        }

        var cityId = cityTable.TaskGrantCityId;
        int taxPct;
        await using (var read = new NpgsqlCommand(
            "select tax_pct from \"社交\".city where city_id = $1 for update",
            connection, transaction))
        {
            read.Parameters.AddWithValue(cityId);
            var scalar = await read.ExecuteScalarAsync(stopping);
            if (scalar is null or DBNull)
            {
                throw new GrantException("city_missing");
            }

            taxPct = Convert.ToInt32(scalar);
        }

        var taxGold = TaxGoldOf(grossGold, taxPct);
        await using var insert = new NpgsqlCommand(
            "insert into \"社交\".city_tax_ledger "
            + "(txn_id, city_id, source, gross_gold, tax_gold) "
            + "values ($1, $2, $3, $4, $5) "
            + "on conflict (txn_id) do nothing",
            connection, transaction);
        insert.Parameters.AddWithValue(txnId);
        insert.Parameters.AddWithValue(cityId);
        insert.Parameters.AddWithValue(LedgerSource);
        insert.Parameters.AddWithValue(grossGold);
        insert.Parameters.AddWithValue(taxGold);
        await insert.ExecuteNonQueryAsync(stopping);
    }

    /// <summary>
    /// Q5：任务发放灵石时按城池抽成记一笔**灵石**账（「社交」.city_stone_tax_ledger）。
    /// 与金币账本 city_tax_ledger 是两张表：灵石与金币单位不同，加在一起没有意义。
    /// 抽成只记账、不从玩家钱包扣（与 N9 的金币口径一致）；税率读运行时
    /// 「社交」.city.tax_pct（城战写回会改它），请求里带不进来、配置里也没有。
    /// 必须在发灵石那一笔事务里调用，事务号与「角色」.grant_txn 是同一个。
    /// 返回这一笔算出来的 tax_stone，给回包一个可核对的事实值。
    /// </summary>
    public async Task<long> RecordStoneGrantInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string cityId,
        string txnId,
        string currency,
        long grossStone,
        CancellationToken stopping)
    {
        if (grossStone < 1)
        {
            return 0;
        }

        int taxPct;
        await using (var read = new NpgsqlCommand(
            "select tax_pct from \"社交\".city where city_id = $1",
            connection, transaction))
        {
            read.Parameters.AddWithValue(cityId);
            var scalar = await read.ExecuteScalarAsync(stopping);
            if (scalar is null or DBNull)
            {
                throw new GrantException("city_missing");
            }

            taxPct = Convert.ToInt32(scalar);
        }

        var taxStone = TaxGoldOf(grossStone, taxPct);
        await using var insert = new NpgsqlCommand(
            "insert into \"社交\".city_stone_tax_ledger "
            + "(txn_id, currency, city_id, source, gross_stone, tax_stone) "
            + "values ($1, $2, $3, $4, $5, $6)",
            connection, transaction);
        insert.Parameters.AddWithValue(txnId);
        insert.Parameters.AddWithValue(currency);
        insert.Parameters.AddWithValue(cityId);
        insert.Parameters.AddWithValue(LedgerSource);
        insert.Parameters.AddWithValue(grossStone);
        insert.Parameters.AddWithValue(taxStone);
        await insert.ExecuteNonQueryAsync(stopping);
        return taxStone;
    }

    public async Task<CityTaxLedgerRow?> ReadRowAsync(string txnId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select txn_id, city_id, source, gross_gold, tax_gold "
            + "from \"社交\".city_tax_ledger where txn_id = $1");
        command.Parameters.AddWithValue(txnId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return new CityTaxLedgerRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    /// <summary>Q5 只读：这一笔的灵石抽成流水（核对用）。没写过返回 null。</summary>
    public async Task<StoneTaxLedgerRow?> ReadStoneRowAsync(
        string txnId, string currency, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select txn_id, currency, city_id, source, gross_stone, tax_stone "
            + "from \"社交\".city_stone_tax_ledger where txn_id = $1 and currency = $2");
        command.Parameters.AddWithValue(txnId);
        command.Parameters.AddWithValue(currency);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return null;
        }

        return new StoneTaxLedgerRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }
}

public sealed record CityTaxLedgerRow(
    string TxnId, string CityId, string Source, long GrossGold, long TaxGold);
/// <summary>Q5 只读：一笔灵石抽成流水。与金币行 CityTaxLedgerRow 的字段一一对应，单位是灵石。</summary>
public sealed record StoneTaxLedgerRow(
    string TxnId, string Currency, string CityId, string Source, long GrossStone, long TaxStone);
