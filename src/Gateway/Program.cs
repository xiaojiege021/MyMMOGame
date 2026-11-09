using System.Runtime.InteropServices;
using Gateway;
using Npgsql;

// 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
var configPath = "config/gateway.json";
var sqlPath = "db/character/account.sql";
string? seedAccount = null;
string? deleteAccount = null;
var migrate = false;
var stats = false;

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
        case "--seed-account" when i + 1 < args.Length:
            seedAccount = args[++i];
            break;
        case "--delete-account" when i + 1 < args.Length:
            deleteAccount = args[++i];
            break;
        case "--migrate":
            migrate = true;
            break;
        case "--account-stats":
            stats = true;
            break;
        default:
            Console.WriteLine("unknown_arg=" + args[i]);
            return 64;
    }
}

// 运维子命令要连库；正常启动不需要（缺凭据也能起进程）。
if (migrate || seedAccount != null || deleteAccount != null || stats)
{
    var connectionString = DbEnv.Resolve();
    if (connectionString == null)
    {
        Console.WriteLine("db_env=missing 需要 " + DbEnv.EnvName + " 或 " + DbEnv.DefaultEnvFile);
        return 3;
    }

    Console.WriteLine("db_env=" + DbEnv.Source());

    GatewayConfig config;
    try
    {
        config = GatewayConfig.Load(configPath);
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    await using var dataSource = NpgsqlDataSource.Create(DbEnv.WithPoolLimit(connectionString, 2));
    var accounts = new AccountRepository(dataSource);
    using var stopping = new CancellationTokenSource();

    try
    {
        if (migrate)
        {
            await accounts.MigrateAsync(sqlPath, stopping.Token);
            Console.WriteLine("migrate=ok sql=" + sqlPath);
        }

        if (seedAccount != null)
        {
            // 口令从 stdin 读，不进命令行、不进进程列表。
            var password = Console.ReadLine();
            if (string.IsNullOrEmpty(password))
            {
                Console.WriteLine("seed_error=empty_password");
                return 4;
            }

            await accounts.SeedAsync(seedAccount, password, config.Pbkdf2Iterations, stopping.Token);
            Console.WriteLine("seed=ok account=" + seedAccount);
        }

        if (deleteAccount != null)
        {
            var removed = await accounts.DeleteAsync(deleteAccount, stopping.Token);
            Console.WriteLine("delete=ok account=" + deleteAccount + " rows=" + removed);
        }

        if (stats)
        {
            var s = await accounts.StatsAsync(stopping.Token);
            Console.WriteLine("accounts=" + s.Accounts
                + " distinct_hashes=" + s.DistinctHashes
                + " distinct_salts=" + s.DistinctSalts
                + " hash_bytes=" + s.MinHashBytes + ".." + s.MaxHashBytes);
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

return await new Gateway.GatewayProcess(configPath).RunAsync(stop.Token);
