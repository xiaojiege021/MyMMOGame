using System.Globalization;
using System.Text.Json;

namespace World;

/// <summary>
/// 世界 Boss 的地表入口表（config/world_boss.json）。世界服是「这一刻入口在不在」的
/// 唯一判定方：窗口按 UTC 时刻表循环，同一时刻只有一场（窗口不许重叠，加载期硬校验），
/// 上限、首领与入口位置全从这份配置读。
///
/// 这张表里放不进「奖励、掉落、灵石、矿脉、税收、归属加成」：顶层与每一段都做键白名单，
/// 方案只写定「归属只记名字，不加属性」——记名字这件事在本进程是内存里的一行状态，
/// 不进库、不进任何数值。掉落表全仓还没有承接卡，本卡不做掉落，也不在这张表里给它留位置。
///
/// 时刻只按服务端时钟解释。请求里没有时间、没有场次编号、没有上限：客户端改包改不了
/// 「现在开没开」。
/// </summary>
public sealed class WorldBossTable
{
    public const string EntrySchema = "world-boss-table/1";

    /// <summary>一天多少分钟。窗口按 UTC 的一天循环，不读本机时区。</summary>
    const int MinutesPerDay = 24 * 60;

    /// <summary>一天多少秒。窗口时长按秒配，上限就是一天。</summary>
    const int SecondsPerDay = 24 * 60 * 60;

    static readonly string[] TopKeys =
    [
        "schema", "version", "pending", "note",
        "entrance", "cap", "cap_note", "timezone", "timezone_note",
        "windows", "windows_note", "boss",
    ];

    static readonly string[] EntranceKeys = ["id", "region_id", "name", "note"];
    static readonly string[] WindowKeys = ["id", "start_utc", "duration_s"];
    static readonly string[] BossKeys =
        ["id", "name", "hp_max", "spawn_mm", "exp_on_clear", "attack", "note"];
    static readonly string[] AttackKeys = ["damage", "interval_ms", "foreswing_ms"];
    static readonly string[] PointKeys = ["x", "y"];

    /// <summary>这些键一次都不许出现：归属只记名字，不变成第二份数值或第二份归属。</summary>
    static readonly string[] ForbiddenKeys =
    [
        "reward", "rewards", "drop", "drops", "loot", "gold", "spirit_stone",
        "vein", "vein_id", "tax", "tax_pct", "owner_sect", "sect_bonus",
        "attribute", "attributes", "attr", "shop", "price", "item", "items",
        "exp_bonus", "buff", "balance", "ratio",
    ];

    readonly List<BossWindow> _windows;

    WorldBossTable(WorldBossWire wire, List<BossWindow> windows)
    {
        Version = wire.Version;
        Pending = wire.Pending;
        EntranceId = wire.Entrance.Id.Trim();
        EntranceName = wire.Entrance.Name.Trim();
        RegionId = wire.Entrance.RegionId.Trim();
        Cap = wire.Cap;
        _windows = windows;
    }

    public int Version { get; }

    /// <summary>数值是不是还待定。产品正本是 true，核对与界面照它显示占位。</summary>
    public bool Pending { get; }

    public string EntranceId { get; }
    public string EntranceName { get; }
    public string RegionId { get; }

    /// <summary>同一场最多几个人（首发 20）。代码里没有第二个数字。</summary>
    public int Cap { get; }

    public IReadOnlyList<BossWindow> Windows => _windows;

