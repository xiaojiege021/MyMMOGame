using System.Runtime.InteropServices;
using World;

// 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
var configPath = "config/world.json";
var gatherConfigPath = "config/gather.json";
// C2 铺开表是可选表：老卡的临时配置目录里没有它，世界服按 P1-3b 的四个基准点照常启动。
var gatherSpreadConfigPath = "config/gather_spread.json";
var gatherSpreadExplicit = false;
var flightConfigPath = "config/flight.json";
var worldBossConfigPath = "config/world_boss.json";
var regionState = false;
var positionState = false;
var tierState = false;
var gatherState = false;
var gatherSpreadState = false;
var flightState = false;
var worldBossState = false;
var worldBossAtMs = 0L;

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
        // C2 铺开表单独一份，核对脚本用临时配置跑，不碰仓库里那份。
        case "--gather-spread-config" when i + 1 < args.Length:
            gatherSpreadConfigPath = args[++i];
            gatherSpreadExplicit = true;
            break;
        // 只给核对用：把铺开后的节点、境界区域与每块地表的覆盖打出来，不启动进程。
        case "--gather-spread-state":
            gatherSpreadState = true;
            break;
        // 飞行与跨区门槛单独一份，核对脚本用临时配置跑，不碰仓库里那份。
        case "--flight-config" when i + 1 < args.Length:
            flightConfigPath = args[++i];
            break;
        // 只给核对用：把读表后的境界顺序与门槛打出来，不启动进程。
        case "--flight-state":
            flightState = true;
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
        // 世界 Boss 表单独一份，核对脚本用临时配置跑，不碰仓库里那份。
        case "--world-boss-config" when i + 1 < args.Length:
            worldBossConfigPath = args[++i];
            break;
        // 只给核对用：把读表后的入口、上限、窗口与「这一刻是哪一场」打出来，不起进程。
        case "--world-boss-state":
            worldBossState = true;
            break;
        // 搭配 --world-boss-state：把「这一刻」定成给定的毫秒时间戳，用来验未到点 / 到点。
        case "--at" when i + 1 < args.Length:
            if (!long.TryParse(args[++i], out worldBossAtMs))
            {
                Console.WriteLine("bad_arg=--at");
                return 64;
            }
            break;
        default:
            Console.WriteLine("unknown_arg=" + args[i]);
            return 64;
    }
}

