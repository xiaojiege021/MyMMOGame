namespace World;

// 14.8 D3h：不连库的只读入口原样搬出 Program.cs（采集 / 地表铺开 / 飞行 / 外观 / 位置），
// 判定与打印一行未改；命中任一个就返回它的退出码，都没命中返回 null 继续往下走。
public sealed partial class WorldCli
{
    public int? RunStateEntries()
    {

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

        // 地表 / 解锁 / 列表与世界 Boss 两组见 WorldCliRegions.cs / WorldCliBoss.cs。
        if (RunRegionEntries() is { } regionCode)
        {
            return regionCode;
        }

        return RunBossEntry();
    }
}
