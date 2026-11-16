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
}

public sealed record CityTaxLedgerRow(
    string TxnId, string CityId, string Source, long GrossGold, long TaxGold);