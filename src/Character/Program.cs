using System.Runtime.InteropServices;
using Character;
using Npgsql;

// 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
var configPath = "config/character.json";
var sqlPath = "db/character/character.sql";
string? reportName = null;
string? deleteName = null;
var stats = false;
var migrate = false;
long rawLevelWrite = 0;
var rawLevelValue = 0;
long levelOf = 0;
string? bindingOf = null;
var bindingSlot = 0;
long rawCurrencyId = 0;
string? rawCurrencyKind = null;
var rawCurrencyAmount = 0L;
string? txnCount = null;
long reservesOf = 0;
long entryCountOf = 0;
long fatigueStateOf = 0;
long setFatigueDayId = 0;
string? setFatigueDay = null;
long rawFatigueId = 0;
var rawFatigueValue = 0;
long shareStateOf = 0;
long setWeekDayId = 0;
string? setWeekDay = null;
long rawShareId = 0;
var rawShareValue = 0;
long sharesTotalOf = 0;
string? sharesTotalDungeon = null;
long expStateOf = 0;
long rawExpId = 0;
var rawExpValue = 0L;
var enhanceState = false;
string? enhanceConfigPath = null;
long enhanceLevelOf = 0;
var enhanceSlotOf = 0;
string? enhanceTxnCount = null;
long rawEnhanceId = 0;
var rawEnhanceSlot = 0;
var rawEnhanceLevel = 0;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--sql" when i + 1 < args.Length:
            sqlPath = args[++i];
            break;
        case "--migrate":
            migrate = true;
            break;
        case "--character-report" when i + 1 < args.Length:
            reportName = args[++i];
            break;
        case "--delete-character" when i + 1 < args.Length:
            deleteName = args[++i];
            break;
        case "--character-stats":
            stats = true;
            break;
        // 只给核对用：绕过接口直写库，验库层 CHECK。不能当运维手段用。
        case "--raw-level-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawLevelWrite);
            int.TryParse(args[++i], out rawLevelValue);
            break;
        case "--level-of" when i + 1 < args.Length:
            long.TryParse(args[++i], out levelOf);
            break;
        // 只给核对用：看这一格的 bound 与 bound_at 有没有一起落对。
        case "--binding-of" when i + 2 < args.Length:
            bindingOf = args[++i];
            int.TryParse(args[++i], out bindingSlot);
            break;
        // 只给核对用：绕过接口直写余额，验库层 CHECK 会不会挡住负数。
        case "--raw-currency-write" when i + 3 < args.Length:
            long.TryParse(args[++i], out rawCurrencyId);
            rawCurrencyKind = args[++i];
            long.TryParse(args[++i], out rawCurrencyAmount);
            break;
        // 只给核对用：这个事务号在表里有几行。
        case "--txn-count" when i + 1 < args.Length:
            txnCount = args[++i];
            break;
        // 只给核对用：读一个角色的疲劳与周份额。
        case "--reserves-of" when i + 1 < args.Length:
            long.TryParse(args[++i], out reservesOf);
            break;
        // 只给核对用：这个角色进过几次本。
        case "--entry-count" when i + 1 < args.Length:
            long.TryParse(args[++i], out entryCountOf);
            break;
        // 只给核对用：读疲劳、周份额与日界（含跨天归零后的状态）。
        case "--fatigue-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out fatigueStateOf);
            break;
        // 只给核对用：把日界改到别的一天，验第二天从 0 起算。
        case "--set-fatigue-day" when i + 2 < args.Length:
            long.TryParse(args[++i], out setFatigueDayId);
            setFatigueDay = args[++i];
            break;
        // 只给核对用：绕过接口直写疲劳，验库层日上限 CHECK。
        case "--raw-fatigue-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawFatigueId);
            int.TryParse(args[++i], out rawFatigueValue);
            break;
        // 只给核对用：读已用份额、剩余份额、周界与疲劳。
        case "--share-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out shareStateOf);
            break;
        // 只给核对用：把周界改到别的周首，验跨周归零。
        case "--set-week-day" when i + 2 < args.Length:
            long.TryParse(args[++i], out setWeekDayId);
            setWeekDay = args[++i];
            break;
        // 只给核对用：绕过接口直写已用份额，造并发现场或验库层非负 CHECK。
        case "--raw-share-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawShareId);
            int.TryParse(args[++i], out rawShareValue);
            break;
        // 只给核对用：这个角色在某个副本累计应得多少单人份。
        case "--shares-total" when i + 2 < args.Length:
            long.TryParse(args[++i], out sharesTotalOf);
            sharesTotalDungeon = args[++i];
            break;
        // 只给核对用：读等级、当前级经验、结算笔数与累计入账经验。
        case "--exp-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out expStateOf);
            break;
        // 只给核对用：绕过接口直写经验，验库层的非负 CHECK。
        case "--raw-exp-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawExpId);
            long.TryParse(args[++i], out rawExpValue);
            break;
        // 只给核对用：把强化表读出来，不连库、不启动进程。坏配置也走这里。
        case "--enhance-state":
            enhanceState = true;
            break;
        case "--enhance-config" when i + 1 < args.Length:
            enhanceConfigPath = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回强化等级、金币与三档石头余量。
        case "--enhance-level" when i + 2 < args.Length:
            long.TryParse(args[++i], out enhanceLevelOf);
            int.TryParse(args[++i], out enhanceSlotOf);
            break;
        // 只给核对用：这个强化事务号在表里有几行。
        case "--enhance-txn-count" when i + 1 < args.Length:
            enhanceTxnCount = args[++i];
            break;
        // 只给核对用：绕过业务层直写强化等级，验库层 CHECK 挡住越界。
        case "--raw-enhance-write" when i + 3 < args.Length:
            long.TryParse(args[++i], out rawEnhanceId);
            int.TryParse(args[++i], out rawEnhanceSlot);
            int.TryParse(args[++i], out rawEnhanceLevel);
            break;
        default:
            Console.WriteLine("unknown_arg=" + args[i]);
            return 64;
    }
}

