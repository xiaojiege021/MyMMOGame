using Npgsql;

using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>
/// Q5/J2 城池任务的核对入口：
///   --city-task-config / --city-task-table  只读，不连库（坏表在这里就 config_error）
///   --city-task-state &lt;角色号&gt;             只读面板事实值
///   --city-task-complete &lt;角色号&gt; &lt;任务&gt;   走一遍完成（与 RPC 同一个仓储）
///   --city-task-ledger &lt;角色号&gt;             任务流水 + 灵石抽成流水（核对用）
/// 时间点可用 --at 固定（业务日 / 业务周首只由 config/calendar.json 算）。
/// </summary>
internal static class CityTaskCli
{
    /// <summary>这几个 flag 归本类解析（CliParser 一行分发，避免把行数推过闸门）。</summary>
    public static bool Handles(string flag) => flag
        is "--city-task-config" or "--city-task-table" or "--city-task-state"
        or "--city-task-complete" or "--city-task-txn" or "--city-task-ledger";

    public static int Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--city-task-config":
                cityTaskConfig = true;
                return 0;
            case "--city-task-table" when i + 1 < args.Length:
                cityTaskTablePath = args[++i];
                return 0;
            case "--city-task-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out cityTaskStateOf);
                return 0;
            case "--city-task-complete" when i + 2 < args.Length:
                long.TryParse(args[++i], out cityTaskCompleteOf);
                cityTaskCompleteTask = args[++i];
                return 0;
            case "--city-task-txn" when i + 1 < args.Length:
                cityTaskTxn = args[++i];
                return 0;
            case "--city-task-ledger" when i + 1 < args.Length:
                long.TryParse(args[++i], out cityTaskLedgerOf);
                return 0;
            default:
                return 64;
        }
    }

    public static int? TryReadOnly()
    {
        if (!cityTaskConfig)
        {
            return null;
        }

        try
        {
            var cfg = CharacterConfig.Load(configPath);
            var table = LoadCityTasks(cfg, cityTaskTablePath);
            var calendar = LoadCalendar(cfg, calendarPath);
            if (!CalendarCli.TryParseInstant(calendarAt, out var now))
            {
                Console.WriteLine("city_task_at_error=--at 要写成 ISO 8601 或毫秒时间戳："
                    + (calendarAt ?? ""));
                return 2;
            }

            Console.WriteLine("city_task_config_state=ok version=" + table.Version
                + " pending=" + (table.Pending ? "true" : "false")
                + " tasks=" + string.Join(",", table.Tasks.Select(
                    t => t.Id + ":" + t.CityId + ":" + t.Period + ":" + t.MaxPerPeriod
                        + ":level" + t.RequireLevel
                        + ":realm" + (t.RequireRealm.Length == 0 ? "-" : t.RequireRealm)
                        + ":" + string.Join("+", t.Rewards.Select(r => r.Currency + "=" + r.Amount)))));
            Console.WriteLine("city_task_period=day:" + calendar.DayKey(now).ToString("yyyy-MM-dd")
                + " week:" + calendar.WeekStartDate(now).ToString("yyyy-MM-dd")
                + " timezone:" + calendar.TimeZoneId
                + " daily_reset_hour:" + calendar.DailyResetHour);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }
        catch (IOException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        return 0;
    }

    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        if (cityTaskStateOf == 0 && cityTaskCompleteOf == 0 && cityTaskLedgerOf == 0)
        {
            return null;
        }

        CharacterConfig cfg;
        CityTaskRepository repo;
        CityTaskTable table;
        CityTaxRepository cityTax;
        CalendarTable calendar;
        try
        {
            cfg = CharacterConfig.Load(configPath);
            table = LoadCityTasks(cfg, cityTaskTablePath);
            calendar = LoadCalendar(cfg, calendarPath);
            var currencies = CurrencyTable.Load("config/currency.json");
            var items = ItemTable.Load("config/items.json");
            var cities = CityTable.Load(cfg.CityTable);
            var stones = StoneExchangeTable.Load(cfg.StoneExchangeTable, currencies);
            var dungeons = DungeonTable.Load(cfg.DungeonTable);
            var realms = RealmTable.Load(cfg.RealmTable, dungeons, currencies);
            var auction = AuctionTable.Load(cfg.AuctionTable, currencies);
            cityTax = new CityTaxRepository(ctx.DataSource, cities, auction.GoldKind);
            var grants = new GrantRepository(ctx.DataSource, items, currencies, cfg, cityTax);
            repo = new CityTaskRepository(ctx.DataSource, table, calendar, realms, grants, cityTax);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }
        catch (IOException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        if (!CalendarCli.TryParseInstant(calendarAt, out var now))
        {
            Console.WriteLine("city_task_at_error=--at 要写成 ISO 8601 或毫秒时间戳："
                + (calendarAt ?? ""));
            return 2;
        }

        if (cityTaskStateOf != 0)
        {
            var board = await repo.BoardAsync(cityTaskStateOf, now, CancellationToken.None);
            if (!board.Ok)
            {
                Console.WriteLine("city_task_state=reject reason=" + board.Reason
                    + " id=" + cityTaskStateOf);
                return 0;
            }

            Console.WriteLine("city_task_state=ok id=" + cityTaskStateOf
                + " level=" + board.Level
                + " realm=" + board.Realm
                + " stones=" + string.Join("+", board.Stones
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => p.Key + ":" + p.Value))
                + " tasks=" + string.Join(",", board.Rows.Select(
                    r => r.TaskId + ":" + r.Done + "/" + r.MaxPerPeriod
                        + ":" + r.PeriodKey + ":" + (r.Eligible ? "yes" : "no"))));
        }

        if (cityTaskCompleteOf != 0 && !string.IsNullOrWhiteSpace(cityTaskCompleteTask))
        {
            var txnId = string.IsNullOrWhiteSpace(cityTaskTxn)
                ? "city_task_cli:" + Guid.NewGuid().ToString("N")
                : cityTaskTxn!;
            var outcome = await repo.CompleteAsync(
                cityTaskCompleteOf, cityTaskCompleteTask!.Trim(), txnId, now, CancellationToken.None);
            Console.WriteLine("city_task_complete=" + (outcome.Ok ? "ok" : "reject")
                + " id=" + cityTaskCompleteOf
                + " task=" + outcome.TaskId
                + " city=" + (outcome.CityId.Length == 0 ? "-" : outcome.CityId)
                + " reason=" + (outcome.Reason.Length == 0 ? "-" : outcome.Reason)
                + " done=" + outcome.Done
                + " max=" + outcome.MaxPerPeriod
                + " period=" + (outcome.PeriodKey.Length == 0 ? "-" : outcome.PeriodKey)
                + " tax_pct=" + outcome.TaxPct
                + " txn=" + txnId
                + " grants=" + string.Join(",", outcome.Grants.Select(
                    g => g.Currency + ":" + g.Amount + ":" + g.BalanceBefore + "->"
                        + g.BalanceAfter + ":tax" + g.TaxStone)));
        }

        if (cityTaskLedgerOf != 0)
        {
            var rows = await repo.LedgerAsync(cityTaskLedgerOf, 50, CancellationToken.None);
            Console.WriteLine("city_task_ledger=ok id=" + cityTaskLedgerOf + " rows=" + rows.Count);
            foreach (var row in rows)
            {
                Console.WriteLine("city_task_ledger_row=" + row.TxnId
                    + " task=" + row.TaskId
                    + " city=" + row.CityId
                    + " period=" + row.Period
                    + " key=" + row.PeriodKey
                    + " currency=" + row.Currency
                    + " amount=" + row.Amount);
                var tax = await cityTax.ReadStoneRowAsync(
                    row.TxnId, row.Currency, CancellationToken.None);
                Console.WriteLine("city_task_tax_row=" + (tax is null
                        ? "none txn=" + row.TxnId + " currency=" + row.Currency
                        : "txn=" + tax.TxnId
                            + " currency=" + tax.Currency
                            + " city=" + tax.CityId
                            + " source=" + tax.Source
                            + " gross=" + tax.GrossStone
                            + " tax=" + tax.TaxStone));
            }
        }

        return 0;
    }
}
