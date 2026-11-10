using System.Runtime.InteropServices;
using World;

// 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
var configPath = "config/world.json";
var gatherConfigPath = "config/gather.json";
var regionState = false;
var positionState = false;
var tierState = false;
var gatherState = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        // 采集表单独一份，核对脚本用临时配置跑，不碰仓库里那份。
        case "--gather-config" when i + 1 < args.Length:
            gatherConfigPath = args[++i];
            break;
        // 只给核对用：把读表后的节点与产物打出来，不启动进程。
        case "--gather-state":
            gatherState = true;
            break;
        // 只给核对用：把读表后的地点与人物编号打出来，不启动进程。
        case "--region-state":
            regionState = true;
            break;
        // 只给核对用：把位置同步的配置读出来，不启动进程。
        case "--position-state":
            positionState = true;
            break;
        // 只给核对用：把同屏三档的阈值与分档结果读出来，不启动进程。
        case "--tier-state":
            tierState = true;
            break;
        default:
            Console.WriteLine("unknown_arg=" + args[i]);
            return 64;
    }
}

if (gatherState)
{
    GatherTable table;
    try
    {
        table = GatherTable.Load(gatherConfigPath);
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

    var ore = table.Nodes.First(n => n.Kind == GatherTable.Ore);
    var herb = table.Nodes.First(n => n.Kind == GatherTable.Herb);
    Console.WriteLine("gather_state=ok nodes=" + table.Nodes.Count
        + " ore=" + ore.ItemId + ":" + ore.Count
        + " herb=" + herb.ItemId + ":" + herb.Count
        + " respawn_s=" + ore.RespawnSeconds
        + " forbidden=" + table.Nodes.Count(n => n.Kind == GatherTable.SpiritStone));
    return 0;
}

if (tierState)
{
    WorldConfig config;
    try
    {
        config = WorldConfig.Load(configPath);
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

    var tiers = new AppearanceTierTable(config);
    Console.WriteLine("tier_state=ok near=" + config.TierNearDistance
        + " far=" + config.TierFarDistance
        + " at_0=" + (int)tiers.ForDistance(0)
        + " at_mid=" + (int)tiers.ForDistance((config.TierNearDistance + config.TierFarDistance) / 2)
        + " at_far=" + (int)tiers.ForDistance(config.TierFarDistance + 1));
    return 0;
}

if (positionState)
{
    WorldConfig config;
    try
    {
        config = WorldConfig.Load(configPath);
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

    Console.WriteLine("position_state=ok sync_hz=" + config.SyncHz
        + " max_move_speed=" + config.MaxMoveSpeed
        + " spawn=" + config.SpawnX + "," + config.SpawnY + "," + config.SpawnZ
        + "," + config.SpawnFacing);
    return 0;
}

if (regionState)
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
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    foreach (var region in catalog.Regions)
    {
        Console.WriteLine("region_state=ok id=" + region.Id
            + " chapters=" + string.Join(",", region.Chapters)
            + " neighbors=" + string.Join(",", region.Neighbors)
            + " dialogues=" + string.Join(",", catalog.DialogueIdsFor(region.Id))
            + " faction=" + region.Faction
            + " open=" + region.Open);
    }

    Console.WriteLine("region_state=summary version=" + catalog.Version
        + " regions=" + catalog.Regions.Count
        + " dialogues=" + catalog.Dialogues.Count
        + " speakers=" + catalog.Speakers.Count
        + " factions=" + catalog.Factions.Count
        + " line_capacity=" + config.LineCapacity
        + " max_lines=" + config.MaxLinesPerRegion);
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

return await new WorldProcess(configPath, gatherConfigPath).RunAsync(stop.Token);
