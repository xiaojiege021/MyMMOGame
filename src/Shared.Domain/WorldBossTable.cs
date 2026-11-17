using System.Globalization;
using System.Text.Json;

namespace Mmo.Shared.Domain;

/// <summary>
/// 世界 Boss 表（config/world_boss.json）。C6/D12 起世界服与战斗服共用这一份解析：
/// 同一份文件、同一条「窗口不许重叠」的硬校验、同一个纯计算
/// （start_utc + duration_s → 这一刻是不是这一场）。
///
/// 两个进程的差别只在调用参数：世界服传已开放地表集合，入口必须长在开放地表上；
/// 战斗服不传集合，只算窗口、人数上限与首领数值。两边读到的是同一个类型。
///
/// 这张表里放不进奖励、掉落、灵石、矿脉、税收、归属加成：归属只记名字，
/// 在本卡里连一个数值都不产生。
/// </summary>
public sealed class WorldBossTable
{
    public const string EntrySchema = "world-boss-table/1";

    const int MinutesPerDay = 24 * 60;
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
        ["id", "name", "hp_max", "spawn_mm", "exp_on_clear", "attack", "phases", "note"];
    static readonly string[] AttackKeys = ["damage", "interval_ms", "foreswing_ms"];
    static readonly string[] PhaseKeys = ["name", "hp_pct_from", "hp_pct_to", "attack"];
    static readonly string[] PointKeys = ["x", "y"];

    static readonly string[] ForbiddenKeys =
    [
        "reward", "rewards", "drop", "drops", "loot", "gold", "spirit_stone",
        "vein", "vein_id", "tax", "tax_pct", "owner_sect", "sect_bonus",
        "attribute", "attributes", "attr", "shop", "price", "item", "items",
        "exp_bonus", "buff", "balance", "ratio",
    ];

    readonly List<BossWindow> _windows;

    WorldBossTable(
        int version, bool pending, string entranceId, string entranceName, string regionId,
        int cap, BossConfig boss, List<BossWindow> windows)
    {
        Version = version;
        Pending = pending;
        EntranceId = entranceId;
        EntranceName = entranceName;
        RegionId = regionId;
        Cap = cap;
        Boss = boss;
        _windows = windows;
    }

    public int Version { get; }
    public bool Pending { get; }
    public string EntranceId { get; }
    public string EntranceName { get; }
    public string RegionId { get; }

    /// <summary>同一场最多几个人。这一场的人数上限只有这一个来源。</summary>
    public int Cap { get; }

    /// <summary>这一场打的首领。与 config/battle.json 的 boss 是两条独立的线。</summary>
    public BossConfig Boss { get; }

    public IReadOnlyList<BossWindow> Windows => _windows;

    /// <summary>没配这张表：带 worldboss 参数的连接一律拒，别的模式照跑。</summary>
    public static WorldBossTable Missing() =>
        new(0, false, "", "", "", 0, new BossConfig { Id = 0 }, []);

    public bool IsMissing => Version == 0;

    /// <summary>
    /// 读表 + 硬校验。openRegions 为空表示不校验入口所在地表（战斗服只算窗口）；
    /// 世界服传已开放地表集合，入口不在其中就起不来——方案写的是「在已经开放的
    /// 某一处地表入口出现」。
    /// </summary>
    public static WorldBossTable Load(string path, IReadOnlySet<string>? openRegions = null)
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

            var version = Int(root, "version");
            if (version < 1)
            {
                throw new ConfigException("世界 Boss 表的 version 必须大于 0，现在是 " + version);
            }

            var pending = Bool(root, "pending");

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
            if (openRegions is not null && !openRegions.Contains(regionId))
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
            var boss = ParseBoss(root);

