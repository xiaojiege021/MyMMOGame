using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mmo.Shared.Domain.Tests;

/// <summary>
/// C6/D12：三张表（副本 / 掉落 / 世界 Boss）的配置加载单测。
/// 既读仓库正本，也读临时改坏的副本，证明「坏配置起不来」不是靠核对脚本的
/// 另一套规则，而是加载器本体的硬校验。
/// </summary>
public sealed class ConfigLoadingTests
{
    static string ConfigDir => Path.Combine(AppContext.BaseDirectory, "config");

    static string Real(string name) => Path.Combine(ConfigDir, name);

    static JsonNode ReadReal(string name) =>
        JsonNode.Parse(File.ReadAllText(Real(name)))!;

    /// <summary>把改过的 JSON 写回配置目录：items_config 这类相对引用仍能解析。</summary>
    static string WriteTemp(JsonNode root, string name)
    {
        var path = Path.Combine(ConfigDir, "tmp-" + Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    static JsonNode Dungeon(JsonNode root, string id) =>
        root["dungeons"]!.AsArray().First(node => node!["id"]!.GetValue<string>() == id)!;

    static (HashSet<string> Items, HashSet<string> Currencies) LoadCatalogs()
    {
        var items = ReadReal("items.json")["items"]!.AsArray()
            .Select(node => node!["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        var currencies = ReadReal("currency.json")["currencies"]!.AsArray()
            .Select(node => node!["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        return (items, currencies);
    }

    // ---- 副本表 ----

    [Fact]
    public void DungeonTable_loads_real_config_with_one_shared_definition()
    {
        var table = DungeonTable.Load(Real("dungeons.json"));

        Assert.True(table.Version >= 8);
        Assert.True(table.All.Count >= 6);
        Assert.Equal(6000, table.FatigueDailyCap);
        Assert.Equal(1500, table.HotSpringRecovery);
        Assert.Equal(12, table.WeeklyShareCap);
        Assert.Equal(1.0, table.MonsterHpScalePerPlayer);

        var main = Assert.IsType<DungeonDef>(table.Find("main"));
        Assert.Equal(10, main.Drill);
        Assert.Null(main.Realm);

        var dragon = Assert.IsType<DungeonDef>(table.Find("dragon"));
        Assert.True(dragon.IsHard);
        Assert.Equal(2, dragon.MinPlayers);
        Assert.Equal(8, dragon.MaxPlayers);
        Assert.Equal(1, dragon.EntriesPerWeek);
        Assert.Equal(1, dragon.RewardPerPlayer);
        Assert.Equal(100, dragon.Drill);

        // P3-1a1 / P3-1a2 的字段合并前只在角色服那一份里有；现在就是共享定义本身。
        var late = Assert.IsType<DungeonDef>(table.Find("abyss_yuanying"));
        Assert.Equal("元婴", late.Realm);
        Assert.Equal(200, late.Drill);
        Assert.NotNull(typeof(DungeonDef).GetProperty(nameof(DungeonDef.Realm)));
        Assert.NotNull(typeof(DungeonDef).GetProperty(nameof(DungeonDef.Drill)));
    }

    [Fact]
    public void DungeonTable_rejects_hard_dungeon_without_min_players()
    {
        var root = ReadReal("dungeons.json");
        Dungeon(root, "dragon").AsObject().Remove("min_players");
        var path = WriteTemp(root, "dungeons.json");

        var ex = Assert.Throws<ConfigException>(() => DungeonTable.Load(path));
        Assert.Contains("min_players", ex.Message);
    }

    [Fact]
    public void DungeonTable_rejects_normal_dungeon_with_hard_only_fields()
    {
        var root = ReadReal("dungeons.json");
        Dungeon(root, "lair")["entries_per_week"] = 1;
        var path = WriteTemp(root, "dungeons.json");

        var ex = Assert.Throws<ConfigException>(() => DungeonTable.Load(path));
        Assert.Contains("entries_per_week", ex.Message);
    }

    [Fact]
    public void DungeonTable_rejects_empty_realm()
    {
        var root = ReadReal("dungeons.json");
        Dungeon(root, "abyss_yuanying")["realm"] = "";
        var path = WriteTemp(root, "dungeons.json");

        Assert.Throws<ConfigException>(() => DungeonTable.Load(path));
    }

    [Fact]
    public void DungeonTable_rejects_negative_drill()
    {
        var root = ReadReal("dungeons.json");
        Dungeon(root, "abyss")["drill"] = -1;
        var path = WriteTemp(root, "dungeons.json");

        var ex = Assert.Throws<ConfigException>(() => DungeonTable.Load(path));
        Assert.Contains("drill", ex.Message);
    }

    [Fact]
    public void DungeonTable_rejects_fatigue_over_daily_cap()
    {
        var root = ReadReal("dungeons.json");
        Dungeon(root, "abyss")["fatigue_per_player"] = 6000;
        var path = WriteTemp(root, "dungeons.json");

        Assert.Throws<ConfigException>(() => DungeonTable.Load(path));
    }

    [Fact]
    public void DungeonTable_rejects_missing_hot_spring_recovery()
    {
        var root = ReadReal("dungeons.json");
        root.AsObject().Remove("hot_spring_recovery");
        var path = WriteTemp(root, "dungeons.json");

        var ex = Assert.Throws<ConfigException>(() => DungeonTable.Load(path));
        Assert.Contains("hot_spring_recovery", ex.Message);
    }

    // ---- 掉落表 ----

    [Fact]
    public void DungeonDropTable_loads_real_config_with_item_and_currency_checks()
    {
        var (items, currencies) = LoadCatalogs();
        var table = DungeonDropTable.Load(Real("dungeon_drops.json"), items, currencies);

        Assert.False(table.IsMissing);
        Assert.True(table.Pending);
        Assert.Equal(3, table.DungeonCount);
        Assert.Equal(2, table.LinesFor("abyss").Count);
        Assert.Equal(2, table.LinesFor("world_boss").Count);
        Assert.True(table.MatchesReported("abyss", table.LinesFor("abyss")));
        Assert.False(table.MatchesReported("abyss", [new DropLineDef("crystal_attack", 1)]));
    }

    [Fact]
    public void DungeonDropTable_rejects_item_not_in_catalog()
    {
        var root = ReadReal("dungeon_drops.json");
        root["entries"]![0]!["lines"]![0]!["item_id"] = "no_such_item";
        var path = WriteTemp(root, "dungeon_drops.json");
        var (items, currencies) = LoadCatalogs();

        var ex = Assert.Throws<ConfigException>(
            () => DungeonDropTable.Load(path, items, currencies));
        Assert.Contains("items", ex.Message);
    }

    [Fact]
    public void DungeonDropTable_rejects_spirit_stone_currency()
    {
        var root = ReadReal("dungeon_drops.json");
        root["entries"]![0]!["lines"]![0]!["item_id"] = "stone_1";
        var path = WriteTemp(root, "dungeon_drops.json");
        var (items, currencies) = LoadCatalogs();

        var ex = Assert.Throws<ConfigException>(
            () => DungeonDropTable.Load(path, items, currencies));
        Assert.Contains("灵石", ex.Message);
    }

    [Fact]
    public void DungeonDropTable_without_catalog_only_checks_structure()
    {
        // 战斗服只报事实，不传物品 / 币种目录：表外物品放过，灵石币种仍然挡住。
        var root = ReadReal("dungeon_drops.json");
        root["entries"]![0]!["lines"]![0]!["item_id"] = "no_such_item";
        var path = WriteTemp(root, "dungeon_drops.json");

        var table = DungeonDropTable.Load(path);
        Assert.Equal("no_such_item", table.LinesFor("abyss")[0].ItemId);

        root["entries"]![0]!["lines"]![0]!["item_id"] = "stone_2";
        var stonePath = WriteTemp(root, "dungeon_drops.json");
        Assert.Throws<ConfigException>(() => DungeonDropTable.Load(stonePath));
    }

    [Fact]
    public void DungeonDropTable_missing_file_is_a_degraded_empty_table()
    {
        var table = DungeonDropTable.LoadIfPresent(
            Path.Combine(ConfigDir, "no-such-drops.json"));
        Assert.True(table.IsMissing);
        Assert.Empty(table.LinesFor("abyss"));
    }

    // ---- 世界 Boss 表 ----

    static WorldBossTable LoadWorldBoss() =>
        WorldBossTable.Load(Real("world_boss.json"), new HashSet<string>(["龙巢门口"], StringComparer.Ordinal));

    [Fact]
    public void WorldBossTable_loads_real_config_with_open_region()
    {
        var table = LoadWorldBoss();

        Assert.False(table.IsMissing);
        Assert.Equal("world_boss_1", table.EntranceId);
        Assert.Equal("龙巢门口", table.RegionId);
        Assert.Equal(20, table.Cap);
        Assert.Equal(2, table.Windows.Count);
        Assert.Equal(9101UL, table.Boss.Id);
        Assert.Equal(6000, table.Boss.HpMax);
        Assert.Equal(120, table.Boss.ExpOnClear);
    }

    [Fact]
    public void WorldBossTable_rejects_entrance_outside_open_regions()
    {
        var ex = Assert.Throws<ConfigException>(() => WorldBossTable.Load(
            Real("world_boss.json"),
            new HashSet<string>(["别处"], StringComparer.Ordinal)));
        Assert.Contains("龙巢门口", ex.Message);
    }

    [Fact]
    public void WorldBossTable_battle_side_loads_without_region_check()
    {
        // 战斗服不传已开放地表：同一份正本照样能读，只是不算入口归属。
        var table = WorldBossTable.Load(Real("world_boss.json"));
        Assert.Equal("龙巢门口", table.RegionId);
    }

    [Fact]
    public void WorldBossTable_window_math_is_pure_and_shared()
    {
        var table = LoadWorldBoss();

        var open = table.WindowOf(new DateTimeOffset(2026, 11, 17, 12, 10, 0, TimeSpan.Zero));
        Assert.True(open.Open);
        Assert.Equal("world_boss_1-20261117-w1", open.Key);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            open.StartsAtMs);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 17, 12, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            open.EndsAtMs);

        var now = new DateTimeOffset(2026, 11, 17, 13, 0, 0, TimeSpan.Zero);
        var closed = table.WindowOf(now);
        Assert.False(closed.Open);
        Assert.Equal("", closed.Key);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 17, 20, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            closed.NextStartsAtMs);
    }

    [Fact]
    public void WorldBossTable_missing_table_never_opens()
    {
        var table = WorldBossTable.Missing();
        Assert.True(table.IsMissing);
        Assert.False(table.WindowOf(DateTimeOffset.UtcNow).Open);
    }

    [Fact]
    public void WorldBossTable_rejects_overlapping_windows()
    {
        var root = ReadReal("world_boss.json");
        root["windows"]![1]!["start_utc"] = "12:10";
        var path = WriteTemp(root, "world_boss.json");

        var ex = Assert.Throws<ConfigException>(() => WorldBossTable.Load(path));
        Assert.Contains("重叠", ex.Message);
    }

    [Fact]
    public void WorldBossTable_rejects_forbidden_reward_key()
    {
        var root = ReadReal("world_boss.json");
        root["reward"] = 1;
        var path = WriteTemp(root, "world_boss.json");

        Assert.Throws<ConfigException>(() => WorldBossTable.Load(path));
    }

    // ---- 重置日历（Q3/G1） ----

    static CalendarTable LoadCalendar() => CalendarTable.Load(Real("calendar.json"));

    [Fact]
    public void CalendarTable_loads_real_config()
    {
        var table = LoadCalendar();

        Assert.Equal(CalendarTable.WrittenSchema, "calendar/2");
        Assert.False(table.Pending);
        Assert.Equal("Asia/Shanghai", table.TimeZoneId);
        Assert.Equal(5, table.DailyResetHour);
        Assert.Equal(DayOfWeek.Monday, table.WeeklyResetDay);
        Assert.Equal(5, table.WeeklyResetHour);
        Assert.Equal(new DateOnly(2026, 1, 5), table.SeasonStartDate);
        Assert.Equal(90, table.SeasonLengthDays);
    }

    [Fact]
    public void CalendarTable_day_boundary_is_local_reset_hour()
    {
        var table = LoadCalendar();

        // 东八区 04:59 仍算前一天；05:00 才进新的一天。
        var before = DateTimeOffset.Parse("2026-11-17T20:59:00Z");
        var after = DateTimeOffset.Parse("2026-11-17T21:00:00Z");
        Assert.Equal(new DateOnly(2026, 11, 17), table.DayKey(before));
        Assert.Equal(new DateOnly(2026, 11, 18), table.DayKey(after));
    }

    [Fact]
    public void CalendarTable_week_starts_on_configured_day_and_hour()
    {
        var table = LoadCalendar();

        // 2026-11-16 是周一。周一 04:59 还属于上一周，05:00 才换周。
        var before = DateTimeOffset.Parse("2026-11-15T20:59:00Z");
        var after = DateTimeOffset.Parse("2026-11-15T21:00:00Z");
        Assert.Equal(new DateOnly(2026, 11, 9), table.WeekStartDate(before));
        Assert.Equal(new DateOnly(2026, 11, 16), table.WeekStartDate(after));
        Assert.Equal(
            DateTimeOffset.Parse("2026-11-15T21:00:00Z"),
            table.WeekStartInstant(after).ToUniversalTime());
    }

    [Fact]
    public void CalendarTable_timezone_change_moves_boundaries()
    {
        var root = ReadReal("calendar.json");
        root["timezone"] = "America/New_York";
        var ny = CalendarTable.LoadText(root.ToJsonString(), "ny");

        var at = DateTimeOffset.Parse("2026-11-18T03:00:00Z"); // 上海 11:00 / 纽约 22:00 前一天
        Assert.Equal(new DateOnly(2026, 11, 18), LoadCalendar().DayKey(at));
        Assert.Equal(new DateOnly(2026, 11, 17), ny.DayKey(at));
        Assert.NotEqual(LoadCalendar().FormatLocal(at), ny.FormatLocal(at));
    }

    [Fact]
    public void CalendarTable_season_boundary_follows_reset_hour()
    {
        var root = ReadReal("calendar.json");
        var at = DateTimeOffset.Parse("2026-01-05T00:30:00+08:00");
        Assert.Equal(-1, LoadCalendar().SeasonOf(at).Index);

        root["daily_reset_hour"] = 0;
        var midnight = CalendarTable.LoadText(root.ToJsonString(), "midnight");
        Assert.Equal(0, midnight.SeasonOf(at).Index);
        Assert.Equal(
            DateTimeOffset.Parse("2026-01-05T00:00:00+08:00"),
            midnight.SeasonOf(at).StartsAt);
    }

    [Theory]
    [InlineData("timezone", "Mars/Base")]
    [InlineData("daily_reset_hour", 24)]
    [InlineData("schema", "calendar/1")]
    public void CalendarTable_rejects_bad_config(string key, object value)
    {
        var root = ReadReal("calendar.json");
        if (value is int number)
        {
            root[key] = number;
        }
        else
        {
            root[key] = value.ToString();
        }

        Assert.Throws<ConfigException>(
            () => CalendarTable.LoadText(root.ToJsonString(), "bad"));
    }
}