// 强化表只读核对：与启动共用同一套校验（三档材料要在物品表里、金币要在币种表里），
// 不连库也不启动进程，坏配置能在这一步直接看出来。
if (enhanceState)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var items = ItemTable.Load("config/items.json");
        var currencies = CurrencyTable.Load("config/currency.json");
        var table = EnhanceTable.Load(enhanceConfigPath ?? characterConfig.EnhanceTable, items, currencies);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine("enhance_table_state=ok version=" + table.Version
            + " max_level=" + table.MaxLevel
            + " gold_kind=" + table.GoldKind
            + " tiers=" + string.Join(",", table.Tiers.Select(
                t => t.Id + ":" + t.FromLevel + "-" + t.ToLevel + ":" + t.StoneId))
            + " rates=" + string.Join(",", table.Tiers.Select(
                t => t.SuccessRate.ToString("0.##", culture)))
            + " gold=" + string.Join(",", table.Tiers.Select(t => t.GoldCost))
            + " stones=" + string.Join(",", table.Tiers.Select(t => t.StoneCost)));
        Console.WriteLine("enhance_gate=ok target=7 tier=" + table.TierFor(7).Id
            + " stone=" + table.TierFor(7).StoneId
            + " target=12 tier=" + table.TierFor(12).Id);
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

