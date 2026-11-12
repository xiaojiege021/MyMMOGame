using System.Runtime.InteropServices;
using Npgsql;
using Social;

// 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
var configPath = "config/social.json";
string? sectTablePath = null;
var sqlPath = "db/social/social.sql";
var migrate = false;
var sectConfig = false;
var sectList = false;
long sectStateOf = 0;
long sectMembersOf = 0;
long sectSetLevelOf = 0;
var sectSetLevelTo = 0;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--sect-table" when i + 1 < args.Length:
            sectTablePath = args[++i];
            break;
        case "--sql" when i + 1 < args.Length:
            sqlPath = args[++i];
            break;
        case "--migrate":
            migrate = true;
            break;
        case "--sect-config":
            sectConfig = true;
            break;
        case "--sect-list":
            sectList = true;
            break;
        case "--sect-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectStateOf);
            break;
        case "--sect-members" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectMembersOf);
            break;
        // 只给核对用：把某个宗门的 level 改到指定档，验上限切换，不做升级逻辑。
        case "--sect-set-level" when i + 2 < args.Length:
            long.TryParse(args[++i], out sectSetLevelOf);
            int.TryParse(args[++i], out sectSetLevelTo);
            break;
        default:
            Console.WriteLine("unknown_arg=" + args[i]);
            return 64;
    }
}

// --sect-config：不连库，逐档打印配置正本。坏配置在这一步就起不来
// （config_error= + 非 0 退出），与真实启动共用同一份 Load + 同一组参数。
if (sectConfig)
{
    try
    {
        var loaded = SocialLoader.LoadAll(configPath, sectTablePath);
        var table = loaded.Sect;
        Console.WriteLine("sect_config_state=ok version=" + table.Version
            + " levels=" + string.Join(",", table.Levels.Select(l => l.Number + ":" + l.MemberCap))
            + " create_gate=" + table.Create.FounderLevelGate
            + " create_gold_kind=" + table.Create.FounderGoldKind
            + " create_gold_cost=" + table.Create.FounderGoldCost
            + " create_gold_pending=" + table.Create.FounderGoldCostPending);
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

// 连库入口：--migrate / --sect-list / --sect-state / --sect-members /
// --sect-set-level。都先过与真实启动同一份 Load + 同一组参数。
if (migrate || sectList || sectStateOf != 0 || sectMembersOf != 0 || sectSetLevelOf != 0)
{
    SectTable table;
    try
    {
        table = SocialLoader.LoadAll(configPath, sectTablePath).Sect;
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

    var connectionString = DbEnv.Resolve();
    if (connectionString == null)
    {
        Console.WriteLine("db_env=missing 需要 " + DbEnv.EnvName + " 或 " + DbEnv.DefaultEnvFile);
        return 3;
    }

    Console.WriteLine("db_env=" + DbEnv.Source());
    await using var dataSource = NpgsqlDataSource.Create(DbEnv.WithPoolLimit(connectionString, 2));
    var repo = new SectRepository(dataSource, table);

    try
    {
        if (migrate)
        {
            var sql = File.ReadAllText(sqlPath);
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("migrate=ok sql=" + sqlPath);
        }

        if (sectList)
        {
            var all = await repo.ListAsync(CancellationToken.None);
            Console.WriteLine("sect_list_state=ok rows=" + all.Count
                + " sects=" + string.Join(",", all.Select(
                    s => s.SectId + ":" + s.Name + ":" + s.Level + ":" + s.MemberCount)));
        }

        if (sectStateOf != 0)
        {
            var state = await repo.StateAsync(sectStateOf, CancellationToken.None);
            Console.WriteLine(state.Found
                ? "sect_state=ok sect=" + state.SectId
                    + " name=" + state.Name
                    + " level=" + state.Level
                    + " member_cap=" + state.MemberCap
                    + " member_count=" + state.MemberCount
                : "sect_state=missing sect=" + sectStateOf);
        }

        if (sectMembersOf != 0)
        {
            var members = await repo.MembersAsync(sectMembersOf, CancellationToken.None);
            Console.WriteLine("sect_members_state=ok sect=" + sectMembersOf
                + " rows=" + members.Count
                + " ids=" + string.Join(",", members));
        }

        if (sectSetLevelOf != 0 && sectSetLevelTo > 0)
        {
            var changed = await repo.SetLevelAsync(sectSetLevelOf, sectSetLevelTo, CancellationToken.None);
            Console.WriteLine("sect_set_level=" + (changed ? "ok" : "missing")
                + " sect=" + sectSetLevelOf + " level=" + sectSetLevelTo);
        }
    }
    catch (Exception ex) when (ex is NpgsqlException or IOException)
    {
        Console.WriteLine("db_error=" + ex.GetType().Name + " " + ex.Message);
        return 5;
    }

    return 0;
}

// 真实启动：与上面所有只读入口共用同一份 Load + 同一组参数。
(SocialConfig Config, SectTable Sect) startup;
try
{
    startup = SocialLoader.LoadAll(configPath, sectTablePath);
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

return await new SocialProcess(startup.Config, startup.Sect).RunAsync(stop.Token);
