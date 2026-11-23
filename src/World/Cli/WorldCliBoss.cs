namespace World;

// 14.8 D3h：世界 Boss 只读入口（--world-boss-state）原样搬出 Program.cs；
// 场次、名额、计数器的打印与退出码一行未改。
public sealed partial class WorldCli
{
    int? RunBossEntry()
    {

        if (worldBossState)
        {
            WorldBossTable table;
            Mmo.Shared.Domain.CalendarTable calendar;
            try
            {
                var worldConfig = WorldConfig.Load(configPath);
                var catalog = WorldCatalog.Load(worldConfig);
                var open = catalog.Regions.Where(r => r.Open).Select(r => r.Id)
                    .ToHashSet(StringComparer.Ordinal);
                calendar = Mmo.Shared.Domain.CalendarTable.Load(
                    calendarPath.Length > 0 ? calendarPath : worldConfig.CalendarTable);
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
            var window = table.WindowOf(now, calendar);
            Console.WriteLine("world_boss_calendar=timezone=" + calendar.TimeZoneId
                + " path=" + (calendarPath.Length > 0 ? calendarPath : "config/calendar.json"));
            Console.WriteLine("world_boss_window=at=" + now.ToUnixTimeMilliseconds()
                + " open=" + (window.Open ? "true" : "false")
                + " key=" + (window.Key.Length == 0 ? "none" : window.Key)
                + " starts_at_ms=" + window.StartsAtMs
                + " ends_at_ms=" + window.EndsAtMs
                + " next_starts_at_ms=" + window.NextStartsAtMs);

            // 纯计算：入口这台状态机不连线也能验。一场里「散场没打死」只记一次、
            // 可以被后来「打死了」升级、打掉以后再来报一律 duplicate —— 顺序反过来不行。
            var probe = new WorldBossEntrance(table, calendar);
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

            // Q11：名额占位表不连线也能验。假一条入口分线与一条非入口分线，把发名额、
            // 重复要、续约、满员、不在入口线、分线满、过期回收、释放都走一遍。
            // 名额只在进程内存，这里跑完就丢，不落库、不发奖、不改任何数值。
            var probeEntrance = new WorldBossEntrance(table, calendar);
            var onLine = new ProbeStanding(true, table.RegionId + "#1", 1, 4);
            var offLine = new ProbeStanding(false, "", 0, 0);
            var fullLine = new ProbeStanding(true, table.RegionId + "#2", 9, 4);
            void Slot(string step, SlotOutcome r)
                => Console.WriteLine("world_boss_slot=step=" + step
                    + " ok=" + (r.Ok ? "true" : "false")
                    + " reason=" + (r.Reason.Length == 0 ? "none" : r.Reason)
                    + " duplicate=" + (r.Duplicate ? "true" : "false")
                    + " current=" + r.Current + " cap=" + r.Cap
                    + " line=" + (r.LineKey.Length == 0 ? "none" : r.LineKey)
                    + " line_occupancy=" + r.LineOccupancy + " line_capacity=" + r.LineCapacity);

            if (!window.Open)
            {
                // 闭场时也要能要名额：一律 worldboss_closed，一个位置都不占。
                var closedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                    window.NextStartsAtMs > 0 ? window.NextStartsAtMs - 1000 : now.ToUnixTimeMilliseconds());
                var closedSlots = new WorldBossSlots(table, calendar, probeEntrance, onLine);
                Slot("closed", closedSlots.Hold(1001, closedAt));
                Console.WriteLine("world_boss_slot=summary current=0 cap=" + table.Cap);
                return 0;
            }

            var at = DateTimeOffset.FromUnixTimeMilliseconds(window.StartsAtMs + 1000);
            var slots = new WorldBossSlots(table, calendar, probeEntrance, onLine);
            Slot("hold", slots.Hold(1001, at));
            Slot("hold_again", slots.Hold(1001, at));
            Slot("renew_entered", slots.Renew(1001, entered: true, at));
            for (ulong id = 1002; id <= 1000UL + (ulong)table.Cap; id++)
            {
                slots.Hold(id, at);
            }

            Slot("hold_over_cap", slots.Hold(9001, at));
            Slot("renew_unknown", slots.Renew(9002, entered: true, at));
            Slot("release", slots.Release(1001, "leave", at));
            Slot("release_again", slots.Release(1001, "leave", at));
            var offSlots = new WorldBossSlots(table, calendar, probeEntrance, offLine);
            Slot("not_on_entrance_line", offSlots.Hold(2001, at));
            var fullSlots = new WorldBossSlots(table, calendar, probeEntrance, fullLine);
            Slot("line_full", fullSlots.Hold(2002, at));
            var holdFacts = slots.Describe(at);
            var late = at.AddSeconds(holdFacts.HoldSeconds + 1);
            Slot("renew_after_lost", slots.Renew(1002, entered: false, late));
            var facts = slots.Describe(late);
            Console.WriteLine("world_boss_slot=summary current=" + facts.Current
                + " remaining=" + facts.Remaining + " cap=" + facts.Cap
                + " hold_s=" + facts.HoldSeconds + " renew_s=" + facts.RenewSeconds
                + " lost_s=" + facts.LostSeconds);
            var counters = slots.Counters;
            Console.WriteLine("world_boss_slot=counters granted=" + counters.Granted
                + " renewed=" + counters.Renewed + " released=" + counters.Released
                + " expired=" + counters.Expired + " rejected_full=" + counters.RejectedFull
                + " rejected_line=" + counters.RejectedLine);
            return 0;
        }

        return null;
    }
}