            return new WorldBossTable(
                version, pending, entranceId, entranceName, regionId, cap, boss, windows);
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
                    "windows[" + id + "] 跨过了午夜：" + start + " + " + duration + "s");
            }

            windows.Add(new BossWindow(id, minutes, duration));
        }

        if (windows.Count == 0)
        {
            throw new ConfigException("windows 不能是空数组");
        }

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

    static BossConfig ParseBoss(JsonElement root)
    {
        var boss = Block(root, "boss", BossKeys, "boss");
        RejectForbidden(boss, "boss");
        var id = RequiredLong(boss, "id", "boss.id");
        if (id == 0)
        {
            throw new ConfigException("boss.id 不能是 0");
        }

        var name = RequiredText(boss, "name", "boss.name");
        var hp = RequiredLong(boss, "hp_max", "boss.hp_max");
        if (hp <= 0)
        {
            throw new ConfigException("boss.hp_max 必须大于 0，现在是 " + hp);
        }

        var exp = boss.TryGetProperty("exp_on_clear", out _)
            ? RequiredLong(boss, "exp_on_clear", "boss.exp_on_clear")
            : 0L;
        if (exp < 0)
        {
            throw new ConfigException("boss.exp_on_clear 不能是负数");
        }

        PointConfig? spawn = null;
        if (boss.TryGetProperty("spawn_mm", out var point))
        {
            if (point.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("boss.spawn_mm 必须是对象");
            }

            RejectExtraKeys(point, PointKeys, "boss.spawn_mm");
            spawn = new PointConfig
            {
                X = (int)RequiredLong(point, "x", "boss.spawn_mm.x"),
                Y = (int)RequiredLong(point, "y", "boss.spawn_mm.y"),
            };
        }

        BossAttackConfig? attack = null;
        if (boss.TryGetProperty("attack", out var attackNode))
        {
            attack = ParseAttack(attackNode, "boss.attack");
        }

        List<PhaseConfig>? phases = null;
        if (boss.TryGetProperty("phases", out var phaseList))
        {
            if (phaseList.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("boss.phases 必须是数组");
            }

            phases = [];
            foreach (var phase in phaseList.EnumerateArray())
            {
                if (phase.ValueKind != JsonValueKind.Object)
                {
                    throw new ConfigException("boss.phases 的每一项必须是对象");
                }

                RejectExtraKeys(phase, PhaseKeys, "boss.phases[]");
                RejectForbidden(phase, "boss.phases[]");
                phases.Add(new PhaseConfig
                {
                    Name = Text(phase, "name").Trim(),
                    HpPctFrom = (int)RequiredLong(phase, "hp_pct_from", "boss.phases[].hp_pct_from"),
                    HpPctTo = (int)RequiredLong(phase, "hp_pct_to", "boss.phases[].hp_pct_to"),
                    Attack = phase.TryGetProperty("attack", out var phaseAttack)
                        ? ParseAttack(phaseAttack, "boss.phases[].attack")
                        : null,
                });
            }
        }

        return new BossConfig
        {
            Id = (ulong)id,
            Name = name,
            HpMax = hp,
            SpawnMm = spawn ?? new PointConfig(),
            ExpOnClear = exp,
            Attack = attack,
            Phases = phases,
        };
    }

    static BossAttackConfig ParseAttack(JsonElement node, string where)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException(where + " 必须是对象");
        }

        RejectExtraKeys(node, AttackKeys, where);
        var damage = RequiredLong(node, "damage", where + ".damage");
        var interval = RequiredLong(node, "interval_ms", where + ".interval_ms");
        var foreswing = RequiredLong(node, "foreswing_ms", where + ".foreswing_ms");
        if (damage <= 0 || interval < 100 || foreswing < 0)
        {
            throw new ConfigException(
                where + " 的数值不合理：damage=" + damage + " interval_ms=" + interval
                + " foreswing_ms=" + foreswing);
        }

        return new BossAttackConfig
        {
            Damage = damage,
            IntervalMs = (int)interval,
            ForeswingMs = (int)foreswing,
        };
    }

    /// <summary>
    /// 这一刻是不是落在某一场里。纯计算，只读服务端时钟；同一时刻所有人算出来
    /// 同一把钥匙（入口 + 日期 + 窗口编号），所以「同一时刻一场」是结构保证。
    /// </summary>
    public WorldBossWindow WindowOf(DateTimeOffset now)
    {
        if (IsMissing)
        {
            return new WorldBossWindow(false, "", 0, 0, 0);
        }

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
                    true, EntranceId + "-" + date + "-" + window.Id,
                    startAt.ToUnixTimeMilliseconds(),
                    startAt.AddSeconds(window.DurationSeconds).ToUnixTimeMilliseconds(), 0);
            }
        }

        return new WorldBossWindow(false, "", 0, 0, NextStartAfter(utc).ToUnixTimeMilliseconds());
    }

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
            if (ForbiddenKeys.Contains(property.Name.ToLowerInvariant()))
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
}

/// <summary>这一刻的场次事实值。关着时 next_starts_at_ms 是下一次出现的时刻。</summary>
public sealed record WorldBossWindow(
    bool Open, string Key, long StartsAtMs, long EndsAtMs, long NextStartsAtMs);
