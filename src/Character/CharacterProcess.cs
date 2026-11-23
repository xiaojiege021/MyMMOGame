using Npgsql;

namespace Character;

/// <summary>
/// 角色服启动（D2d：入口只留装配顺序）。四段都在 Startup/ 下，行为与原文件一致：
/// 读配置与表 → 回显事实值 → 连库补齐城池行 → 装配仓储与 gRPC 宿主 → 跑起来。
/// </summary>
public sealed class CharacterProcess(string configPath)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        CharacterConfig config;
        CharacterTables tables;
        try
        {
            config = CharacterConfig.Load(configPath);
            // 表装载失败与原文件同一出口：config_error + 退出码 2。
            tables = TableLoad.Load(config, configPath);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        BootReport.Print(config, configPath, tables);

        var connectionString = DbEnv.Resolve();
        Console.WriteLine("db_env=" + DbEnv.Source());

        if (connectionString == null)
        {
            // 角色数据没有库就没法建号，这里直接不起来，免得看起来在跑其实什么都做不了。
            Console.WriteLine("db_missing=1 需要 MMO_GAMEDB 或 " + DbEnv.DefaultEnvFile);
            return 3;
        }

        await using var dataSource = NpgsqlDataSource.Create(
            DbEnv.WithPoolLimit(connectionString, config.DbPoolMax));

        await SeedCitiesAsync(dataSource, tables.CityTable, stopping);

        var services = ServiceAssembly.Build(dataSource, config, tables);

        WebApplication app;
        try
        {
            app = GrpcHost.Build(config, tables, services);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        // E3 认这一行：进程起来了，gRPC 也在听。
        Console.WriteLine("ready name=Character pid=" + Environment.ProcessId);

        try
        {
            await app.RunAsync(stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Character pid=" + Environment.ProcessId);
        return 0;
    }

    /// <summary>
    /// P3-3a4：config/city.json 是城池的正本，库里这一行是运行时事实。
    /// 启动时把配置里登记的城池补进库；已存在的行（含城战写回过的 owner / tax_pct）
    /// 一律不覆盖，免得每次重启把归属改回空。只跑分表迁移的老卡环境可能还没有这张表，
    /// 那就跳过，不让启动被拖垮。K7：只插静态三列，另两列走库默认。
    /// </summary>
    static async Task SeedCitiesAsync(
        NpgsqlDataSource dataSource, CityTable cityTable, CancellationToken stopping)
    {
        try
        {
            foreach (var city in cityTable.Cities)
            {
                await using var seed = dataSource.CreateCommand(
                    "insert into \"社交\".city (city_id, name, vein_id) "
                    + "values ($1, $2, $3) on conflict (city_id) do nothing");
                seed.Parameters.AddWithValue(city.CityId);
                seed.Parameters.AddWithValue(city.Name);
                seed.Parameters.AddWithValue(city.VeinId);
                await seed.ExecuteNonQueryAsync(stopping);
            }
            Console.WriteLine("city_seed=ok cities=" + cityTable.Cities.Count);
        }
        catch (NpgsqlException ex)
        {
            Console.WriteLine("city_seed=skipped " + ex.GetType().Name);
        }
    }
}
