using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace World;

public sealed class WorldProcess(string configPath, string gatherConfigPath)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        WorldConfig config;
        WorldCatalog catalog;
        GatherTable gather;
        try
        {
            config = WorldConfig.Load(configPath);
            catalog = WorldCatalog.Load(config);
            // 节点必须长在已经配置的地表上；采集表坏掉同样不带半截配置跑。
            gather = GatherTable.Load(
                gatherConfigPath,
                catalog.Regions.Select(r => r.Id).ToHashSet(StringComparer.Ordinal));
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
        Console.WriteLine("position_config=ok sync_hz=" + config.SyncHz
            + " max_move_speed=" + config.MaxMoveSpeed
            + " spawn=" + config.SpawnX + "," + config.SpawnY + "," + config.SpawnZ
            + "," + config.SpawnFacing);
        Console.WriteLine("tier_config=ok near=" + config.TierNearDistance
            + " far=" + config.TierFarDistance);
        Console.WriteLine("gather_config=ok nodes=" + gather.Nodes.Count
            + " kinds=" + string.Join(",", gather.Nodes.Select(n => n.Kind).Distinct())
            + " forbidden=" + gather.Nodes.Count(n => n.Kind == GatherTable.SpiritStone));
        Console.WriteLine("regions=" + string.Join(",", catalog.Regions.Select(r => r.Id)));
        Console.WriteLine("speakers=" + string.Join(",", catalog.Speakers));
        Console.WriteLine("locked=" + string.Join(",", catalog.Regions.Where(r => !r.Open).Select(r => r.Id)));

        // 分线、位置、同屏档位与采集占用都是在线状态，放进程内存；不连角色库，
        // 也不在这里结算技能。采集产物经 gRPC 交给角色服，那边才是唯一写入方。
        var registry = new LineRegistry(config, catalog);
        var tiers = new AppearanceTierTable(config);
        var hub = new PositionHub(registry, config, tiers);
        var gatherNodes = new GatherNodeRegistry();
        using var gatherGrants = new GrpcGatherGrant(gather.CharacterUpstream);

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
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(tiers);
        builder.Services.AddSingleton(hub);
        builder.Services.AddSingleton(gather);
        builder.Services.AddSingleton(gatherNodes);
        builder.Services.AddSingleton<IGatherGrant>(gatherGrants);
        builder.Services.AddHostedService<PositionBroadcaster>();

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
