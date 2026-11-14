using Npgsql;

namespace Character;

/// <summary>写回结果。Ok=false 时 Reason 给拒绝码；拒绝路径一分不写库。</summary>
public sealed record SiegeTerritoryOutcome(
    bool Ok, string Reason, bool Duplicate, string CityId, string OwnerSect, int TaxPct);

/// <summary>业务拒绝码，转成 ok=false + reason，异常不当成成功。</summary>
public sealed class SiegeTerritoryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// P3-3a4：城战胜负写回城池的唯一写入点（技术架构 3 / 6 节：持久数据只在角色服写）。
/// 一笔事务：锁城池行 → 先抢事务号 → 写 owner_sect / tax_pct → 事务内读回 → 提交。
/// 请求里只有胜方事实值，没有税率、旧归属、金币、灵石与奖励；抽成比例只从
/// config/siege_territory.json 读，城池编号要与 config/city.json 对得上。
///
/// 「先抢事务号」必须在写归属之前：同一场重放时若先写再撞主键，就会把归属改第二次。
/// 回滚之后不再读（事务已经结束），所以读回放在提交之前。
/// city_tax_ledger 记的是任务发放时的抽成流水；城战易主本身不产生金币，
/// 本卡不往账本里造一笔税，也不改那张表的 CHECK。
/// </summary>
public sealed class SiegeTerritoryRepository(
    NpgsqlDataSource dataSource, CityTable cities, SiegeTerritoryPolicy policy)
{
    public async Task<SiegeTerritoryOutcome> ApplyAsync(
        string txnId, string cityId, string matchId, uint winnerTeam, ulong winnerSect,
        CancellationToken stopping)
    {
        var txn = (txnId ?? "").Trim();
        var city = (cityId ?? "").Trim();
        if (txn.Length == 0 || city.Length == 0)
        {
            return Rejected("request_missing", city);
        }
        if (winnerSect == 0)
        {
            return Rejected("winner_sect_missing", city);
        }
        if (winnerTeam > 1)
        {
            return Rejected("winner_team_invalid", city);
        }
        if (!cities.Cities.Any(c => string.Equals(c.CityId, city, StringComparison.Ordinal)))
        {
            return Rejected("unknown_city", city);
        }

        int taxPct;
        try
        {
            taxPct = policy.TaxPctOf(city);
        }
        catch (ConfigException)
        {
            return Rejected("tax_policy_missing", city);
        }

        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);
        try
        {
            // 1. 锁城池行，同时确认这座城真的在库里（配置里有不等于库里有）。
            var exists = false;
            await using (var read = new NpgsqlCommand(
                "select city_id from \"社交\".city where city_id = $1 for update",
                connection, transaction))
            {
                read.Parameters.AddWithValue(city);
                await using var reader = await read.ExecuteReaderAsync(stopping);
                exists = await reader.ReadAsync(stopping);
            }
            if (!exists)
            {
                throw new SiegeTerritoryException("unknown_city");
            }

            // 2. 先抢事务号：撞主键就是重放，整笔退回，不改第二次归属。
            await using (var claim = new NpgsqlCommand(
                "insert into \"社交\".siege_result "
                + "(txn_id, city_id, match_id, winner_team, winner_sect, tax_pct) "
                + "values ($1, $2, $3, $4, $5, $6) on conflict (txn_id) do nothing",
                connection, transaction))
            {
                claim.Parameters.AddWithValue(txn);
                claim.Parameters.AddWithValue(city);
                claim.Parameters.AddWithValue((matchId ?? "").Trim());
                claim.Parameters.AddWithValue((int)winnerTeam);
                claim.Parameters.AddWithValue(winnerSect.ToString());
                claim.Parameters.AddWithValue(taxPct);
                if (await claim.ExecuteNonQueryAsync(stopping) != 1)
                {
                    throw new SiegeTerritoryException("duplicate");
                }
            }

            // 3. 归属与抽成比例写在同一行、同一笔事务里。
            await using (var update = new NpgsqlCommand(
                "update \"社交\".city set owner_sect = $2, tax_pct = $3 where city_id = $1",
                connection, transaction))
            {
                update.Parameters.AddWithValue(city);
                update.Parameters.AddWithValue(winnerSect.ToString());
                update.Parameters.AddWithValue(taxPct);
                await update.ExecuteNonQueryAsync(stopping);
            }

            // 4. 事务内读回，回包给的是库里的事实值，不是请求回声。
            string owner = "";
            var storedTax = 0;
            await using (var read = new NpgsqlCommand(
                "select owner_sect, tax_pct from \"社交\".city where city_id = $1",
                connection, transaction))
            {
                read.Parameters.AddWithValue(city);
                await using var reader = await read.ExecuteReaderAsync(stopping);
                if (await reader.ReadAsync(stopping))
                {
                    owner = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    storedTax = reader.GetInt32(1);
                }
            }

            await transaction.CommitAsync(stopping);
            return new SiegeTerritoryOutcome(true, "", false, city, owner, storedTax);
        }
        catch (SiegeTerritoryException ex)
        {
            await transaction.RollbackAsync(stopping);
            return Rejected(ex.Code, city, duplicate: ex.Code == "duplicate");
        }
    }

    static SiegeTerritoryOutcome Rejected(
        string reason, string city, bool duplicate = false) =>
        new(false, reason, duplicate, city, "", 0);
}
