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
        default:
            Console.WriteLine("unknown_arg=" + args[i]);
            return 64;
    }
}

// 只读核对与清理走这里；正常启动不经过（gRPC 服务在下面）。
if (reportName != null || deleteName != null || stats || migrate || rawLevelWrite != 0 || levelOf != 0
    || bindingOf != null)
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
