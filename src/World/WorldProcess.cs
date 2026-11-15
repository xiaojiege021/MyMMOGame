using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace World;

public sealed class WorldProcess(
    string configPath, string gatherConfigPath, string flightConfigPath,
    string worldBossConfigPath, string gatherSpreadConfigPath)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        WorldConfig config;
        WorldCatalog catalog;
        GatherTable gather;
        GatherTable gatherAll;
        GatherSpreadTable? gatherSpread;
        FlightTable flights;
        WorldBossTable? worldBossTable;
        try
        {
            config = WorldConfig.Load(configPath);
            catalog = WorldCatalog.Load(config);
            var regionIds = catalog.Regions.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            // 节点必须长在已经配置的地表上；采集表坏掉同样不带半截配置跑。
            gather = GatherTable.Load(gatherConfigPath, regionIds);
            // 每块已配置地表都必须有境界门槛，漏一块就等于给它开零门槛。
            flights = FlightTable.Load(flightConfigPath, regionIds);
            // C2 铺开表是可选的：老卡的临时配置目录里没有它，就按 P1-3b 的四个基准点照常跑。
            // 地表境界区域与已开放段都复用 flight.json，不另起一套境界正本。
            gatherSpread = GatherSpreadTable.LoadIfPresent(
                gatherSpreadConfigPath, regionIds, flights, gather.Nodes);
            // 运行期唯一节点集 = 基准四点 + 铺开节点；采集判定与角色服发奖链路一字不改。
            gatherAll = gatherSpread is null ? gather : gather.WithSpread(gatherSpread.Nodes);
            // 世界 Boss 是可选表：入口必须长在已经开放的地表上。缺表 / 坏表一律降级成
            // 「没有入口」，不让一份可选配置把分线、采集、飞行这些老链路一起拖垮。
            worldBossTable = LoadWorldBoss(worldBossConfigPath, catalog);
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
        // 这行是 P1-3b / P1-3c 老卡钉住的口径，仍只报基准表，不把铺开节点算进来。
        Console.WriteLine("gather_config=ok nodes=" + gather.Nodes.Count
            + " kinds=" + string.Join(",", gather.Nodes.Select(n => n.Kind).Distinct())
            + " forbidden=" + gather.Nodes.Count(n => n.Kind == GatherTable.SpiritStone));
        Console.WriteLine(gatherSpread is null
            ? "gather_spread=missing"
            : "gather_spread=ok nodes=" + gatherSpread.Nodes.Count
                + " regions=" + gatherSpread.RegionCount
                + " realms=" + gatherSpread.Realms
                + " total=" + gatherAll.Nodes.Count);
        Console.WriteLine("flight_config=ok realms=" + string.Join(",", flights.Realms)
            + " flight_realm=" + flights.FlightRealm
            + " region_realms=" + string.Join(",", catalog.Regions.Select(
                r => r.Id + ":" + flights.RequiredFor(r.Id)))
            + " ground_y=" + config.SpawnY);
        Console.WriteLine("world_boss_config=ok " + DescribeWorldBossTable(worldBossTable, worldBossConfigPath));
        Console.WriteLine("regions=" + string.Join(",", catalog.Regions.Select(r => r.Id)));
        Console.WriteLine("speakers=" + string.Join(",", catalog.Speakers));
        Console.WriteLine("locked=" + string.Join(",", catalog.Regions.Where(r => !r.Open).Select(r => r.Id)));

        // 分线、位置、同屏档位与采集占用都是在线状态，放进程内存；不连角色库，
        // 也不在这里结算技能。采集产物经 gRPC 交给角色服，那边才是唯一写入方。
        var registry = new LineRegistry(config, catalog);
        var tiers = new AppearanceTierTable(config);
        var hub = new PositionHub(registry, config, tiers);
        var gatherNodes = new GatherNodeRegistry();
        using var gatherGrants = new GrpcGatherGrant(gatherAll.CharacterUpstream);
        // 境界只在角色服：世界服每次判飞行/跨区都去问，不连角色库，也不自己存境界。
        using var realmReader = new GrpcRealmReader(flights.CharacterUpstream);
        var flightGate = new FlightGate(flights, realmReader, registry, catalog);
        // 入口的在线状态只在世界服：“这一刻开没开、这一场打掉没有、名字记的是谁”。
        // 全在进程内存，不落库、不结算技能、不碰任何角色数据。
        var worldBoss = new WorldBossEntrance(worldBossTable);

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
        builder.Services.AddSingleton(gatherAll);
        builder.Services.AddSingleton(gatherNodes);
        builder.Services.AddSingleton<IGatherGrant>(gatherGrants);
        builder.Services.AddSingleton(flights);
        builder.Services.AddSingleton(flightGate);
        builder.Services.AddSingleton(worldBoss);
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

    /// <summary>
    /// 世界 Boss 表是可选表：入口必须长在已经开放的地表上。缺表与坏表都降级成
    /// 「没有入口」，只打一行 config 或 error，不把分线、采集、飞行一起拖下水。
    /// </summary>
    static WorldBossTable? LoadWorldBoss(string path, WorldCatalog catalog)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine("world_boss_config=missing path=" + path);
            return null;
        }

        try
        {
            var open = catalog.Regions.Where(r => r.Open).Select(r => r.Id)
                .ToHashSet(StringComparer.Ordinal);
            return WorldBossTable.Load(path, open);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("world_boss_config_error=" + ex.Message);
            return null;
        }
        catch (IOException ex)
        {
            Console.WriteLine("world_boss_config_error=" + ex.Message);
            return null;
        }
    }

    static string DescribeWorldBossTable(WorldBossTable? table, string path) =>
        table is null
            ? "missing path=" + path
            : "version=" + table.Version
              + " entrance=" + table.EntranceId
              + " region=" + table.RegionId
              + " cap=" + table.Cap
              + " windows=" + table.Windows.Count
              + " pending=" + (table.Pending ? "true" : "false")
              + " path=" + path;
}
