using System.Net;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Mmo.Proto.Character;
using Npgsql;

namespace Global;

public sealed class GlobalProcess(string configPath)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        GlobalConfig config;
        try
        {
            config = GlobalConfig.Load(configPath);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        Console.WriteLine("config=" + configPath);
        Console.WriteLine("listen=" + config.Listen);
        Console.WriteLine("character_upstream=" + config.CharacterUpstream);

        var connectionString = DbEnv.Resolve();
        Console.WriteLine("db_env=" + DbEnv.Source());
        if (connectionString == null)
        {
            Console.WriteLine("db_missing=1 需要 " + DbEnv.EnvName + " 或 " + DbEnv.DefaultEnvFile);
            return 3;
        }

        await using var dataSource = NpgsqlDataSource.Create(
            DbEnv.WithPoolLimit(connectionString, config.DbPoolMax));
        var listings = new AuctionListingRepository(dataSource);

        // 成交要转给角色服：货币、物品与成交状态都在那一笔角色服事务里。
        var channel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(channel);

        var builder = WebApplication.CreateSlimBuilder();
        if (!Uri.TryCreate(config.Listen, UriKind.Absolute, out var listenUri)
            || !IPAddress.TryParse(listenUri.Host, out var listenAddress))
        {
            Console.WriteLine("config_error=listen 必须是带 IP 的地址：" + config.Listen);
            return 2;
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(listenAddress, listenUri.Port, options => options.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(listings);
        builder.Services.AddSingleton(character);
        builder.Services.AddSingleton(config);

        var app = builder.Build();
        app.MapGrpcService<GlobalGrpcService>();

        Console.WriteLine("ready name=Global pid=" + Environment.ProcessId);
        try
        {
            await app.RunAsync(stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Global pid=" + Environment.ProcessId);
        return 0;
    }
}
