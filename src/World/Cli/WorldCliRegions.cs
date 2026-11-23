namespace World;

// 14.8 D3h：地表与解锁四条只读入口（--region-state / --region-unlock-state /
// --region-access / --region-list）原样搬出 Program.cs；判定、打印与退出码一行未改。
public sealed partial class WorldCli
{
    int? RunRegionEntries()
    {

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

        if (regionUnlockState)
        {
            WorldCatalog catalog;
            try
            {
                catalog = WorldCatalog.Load(WorldConfig.Load(configPath));
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

            if (!File.Exists(regionUnlockPath))
            {
                // 缺表是合法的降级：世界服按 P1-3a1 的静态 open 跑，两块地表一直锁着。
                Console.WriteLine("region_unlock=missing path=" + regionUnlockPath);
                return 0;
            }

            RegionUnlockTable table;
            try
            {
                table = RegionUnlockTable.Load(regionUnlockPath, catalog);
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

            foreach (var entry in table.Entries)
            {
                Console.WriteLine("region_unlock_entry=region=" + entry.RegionId
                    + " unlock_stage=" + entry.UnlockStage
                    + " alignment=" + entry.Alignment);
            }

            Console.WriteLine("region_unlock=ok entries=" + table.Entries.Count
                + " routes=" + table.Routes
                + " locked=" + string.Join(",", catalog.Regions.Where(r => !r.Open).Select(r => r.Id))
                + " static_open=" + catalog.Regions.Count(r => r.Open));
            return 0;
        }

        if (regionAccessState)
        {
            if (accessStage < 0)
            {
                Console.WriteLine("bad_arg=--main-stage");
                return 64;
            }

            WorldCatalog catalog;
            RegionUnlockTable? table;
            try
            {
                catalog = WorldCatalog.Load(WorldConfig.Load(configPath));
                table = RegionUnlockTable.LoadIfPresent(regionUnlockPath, catalog);
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

            // 纯计算：进度与路线都是命令行给的入参，判定逻辑与运行期是同一份（RegionGate.Evaluate）。
            var gate = new RegionGate(table, new NoProgressReader());
            foreach (var region in catalog.Regions.Where(r => !r.Open))
            {
                var access = gate.Evaluate(region, accessStage, accessAlignment);
                Console.WriteLine("region_access=region=" + region.Id
                    + " stage=" + accessStage
                    + " alignment=" + (accessAlignment.Length == 0 ? "none" : accessAlignment)
                    + " ok=" + (access.Ok ? "true" : "false")
                    + " reason=" + (access.Reason.Length == 0 ? "none" : access.Reason)
                    + " unlock_stage=" + access.UnlockStage
                    + " needs_progress=" + (gate.NeedsProgress(region) ? "true" : "false"));
            }

            Console.WriteLine("region_access_summary=static_open=" + catalog.Regions.Count(r => r.Open)
                + " checked=" + catalog.Regions.Count(r => !r.Open)
                + " table=" + (table is null ? "missing" : "ok"));
            return 0;
        }

        if (regionListState)
        {
            if (accessStage < 0)
            {
                Console.WriteLine("bad_arg=--main-stage");
                return 64;
            }

            WorldCatalog catalog;
            RegionUnlockTable? table;
            try
            {
                catalog = WorldCatalog.Load(WorldConfig.Load(configPath));
                table = RegionUnlockTable.LoadIfPresent(regionUnlockPath, catalog);
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

            // 与运行期同一个判定：静态开放与没有解锁条目的地表不问进度（进度传 0、路线传空），
            // 需要进度的才用 --main-stage / --alignment 这两个事实值。列表口在世界服里就是这么分的。
            var gate = new RegionGate(table, new NoProgressReader());
            var opened = 0;
            foreach (var region in catalog.Regions)
            {
                var needs = gate.NeedsProgress(region);
                var access = needs
                    ? gate.Evaluate(region, accessStage, accessAlignment)
                    : gate.Evaluate(region, 0, "");
                if (access.Ok)
                {
                    opened++;
                }

                Console.WriteLine("region_list=id=" + region.Id
                    + " open=" + (access.Ok ? "true" : "false")
                    + " reason=" + (access.Reason.Length == 0 ? "none" : access.Reason)
                    + " unlock_stage=" + access.UnlockStage
                    + " main_stage=" + access.MainStage
                    + " needs_progress=" + (needs ? "true" : "false"));
            }

            Console.WriteLine("region_list_summary=regions=" + catalog.Regions.Count
                + " open=" + opened
                + " stage=" + accessStage
                + " alignment=" + (accessAlignment.Length == 0 ? "none" : accessAlignment)
                + " table=" + (table is null ? "missing" : "ok"));
            return 0;
        }

        return null;
    }
}
