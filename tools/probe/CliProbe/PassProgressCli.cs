using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>
/// Q6/J4 通行证进度的核对入口：
///   --pass-progress-config                只读，不连库（坏表在这里就 config_error）
///   --pass-progress-table &lt;path&gt;         指到临时表试坏配置
///   --pass-points &lt;角色号&gt;              本季点数、每个来源用了几次
///   --pass-point-award &lt;角色号&gt; &lt;来源&gt;  走一遍加点（与两条服务端事件同一个仓储）
///   --pass-point-ref / --pass-point-txn   这一笔的引用与事务号（不给就现取）
///   --pass-ledger &lt;角色号&gt;               加点流水
/// 季由 --pass-season 显式给，只用于直连核对；网点那条路不带季，季仍归全局服务。
/// 时间点可用 --at 固定（周期键只由 config/calendar.json 算）。
/// </summary>
internal static class PassProgressCli
{
    /// <summary>这几个 flag 归本类解析（CliParser 一行分发，避免把行数推过闸门）。</summary>
    public static bool Handles(string flag) => flag
        is "--pass-progress-config" or "--pass-progress-table" or "--pass-points"
        or "--pass-point-award" or "--pass-point-ref" or "--pass-point-txn" or "--pass-ledger";

    public static int Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--pass-progress-config":
                passProgressConfig = true;
                return 0;
            case "--pass-progress-table" when i + 1 < args.Length:
                passProgressTablePath = args[++i];
                return 0;
            case "--pass-points" when i + 1 < args.Length:
                long.TryParse(args[++i], out passPointsOf);
                return 0;
            case "--pass-point-award" when i + 2 < args.Length:
                long.TryParse(args[++i], out passPointAwardOf);
                passPointSource = args[++i];
                return 0;
            case "--pass-point-ref" when i + 1 < args.Length:
                passPointRef = args[++i];
                return 0;
            case "--pass-point-txn" when i + 1 < args.Length:
                passPointTxn = args[++i];
                return 0;
            case "--pass-ledger" when i + 1 < args.Length:
                long.TryParse(args[++i], out passLedgerOf);
                return 0;
            default:
                return 64;
        }
    }

    public static int? TryReadOnly()
    {
        if (!passProgressConfig)
        {
            return null;
        }

        try
        {
            var cfg = CharacterConfig.Load(configPath);
            var table = LoadSeasonPassProgress(cfg, passProgressTablePath);
            var calendar = LoadCalendar(cfg, calendarPath);
            if (!CalendarCli.TryParseInstant(calendarAt, out var now))
            {
                Console.WriteLine("pass_progress_at_error=--at 要写成 ISO 8601 或毫秒时间戳："
                    + (calendarAt ?? ""));
                return 2;
            }

            Console.WriteLine("pass_progress_config=" + (table.IsMissing ? "missing" : "ok")
                + " version=" + table.Version
                + " pending=" + (table.Pending ? "true" : "false")
                + " points_per_stage=" + table.PointsPerStage
                + " sources=" + table.Sources.Count);
            foreach (var source in table.Sources)
            {
                Console.WriteLine("pass_progress_source=" + source.Id
                    + " kind=" + source.Kind
                    + " dungeon=" + (source.DungeonId.Length == 0 ? "-" : source.DungeonId)
                    + " points=" + source.Points
                    + " period=" + source.Period
                    + " max=" + source.MaxPerPeriod
                    + " key=" + SeasonPassProgressRepository.PeriodKeyOf(source, calendar, now));
            }
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
        if (passPointsOf == 0 && passPointAwardOf == 0 && passLedgerOf == 0)
        {
            return null;
        }

        CharacterConfig cfg;
        SeasonPassProgressTable table;
        CalendarTable calendar;
        try
        {
            cfg = CharacterConfig.Load(configPath);
            table = LoadSeasonPassProgress(cfg, passProgressTablePath);
            calendar = LoadCalendar(cfg, calendarPath);
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

        var season = (seasonPassSeason ?? "").Trim();
        var repo = new SeasonPassProgressRepository(ctx.DataSource, table, calendar);

        if (!CalendarCli.TryParseInstant(calendarAt, out var now))
        {
            Console.WriteLine("pass_progress_at_error=--at 要写成 ISO 8601 或毫秒时间戳："
                + (calendarAt ?? ""));
            return 2;
        }

        if (passPointsOf != 0)
        {
            var state = await repo.StateAsync(passPointsOf, season, now, CancellationToken.None);
            Console.WriteLine("pass_points=" + (season.Length == 0 ? "reject reason=season_missing" : "ok")
                + " id=" + passPointsOf
                + " season=" + (season.Length == 0 ? "-" : state.SeasonId)
                + " points=" + state.Points
                + " per_stage=" + state.PointsPerStage
                + " sources=" + (state.Counts.Count == 0
                    ? "-"
                    : string.Join(",", state.Counts.Select(c => c.SourceId + ":" + c.Done + "/" + c.MaxPerPeriod))));
        }

        if (passPointAwardOf != 0 && !string.IsNullOrWhiteSpace(passPointSource))
        {
            var sourceId = passPointSource!.Trim();
            var source = table.Find(sourceId);
            var reference = (passPointRef ?? "").Trim();
            if (reference.Length == 0 && source is not null)
            {
                // 不给 ref 就按来源自己那一份取：副本用副本编号，主线用最近的一笔。
                reference = source.Kind == SeasonPassProgressTable.DungeonClear
                    ? source.DungeonId
                    : "1";
            }

            var txn = string.IsNullOrWhiteSpace(passPointTxn)
                ? "pass_point_cli:" + Guid.NewGuid().ToString("N")
                : passPointTxn!;
            var outcome = await repo.AwardAsync(
                passPointAwardOf, season, sourceId, reference, txn, now, CancellationToken.None);
            Console.WriteLine("pass_award=" + (outcome.Ok ? "ok" : "reject")
                + " id=" + passPointAwardOf
                + " season=" + (outcome.SeasonId.Length == 0 ? "-" : outcome.SeasonId)
                + " source=" + outcome.SourceId
                + " ref=" + (reference.Length == 0 ? "-" : reference)
                + " period=" + (outcome.PeriodKey.Length == 0 ? "-" : outcome.PeriodKey)
                + " reason=" + (outcome.Reason.Length == 0 ? "-" : outcome.Reason)
                + " points=" + outcome.Points
                + " before=" + outcome.PointsBefore
                + " after=" + outcome.PointsAfter
                + " duplicate=" + (outcome.Duplicate ? "true" : "false")
                + " txn=" + txn);
        }

        if (passLedgerOf != 0)
        {
            var rows = await repo.LedgerAsync(passLedgerOf, season, 50, CancellationToken.None);
            Console.WriteLine("pass_ledger=ok id=" + passLedgerOf + " rows=" + rows.Count);
            foreach (var row in rows)
            {
                Console.WriteLine("pass_ledger_row=" + row.TxnId
                    + " season=" + row.SeasonId
                    + " source=" + row.SourceId
                    + " kind=" + row.Kind
                    + " period=" + row.PeriodKey
                    + " ref=" + (row.RefId.Length == 0 ? "-" : row.RefId)
                    + " points=" + row.Points);
            }
        }

        return 0;
    }
}