// 只读核对与清理走这里；正常启动不经过（gRPC 服务在下面）。
if (reportName != null || deleteName != null || stats || migrate || rawLevelWrite != 0 || levelOf != 0
    || bindingOf != null || rawCurrencyId != 0 || txnCount != null
    || reservesOf != 0 || entryCountOf != 0 || fatigueStateOf != 0 || setFatigueDay != null
    || rawFatigueId != 0 || shareStateOf != 0 || setWeekDay != null
    || rawShareId != 0 || sharesTotalDungeon != null || expStateOf != 0 || rawExpId != 0
    || enhanceLevelOf != 0 || enhanceTxnCount != null || rawEnhanceId != 0)
{
    var connectionString = DbEnv.Resolve();
    if (connectionString == null)
    {
        Console.WriteLine("db_env=missing 需要 " + DbEnv.EnvName + " 或 " + DbEnv.DefaultEnvFile);
        return 3;
    }

    Console.WriteLine("db_env=" + DbEnv.Source());
    await using var dataSource = NpgsqlDataSource.Create(DbEnv.WithPoolLimit(connectionString, 2));
    var characters = new CharacterRepository(dataSource);

    try
    {
        if (migrate)
        {
            var sql = File.ReadAllText(sqlPath);
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("migrate=ok sql=" + sqlPath);
        }

        if (deleteName != null)
        {
            var removed = await characters.DeleteAsync(deleteName, CancellationToken.None);
            Console.WriteLine("delete=ok name=" + deleteName + " rows=" + removed);
        }

        if (stats)
        {
            Console.WriteLine("characters=" + await characters.CountAsync(CancellationToken.None));
        }

        if (rawLevelWrite != 0)
        {
            try
            {
                await characters.RawWriteLevelAsync(rawLevelWrite, rawLevelValue, CancellationToken.None);
                Console.WriteLine("raw_level=written id=" + rawLevelWrite + " level=" + rawLevelValue);
            }
            catch (PostgresException ex)
            {
                // 核对要看到的就是这个：库层的 CHECK 把越界值挡住了。
                Console.WriteLine("raw_level=rejected id=" + rawLevelWrite
                    + " level=" + rawLevelValue + " sqlstate=" + ex.SqlState);
            }
        }

        if (levelOf != 0)
        {
            var found = await characters.LevelOfAsync(levelOf, CancellationToken.None);
            Console.WriteLine(found is null
                ? "level=missing id=" + levelOf
                : "level=ok id=" + levelOf + " account=" + found.Value.Account + " level=" + found.Value.Level);
        }

        if (bindingOf != null)
        {
            var inventory = new InventoryRepository(dataSource);
            var found = await inventory.BindingOfAsync(bindingOf, bindingSlot, CancellationToken.None);
            Console.WriteLine(found is null
                ? "binding=missing name=" + bindingOf + " slot=" + bindingSlot
                : "binding=ok name=" + bindingOf + " slot=" + bindingSlot
                  + " bound=" + found.Value.Bound + " bound_at_present=" + found.Value.BoundAtPresent);
        }

        if (rawCurrencyId != 0 && rawCurrencyKind != null)
        {
            var money = new CurrencyRepository(dataSource);
            try
            {
                await money.RawWriteAsync(rawCurrencyId, rawCurrencyKind, rawCurrencyAmount, CancellationToken.None);
                Console.WriteLine("raw_currency=written id=" + rawCurrencyId
                    + " kind=" + rawCurrencyKind + " amount=" + rawCurrencyAmount);
            }
            catch (PostgresException ex)
            {
                // 核对要看到的就是这个：库层的 CHECK 把负数挡住了。
                Console.WriteLine("raw_currency=rejected id=" + rawCurrencyId
                    + " kind=" + rawCurrencyKind + " amount=" + rawCurrencyAmount
                    + " sqlstate=" + ex.SqlState);
            }
        }

        if (txnCount != null)
        {
            // 只数行，不经过发奖逻辑，免得核对自己去抢号。
            await using var command = dataSource.CreateCommand(
                "select count(*) from \"角色\".grant_txn where txn_id = $1");
            command.Parameters.AddWithValue(txnCount);
            var value = await command.ExecuteScalarAsync(CancellationToken.None);
            Console.WriteLine("txn_count=" + Convert.ToInt32(value) + " txn=" + txnCount);
        }

        if (reservesOf != 0)
        {
            var found = await characters.ReservesAsync(reservesOf, CancellationToken.None);
            Console.WriteLine(found is null
                ? "reserves=missing id=" + reservesOf
                : "reserves=ok id=" + reservesOf
                  + " fatigue=" + found.Value.Fatigue
                  + " weekly_share=" + found.Value.WeeklyShare);
        }

        if (entryCountOf != 0)
        {
            await using var command = dataSource.CreateCommand(
                "select count(*) from \"角色\".dungeon_entry where character_id = $1");
            command.Parameters.AddWithValue(entryCountOf);
            var value = await command.ExecuteScalarAsync(CancellationToken.None);
            Console.WriteLine("entry_count=" + Convert.ToInt32(value) + " id=" + entryCountOf);
        }

        if (fatigueStateOf != 0 || setFatigueDay != null || rawFatigueId != 0)
        {
            var dungeonEntries = new DungeonEntryRepository(dataSource);

            if (fatigueStateOf != 0)
            {
                var state = await dungeonEntries.FatigueStateAsync(fatigueStateOf, CancellationToken.None);
                Console.WriteLine(state is null
                    ? "fatigue_state=missing id=" + fatigueStateOf
                    : "fatigue_state=ok id=" + fatigueStateOf
                      + " fatigue=" + state.Value.Fatigue
                      + " weekly_share=" + state.Value.WeeklyShare
                      + " day=" + state.Value.Day.ToString("yyyy-MM-dd"));
            }

            if (setFatigueDay != null && setFatigueDayId != 0
                && DateOnly.TryParse(setFatigueDay, out var day))
            {
                await dungeonEntries.SetFatigueDayAsync(setFatigueDayId, day, CancellationToken.None);
                Console.WriteLine("fatigue_day=set id=" + setFatigueDayId + " day=" + day.ToString("yyyy-MM-dd"));
            }

            if (rawFatigueId != 0)
            {
                try
                {
                    await dungeonEntries.RawWriteFatigueAsync(
                        rawFatigueId, rawFatigueValue, CancellationToken.None);
                    Console.WriteLine("raw_fatigue=written id=" + rawFatigueId
                        + " fatigue=" + rawFatigueValue);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层的 CHECK 把超过日上限的值挡住了。
                    Console.WriteLine("raw_fatigue=rejected id=" + rawFatigueId
                        + " fatigue=" + rawFatigueValue + " sqlstate=" + ex.SqlState);
                }
            }
        }

        if (shareStateOf != 0 || setWeekDay != null || rawShareId != 0 || sharesTotalDungeon != null)
        {
            var weeklyShares = new WeeklyShareRepository(dataSource);

            if (shareStateOf != 0)
            {
                // 上限从同一个副本表读，和跑着的角色服看到的是同一份。
                var weeklyCap = DungeonTable.Load(CharacterConfig.Load(configPath).DungeonTable).WeeklyShareCap;
                var state = await weeklyShares.ShareStateAsync(shareStateOf, weeklyCap, CancellationToken.None);
                Console.WriteLine(state is null
                    ? "share_state=missing id=" + shareStateOf
                    : "share_state=ok id=" + shareStateOf
                      + " used=" + state.Value.Used
                      + " remaining=" + state.Value.Remaining
                      + " fatigue=" + state.Value.Fatigue
                      + " week=" + state.Value.Week.ToString("yyyy-MM-dd"));
            }

            if (setWeekDay != null && setWeekDayId != 0
                && DateOnly.TryParse(setWeekDay, out var week))
            {
                await weeklyShares.SetWeeklyShareDayAsync(setWeekDayId, week, CancellationToken.None);
                Console.WriteLine("week_day=set id=" + setWeekDayId + " day=" + week.ToString("yyyy-MM-dd"));
            }

            if (rawShareId != 0)
            {
                try
                {
                    await weeklyShares.RawWriteShareAsync(rawShareId, rawShareValue, CancellationToken.None);
                    Console.WriteLine("raw_share=written id=" + rawShareId + " used=" + rawShareValue);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层 CHECK 把负份额挡住了。
                    Console.WriteLine("raw_share=rejected id=" + rawShareId
                        + " used=" + rawShareValue + " sqlstate=" + ex.SqlState);
                }
            }

            if (sharesTotalDungeon != null && sharesTotalOf != 0)
            {
                var total = await weeklyShares.SharesTotalAsync(
                    sharesTotalOf, sharesTotalDungeon, CancellationToken.None);
                Console.WriteLine("shares_total=" + total + " id=" + sharesTotalOf
                    + " dungeon=" + sharesTotalDungeon);
            }
        }

        if (expStateOf != 0 || rawExpId != 0)
        {
            var expSettlements = new ExpSettlementRepository(dataSource);

            if (expStateOf != 0)
            {
                var state = await expSettlements.ExpStateAsync(expStateOf, CancellationToken.None);
                Console.WriteLine(state is null
                    ? "exp_state=missing id=" + expStateOf
                    : "exp_state=ok id=" + expStateOf
                      + " level=" + state.Value.Level
                      + " exp=" + state.Value.Exp
                      + " rows=" + state.Value.Rows
                      + " total_gain=" + state.Value.TotalGain);
            }

            if (rawExpId != 0)
            {
                try
                {
                    await expSettlements.RawWriteExpAsync(rawExpId, rawExpValue, CancellationToken.None);
                    Console.WriteLine("raw_exp=written id=" + rawExpId + " exp=" + rawExpValue);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层 CHECK 把负经验挡住了。
                    Console.WriteLine("raw_exp=rejected id=" + rawExpId
                        + " exp=" + rawExpValue + " sqlstate=" + ex.SqlState);
                }
            }
        }

        if (enhanceLevelOf != 0 || enhanceTxnCount != null || rawEnhanceId != 0)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var enhanceTable = EnhanceTable.Load(
                enhanceConfigPath ?? characterConfig.EnhanceTable,
                items, CurrencyTable.Load("config/currency.json"));
            var enhances = new EnhanceRepository(dataSource, enhanceTable, items);

            if (rawEnhanceId != 0)
            {
                try
                {
                    await enhances.RawWriteLevelAsync(
                        rawEnhanceId, rawEnhanceSlot, rawEnhanceLevel, CancellationToken.None);
                    Console.WriteLine("raw_enhance=written id=" + rawEnhanceId
                        + " slot=" + rawEnhanceSlot + " level=" + rawEnhanceLevel);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层 CHECK 把越界的强化等级挡住了。
                    Console.WriteLine("raw_enhance=rejected id=" + rawEnhanceId
                        + " level=" + rawEnhanceLevel + " sqlstate=" + ex.SqlState);
                }
            }

            if (enhanceLevelOf != 0)
            {
                var state = await enhances.StateAsync(enhanceLevelOf, enhanceSlotOf, CancellationToken.None);
                Console.WriteLine("enhance_state=ok id=" + enhanceLevelOf
                    + " slot=" + enhanceSlotOf
                    + " level=" + state.Level
                    + " gold=" + state.Gold
                    + " low=" + state.Low
                    + " mid=" + state.Mid
                    + " high=" + state.High);
            }

            if (enhanceTxnCount != null)
            {
                Console.WriteLine("enhance_txn_count="
                    + await enhances.TxnCountAsync(enhanceTxnCount, CancellationToken.None)
                    + " txn=" + enhanceTxnCount);
            }
        }

        if (reportName != null)
        {
            var report = await characters.ReportAsync(reportName, CancellationToken.None);
            if (report is null)
            {
                Console.WriteLine("report=missing name=" + reportName);
            }
            else
            {
                Console.WriteLine("report=ok name=" + reportName
                    + " id=" + report.Id
                    + " race=" + report.Race
                    + " class=" + report.ClassId
                    + " branch=" + report.Branch
                    + " level=" + report.Level
                    + " realm=" + report.Realm
                    + " main_stage_null=" + report.MainStageEmpty
                    + " alignment_null=" + report.AlignmentEmpty
                    + " appearance_len=" + report.AppearanceLength);
            }
        }
    }
    catch (Exception ex) when (ex is NpgsqlException or IOException)
    {
        Console.WriteLine("db_error=" + ex.GetType().Name + " " + ex.Message);
        return 5;
    }

    return 0;
}

using var stop = new CancellationTokenSource();
PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.Cancel();
});
PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    stop.Cancel();
});

return await new CharacterProcess(configPath).RunAsync(stop.Token);
