using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;

namespace Character;

public sealed class CharacterProcess(string configPath)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        CharacterConfig config;
        try
        {
            config = CharacterConfig.Load(configPath);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        // 三张表：出身、分支、职业编号。种族与分支都不带属性，只决定外观、对白与兵器。
        RaceTable races;
        BranchTable branches;
        ClassIds classes;
        ItemTable items;
        CurrencyTable currencies;
        DungeonTable dungeons;
        try
        {
            races = RaceTable.Load("config/races.json");
            branches = BranchTable.Load("config/branches.json");
            classes = new ClassIds("config/classes.json");
            items = ItemTable.Load("config/items.json");
            currencies = CurrencyTable.Load("config/currency.json");
            dungeons = DungeonTable.Load(config.DungeonTable);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        Console.WriteLine("config=" + configPath);
        Console.WriteLine("listen=" + config.Listen);
        Console.WriteLine("races=" + races.All.Count + " race_table_version=" + races.Version);
        Console.WriteLine("branches=" + branches.All.Count + " branch_table_version=" + branches.Version);
        Console.WriteLine("items=" + items.All.Count + " item_table_version=" + items.Version
            + " bag_slots=" + config.BagSlots);
        Console.WriteLine("currencies=" + currencies.All.Count + " currency_table_version=" + currencies.Version);
        Console.WriteLine("dungeons=" + dungeons.All.Count + " dungeon_table_version=" + dungeons.Version
            + " fatigue_daily_cap=" + dungeons.FatigueDailyCap
            + " hot_spring_recovery=" + dungeons.HotSpringRecovery);

        var connectionString = DbEnv.Resolve();
        Console.WriteLine("db_env=" + DbEnv.Source());

        if (connectionString == null)
        {
            // 角色数据没有库就没法建号，这里直接不起来，免得看起来在跑其实什么都做不了。
            Console.WriteLine("db_missing=1 需要 MMO_GAMEDB 或 " + DbEnv.DefaultEnvFile);
            return 3;
        }

        await using var dataSource = NpgsqlDataSource.Create(DbEnv.WithPoolLimit(connectionString, config.DbPoolMax));
        var characters = new CharacterRepository(dataSource);
        var inventory = new InventoryRepository(dataSource);
        var currency = new CurrencyRepository(dataSource);
        var grants = new GrantRepository(dataSource, items, currencies, config);
        var dungeonEntries = new DungeonEntryRepository(dataSource);
        var hotSpring = new HotSpringRepository(dataSource);

        var builder = WebApplication.CreateSlimBuilder();
        // 服与服是明文 gRPC（h2c）：端点必须显式只走 HTTP/2，
        // 否则 Kestrel 在明文上只谈 HTTP/1.1，gRPC 握手直接失败。
        if (!Uri.TryCreate(config.Listen, UriKind.Absolute, out var listenUri)
            || !IPAddress.TryParse(listenUri.Host, out var listenAddress))
        {
            Console.WriteLine("config_error=listen 必须是带 IP 的地址：" + config.Listen);
            return 2;
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(listenAddress, listenUri.Port, options => options.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(characters);
        builder.Services.AddSingleton(inventory);
        builder.Services.AddSingleton(currency);
        builder.Services.AddSingleton(grants);
        builder.Services.AddSingleton(races);
        builder.Services.AddSingleton(branches);
        builder.Services.AddSingleton(classes);
        builder.Services.AddSingleton(items);
        builder.Services.AddSingleton(currencies);
        builder.Services.AddSingleton(dungeons);
        builder.Services.AddSingleton(dungeonEntries);
        builder.Services.AddSingleton(hotSpring);
        builder.Services.AddSingleton(config);

        var app = builder.Build();
        app.MapGrpcService<CharacterGrpcService>();

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
}
