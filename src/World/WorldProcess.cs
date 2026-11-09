using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace World;

public sealed class WorldProcess(string configPath)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        WorldConfig config;
        WorldCatalog catalog;
        try
        {
            config = WorldConfig.Load(configPath);
            catalog = WorldCatalog.Load(config);
        }
        catch (ConfigException ex)
        {
            // 配置不对就起不来，不带着半截编号表跑。
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }
        catch (IOException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        Console.WriteLine("config=" + configPath);
        Console.WriteLine("listen=" + config.Listen);
        Console.WriteLine("region_table_version=" + catalog.Version
            + " regions=" + catalog.Regions.Count
            + " dialogues=" + catalog.Dialogues.Count
            + " speakers=" + catalog.Speakers.Count
            + " factions=" + catalog.Factions.Count);
        Console.WriteLine("line_capacity=" + config.LineCapacity
            + " max_lines_per_region=" + config.MaxLinesPerRegion);
        Console.WriteLine("regions=" + string.Join(",", catalog.Regions.Select(r => r.Id)));
        Console.WriteLine("speakers=" + string.Join(",", catalog.Speakers));
        Console.WriteLine("locked=" + string.Join(",", catalog.Regions.Where(r => !r.Open).Select(r => r.Id)));

        // 分线是在线状态，放进程内存；不连角色库，也不在这里结算技能。
        var registry = new LineRegistry(config, catalog);

        var builder = WebApplication.CreateSlimBuilder();
        // 服与服是明文 gRPC（h2c）：端点必须显式只走 HTTP/2。
        if (!Uri.TryCreate(config.Listen, UriKind.Absolute, out var listenUri)
            || !IPAddress.TryParse(listenUri.Host, out var listenAddress))
        {
            Console.WriteLine("config_error=listen 必须是带 IP 的地址：" + config.Listen);
            return 2;
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(listenAddress, listenUri.Port, options => options.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(catalog);

        var app = builder.Build();
        app.MapGrpcService<WorldGrpcService>();

        // E3 认这一行：进程起来了，gRPC 也在听。
        Console.WriteLine("ready name=World pid=" + Environment.ProcessId);

        try
        {
            await app.RunAsync(stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=World pid=" + Environment.ProcessId);
        return 0;
    }
}