    /// <summary>
    /// 读表 + 硬校验。regionIds 是已经配置、且已经开放的地表：入口必须长在
    /// 一块开放的地表上，方案写的是「在已经开放的某一处地表入口出现」。
    /// </summary>
    public static WorldBossTable Load(string path, IReadOnlySet<string> openRegions)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到世界 Boss 表 " + path);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigException("世界 Boss 表不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("世界 Boss 表顶层必须是对象：" + path);
            }

            RejectExtraKeys(root, TopKeys, "顶层");
            RejectForbidden(root, "顶层");

            var schema = Text(root, "schema");
            if (!string.Equals(schema, EntrySchema, StringComparison.Ordinal))
            {
                throw new ConfigException(
                    "世界 Boss 表的 schema 必须是 " + EntrySchema + "，现在是「" + schema + "」");
            }

            var pending = Bool(root, "pending");
            var version = Int(root, "version");
            if (version < 1)
            {
                throw new ConfigException("世界 Boss 表的 version 必须大于 0，现在是 " + version);
            }

            var timezone = Text(root, "timezone");
            if (!string.Equals(timezone, "utc", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigException(
                    "世界 Boss 表的 timezone 只认 utc（服务端时钟是唯一事实值），现在是「"
                    + timezone + "」");
            }

            var entrance = Block(root, "entrance", EntranceKeys, "entrance");
            RejectForbidden(entrance, "entrance");
            var entranceId = RequiredText(entrance, "id", "entrance.id");
            var regionId = RequiredText(entrance, "region_id", "entrance.region_id");
            var entranceName = RequiredText(entrance, "name", "entrance.name");
            if (!openRegions.Contains(regionId))
            {
                throw new ConfigException(
                    "entrance.region_id 必须是已经开放的地表，现在是「" + regionId
                    + "」；已开放：" + string.Join(",", openRegions));
            }

            var cap = Int(root, "cap");
            if (cap < 1 || cap > 100)
            {
                throw new ConfigException("cap 必须在 1 到 100 之间，现在是 " + cap);
            }

            var windows = ParseWindows(root);
            var boss = Block(root, "boss", BossKeys, "boss");
            RejectForbidden(boss, "boss");
            CheckBoss(boss);

            var wire = new WorldBossWire
            {
                Version = version,
                Pending = pending,
                Cap = cap,
                Entrance = new EntranceWire
                {
                    Id = entranceId, RegionId = regionId, Name = entranceName,
                },
            };
            return new WorldBossTable(wire, windows);
        }
    }

    static List<BossWindow> ParseWindows(JsonElement root)
    {
        if (!root.TryGetProperty("windows", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigException("世界 Boss 表缺 windows 数组");
        }

        var windows = new List<BossWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("windows 的每一项必须是对象");
            }

            RejectExtraKeys(item, WindowKeys, "windows[]");
            RejectForbidden(item, "windows[]");
            var id = RequiredText(item, "id", "windows[].id");
            if (!seen.Add(id))
            {
                throw new ConfigException("windows 里的 id 重复：" + id);
            }

            var start = RequiredText(item, "start_utc", "windows[].start_utc");
            var minutes = ParseMinutes(start);
            var duration = Int(item, "duration_s");
            if (duration < 60 || duration > SecondsPerDay)
            {
                throw new ConfigException(
                    "windows[" + id + "].duration_s 必须在 60 到 86400 之间，现在是 " + duration);
            }

            if (minutes + duration / 60.0 > MinutesPerDay)
            {
                throw new ConfigException(
                    "windows[" + id + "] 跨过了午夜：" + start + " + " + duration + "s；"
                    + "一个窗口必须在 UTC 的一天之内结束，跨天请拆成两条");
            }

            windows.Add(new BossWindow(id, minutes, duration));
        }

        if (windows.Count == 0)
        {
            throw new ConfigException("windows 不能是空数组");
        }

        // 同一时刻只有一场：任意两个窗口的时段不许相交，写完这条就是结构保证。
        for (var i = 0; i < windows.Count; i++)
        {
            for (var j = i + 1; j < windows.Count; j++)
            {
                if (windows[i].Overlaps(windows[j]))
                {
                    throw new ConfigException(
                        "两个窗口重叠了：" + windows[i].Id + " / " + windows[j].Id
                        + "；方案写定同一时刻只有一场");
                }
            }
        }

        windows.Sort((a, b) => a.StartMinutes.CompareTo(b.StartMinutes));
        return windows;
    }

    static void CheckBoss(JsonElement boss)
    {
        var id = RequiredLong(boss, "id", "boss.id");
        if (id == 0)
        {
            throw new ConfigException("boss.id 不能是 0");
        }

        RequiredText(boss, "name", "boss.name");
        var hp = RequiredLong(boss, "hp_max", "boss.hp_max");
        if (hp <= 0)
        {
            throw new ConfigException("boss.hp_max 必须大于 0，现在是 " + hp);
        }

        if (boss.TryGetProperty("exp_on_clear", out _)
            && RequiredLong(boss, "exp_on_clear", "boss.exp_on_clear") < 0)
        {
            throw new ConfigException("boss.exp_on_clear 不能是负数");
        }

        if (boss.TryGetProperty("spawn_mm", out var point))
        {
            if (point.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("boss.spawn_mm 必须是对象");
            }

            RejectExtraKeys(point, PointKeys, "boss.spawn_mm");
            _ = RequiredLong(point, "x", "boss.spawn_mm.x");
            _ = RequiredLong(point, "y", "boss.spawn_mm.y");
        }

        if (boss.TryGetProperty("attack", out var attack))
        {
            if (attack.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("boss.attack 必须是对象");
            }

            RejectExtraKeys(attack, AttackKeys, "boss.attack");
            if (RequiredLong(attack, "damage", "boss.attack.damage") <= 0)
            {
                throw new ConfigException("boss.attack.damage 必须大于 0");
            }

            if (RequiredLong(attack, "interval_ms", "boss.attack.interval_ms") < 100)
            {
                throw new ConfigException("boss.attack.interval_ms 不能小于 100");
            }

            if (RequiredLong(attack, "foreswing_ms", "boss.attack.foreswing_ms") < 0)
            {
                throw new ConfigException("boss.attack.foreswing_ms 不能是负数");
            }
        }
    }

    /// <summary>
    /// 这一刻是哪一场、开没开。纯计算：只读服务端时钟给进来的时刻，不读请求、
    /// 不读本机时区。窗口内返回 open=true 与这一场的钥匙（入口 + 日期 + 窗口编号），
    /// 同一时刻所有人算出来的钥匙必然相同——「同一时刻一场」靠这一条落地。
    /// </summary>
    public WorldBossWindow WindowOf(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var dayStart = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        var secondOfDay = (int)(utc - dayStart).TotalSeconds;
        var date = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        foreach (var window in _windows)
        {
            var start = window.StartSeconds;
            if (secondOfDay >= start && secondOfDay < start + window.DurationSeconds)
            {
                var startAt = dayStart.AddSeconds(start);
                return new WorldBossWindow(
                    true, KeyOf(date, window.Id), startAt.ToUnixTimeMilliseconds(),
                    startAt.AddSeconds(window.DurationSeconds).ToUnixTimeMilliseconds(), 0);
            }
        }

        return new WorldBossWindow(
            false, "", 0, 0, NextStartAfter(utc).ToUnixTimeMilliseconds());
    }

    string KeyOf(string date, string windowId) => EntranceId + "-" + date + "-" + windowId;

    DateTimeOffset NextStartAfter(DateTimeOffset utc)
    {
        var dayStart = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        foreach (var window in _windows)
        {
            var start = dayStart.AddMinutes(window.StartMinutes);
            if (start > utc)
            {
                return start;
            }
        }

        return dayStart.AddDays(1).AddMinutes(_windows[0].StartMinutes);
    }

    static int ParseMinutes(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
            || hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            throw new ConfigException("窗口的 start_utc 要写成 HH:MM（UTC），现在是「" + text + "」");
        }

        return hour * 60 + minute;
    }

    static void RejectForbidden(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            if (ForbiddenKeys.Contains(name))
            {
                throw new ConfigException(
                    where + " 里不许出现 " + property.Name
                    + "：世界 Boss 归属只记名字，不加属性、不改税收、不转走灵石矿脉，也不发奖");
            }
        }
    }

    static void RejectExtraKeys(JsonElement element, string[] allowed, string where)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new ConfigException(where + " 里有不认识的键 " + property.Name);
            }
        }
    }

    static JsonElement Block(JsonElement root, string name, string[] allowed, string where)
    {
        if (!root.TryGetProperty(name, out var block) || block.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException("世界 Boss 表缺 " + name + " 段");
        }

        RejectExtraKeys(block, allowed, where);
        return block;
    }

    static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    static string RequiredText(JsonElement element, string name, string label)
    {
        var text = Text(element, name).Trim();
        if (text.Length == 0)
        {
            throw new ConfigException(label + " 不能是空");
        }

        return text;
    }

    static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : throw new ConfigException("世界 Boss 表缺 " + name);

    static long RequiredLong(JsonElement element, string name, string label) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : throw new ConfigException("世界 Boss 表缺 " + label);

    /// <summary>配置里的一条场次窗口。start 是 UTC 的当日分钟数。</summary>
    public sealed record BossWindow(string Id, int StartMinutes, int DurationSeconds)
    {
        public int StartSeconds => StartMinutes * 60;

        public bool Overlaps(BossWindow other) =>
            StartSeconds < other.StartSeconds + other.DurationSeconds
            && other.StartSeconds < StartSeconds + DurationSeconds;
    }

    // 只用来在构造完以后再填一遍入口的名字，避免带着 JSON 元素活过方法体。
    sealed class WorldBossWire
    {
        public int Version { get; init; }
        public bool Pending { get; init; }
        public int Cap { get; init; }
        public required EntranceWire Entrance { get; init; }
    }

    sealed class EntranceWire
    {
        public required string Id { get; init; }
        public required string RegionId { get; init; }
        public required string Name { get; init; }
    }
}

/// <summary>
/// 这一刻的入口事实值。open 为真才带钥匙与起止时刻；关着时 next_starts_at_ms 是
/// 下一次出现的时刻（只作展示）。这是纯计算结果，不碰任何状态。
/// </summary>
public sealed record WorldBossWindow(
    bool Open, string Key, long StartsAtMs, long EndsAtMs, long NextStartsAtMs);