// C2 的可选表跟 --config 所在的目录走。老卡用自己的临时配置目录改 flight.json 门槛时，
// 那份目录里没有 gather_spread.json，就按 P1-3b 四个基准点降级；否则仓库里的产品表
// 会和临时改过的境界门槛交叉校验失败，把老卡的「改配置重启」整段打成假红。
// 显式给 --gather-spread-config 时不推导（C2 自己的核对脚本走这条路）。
if (!gatherSpreadExplicit)
{
    var configDir = Path.GetDirectoryName(configPath);
    if (!string.IsNullOrEmpty(configDir))
    {
        gatherSpreadConfigPath = Path.Combine(configDir, "gather_spread.json");
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

if (gatherSpreadState)
{
    try
    {
        // 与启动同一套校验：地表、飞行门槛、基准采集表都按运行时那样读，
        // 只读入口不能拿一份更松的规则把坏铺开表放过去。
        var worldConfig = WorldConfig.Load(configPath);
        var catalog = WorldCatalog.Load(worldConfig);
        var regionIds = catalog.Regions.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var flights = FlightTable.Load(flightConfigPath, regionIds);
        var baseTable = GatherTable.Load(gatherConfigPath, regionIds);
        var spread = GatherSpreadTable.LoadIfPresent(
            gatherSpreadConfigPath, regionIds, flights, baseTable.Nodes);
        if (spread is null)
        {
            Console.WriteLine("gather_spread=missing");
            return 0;
        }

        var all = baseTable.WithSpread(spread.Nodes);
        Console.WriteLine("gather_spread_state=ok nodes=" + spread.Nodes.Count
            + " regions=" + spread.RegionCount
            + " realms=" + spread.Realms
            + " base=" + baseTable.Nodes.Count
            + " total=" + all.Nodes.Count);
        foreach (var region in catalog.Regions)
        {
            var ore = all.Nodes.Count(n => n.RegionId == region.Id && n.Kind == GatherTable.Ore);
            var herb = all.Nodes.Count(n => n.RegionId == region.Id && n.Kind == GatherTable.Herb);
            // 只报事实值：境界门槛来自 flight.json，节点数来自合并后的唯一节点集。
            Console.WriteLine("region=" + region.Id
                + " realm=" + (flights.RequiredFor(region.Id) ?? "")
                + " ore=" + ore + " herb=" + herb);
        }

        return 0;
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
}

if (flightState)
{
    FlightTable table;
    try
    {
        // 与启动同一套校验：门槛表必须覆盖 world.json 的每一块地表，
        // 漏一块就等于给它开零门槛，只读入口也要挡住。
        var worldConfig = WorldConfig.Load(configPath);
        var catalog = WorldCatalog.Load(worldConfig);
        table = FlightTable.Load(
            flightConfigPath,
            catalog.Regions.Select(r => r.Id).ToHashSet(StringComparer.Ordinal));
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

    Console.WriteLine("flight_state=ok realms=" + string.Join(",", table.Realms)
        + " flight_realm=" + table.FlightRealm
        + " gates=" + table.Gates.Count);
    // 门槛逐块打出来，核对脚本按「地点 + 境界」比对，不在代码里写第二套数字。
    foreach (var gate in table.Gates.OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        Console.WriteLine("flight_gate=ok region=" + gate.Key + " realm=" + gate.Value);
    }

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

if (worldBossState)
{
    WorldBossTable table;
    try
    {
        var catalog = WorldCatalog.Load(WorldConfig.Load(configPath));
        var open = catalog.Regions.Where(r => r.Open).Select(r => r.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!File.Exists(worldBossConfigPath))
        {
            Console.WriteLine("world_boss=missing");
            return 0;
        }

        table = WorldBossTable.Load(worldBossConfigPath, open);
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

    Console.WriteLine("world_boss_table=ok version=" + table.Version
        + " pending=" + (table.Pending ? "true" : "false")
        + " entrance=" + table.EntranceId
        + " name=" + table.EntranceName
        + " region=" + table.RegionId
        + " cap=" + table.Cap
        + " windows=" + string.Join(",", table.Windows.Select(w => w.Id + "@" + w.StartMinutes + "+" + w.DurationSeconds)));
    // 纯计算：这一刻是哪一场只由服务端时钟与这张表决定，同一时刻所有人算出来同一把钥匙。
    var now = worldBossAtMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(worldBossAtMs)
        : DateTimeOffset.UtcNow;
    var window = table.WindowOf(now);
    Console.WriteLine("world_boss_window=at=" + now.ToUnixTimeMilliseconds()
        + " open=" + (window.Open ? "true" : "false")
        + " key=" + (window.Key.Length == 0 ? "none" : window.Key)
        + " starts_at_ms=" + window.StartsAtMs
        + " ends_at_ms=" + window.EndsAtMs
        + " next_starts_at_ms=" + window.NextStartsAtMs);

    // 纯计算：入口这台状态机不连线也能验。一场里「散场没打死」只记一次、
    // 可以被后来「打死了」升级、打掉以后再来报一律 duplicate —— 顺序反过来不行。
    var probe = new WorldBossEntrance(table);
    var probeKey = window.Key.Length == 0
        ? table.EntranceId + "-selftest"
        : window.Key;
    void Step(string name, RecordFact fact)
    {
        var outcome = probe.Record(fact);
        var state = probe.WindowOf(fact.WindowKey);
        Console.WriteLine("world_boss_record=step=" + name
            + " ok=" + (outcome.Ok ? "true" : "false")
            + " duplicate=" + (outcome.Duplicate ? "true" : "false")
            + " closed=" + (outcome.EntranceClosed ? "true" : "false")
            + " reason=" + (outcome.Reason.Length == 0 ? "none" : outcome.Reason)
            + " winner=" + state.SectId
            + " name=" + (state.SectName.Length == 0 ? "none" : state.SectName)
            + " killed=" + (state.Killed ? "true" : "false"));
    }

    Step("disband", new RecordFact("txn:" + probeKey + ":1", table.EntranceId, probeKey, false, 0, "", 0));
    Step("disband_again", new RecordFact("txn:" + probeKey + ":2", table.EntranceId, probeKey, false, 0, "", 0));
    Step("kill", new RecordFact("txn:" + probeKey + ":3", table.EntranceId, probeKey, true, 7001, "太虚道宗", 6200));
    Step("kill_again", new RecordFact("txn:" + probeKey + ":4", table.EntranceId, probeKey, true, 7002, "别家", 6300));
    Step("wrong_entrance",
        new RecordFact("txn:" + probeKey + ":5", table.EntranceId + "-x", probeKey, true, 7002, "别家", 6300));
    Step("no_sect_name",
        new RecordFact("txn:" + probeKey + ":6", table.EntranceId, probeKey, true, 7003, "", 6300));
    Console.WriteLine("world_boss_record=summary recorded=" + probe.RecordCount
        + " kills=" + probe.KillCount
        + " owner=" + probe.WindowOf(probeKey).SectId
        + " owner_name=" + (probe.WindowOf(probeKey).SectName.Length == 0
            ? "none" : probe.WindowOf(probeKey).SectName));
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

return await new WorldProcess(
    configPath, gatherConfigPath, flightConfigPath, worldBossConfigPath, gatherSpreadConfigPath)
    .RunAsync(stop.Token);
