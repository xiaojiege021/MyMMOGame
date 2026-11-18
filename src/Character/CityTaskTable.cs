using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 主城修炼 / 境界任务（config/city_tasks.json，Q5/J2）——任务、次数、门槛与奖励的唯一正本。
/// 方案 9.5 / 12 写定「灵石矿脉归主城辖区，采出的灵石大头做成修炼任务和境界任务的奖励，
/// 掌管方按城池税收抽成」，但没给具体任务与数值，条目全部标待定；改这份配置重启角色服
/// 生效，代码 / proto / SQL 不写第二套数字。
///
/// 周期（daily / weekly）与次数（max_per_period）在本表；**重置边界不在这里**：
/// 日常按 config/calendar.json 的业务日（<see cref="CalendarTable.DayKey"/>），
/// 周常按业务周首（<see cref="CalendarTable.WeekStartDate"/>）。本表不另存小时数，
/// 产品代码也不出现第二套数据库日期函数。
///
/// 奖励只能是灵石五档（方案「灵石仍只按城池任务发放、不从打怪来」）：币种取
/// config/stone_exchange.json 的档位表，金币 / 元宝 / 家园币在加载期就长不出来。
/// 城池编号必须在 config/city.json 里；境界门槛必须在 config/realms.json 的顺序表里
/// （别名按 RealmTable.Normalize 归一）。
/// </summary>
public sealed class CityTaskTable
{
    public const string WrittenSchema = "city-task-table/1";

    static readonly string[] Periods = ["daily", "weekly"];

    static readonly string[] TopKeys = ["schema", "version", "pending", "note", "tasks"];
    static readonly string[] TaskKeys =
    [
        "id", "name", "city_id", "period", "max_per_period",
        "require_level", "require_realm", "rewards", "pending",
    ];
    static readonly string[] RewardKeys = ["currency", "amount"];

    /// <summary>一条奖励：币种（灵石某一档）与数量。</summary>
    public sealed record Reward(string Currency, long Amount);

    /// <summary>
    /// 一条任务。RequireLevel = 0 表示不卡等级；RequireRealm 为空串表示不卡境界。
    /// 两者可以同时写，判定在仓储里各查各的。
    /// </summary>
    public sealed record Task(
        string Id, string Name, string CityId, string Period, int MaxPerPeriod,
        int RequireLevel, string RequireRealm, IReadOnlyList<Reward> Rewards, bool Pending);

    public int Version { get; }
    public bool Pending { get; }
    public string Note { get; }
    public IReadOnlyList<Task> Tasks { get; }

    /// <summary>
    /// 缺表降级：没有任务、版本 0。老卡环境里没有 config/city_tasks.json 时用它，
    /// 面板空、完成一律回 config_missing，不编一份假内容。
    /// </summary>
    public static CityTaskTable Empty { get; } = new(0, false, "", []);

    CityTaskTable(int version, bool pending, string note, IReadOnlyList<Task> tasks)
    {
        Version = version;
        Pending = pending;
        Note = note;
        Tasks = tasks;
    }

    /// <summary>按编号取任务；不在表里返回 null（调用方回 task_unknown）。</summary>
    public Task? TaskFor(string? id)
    {
        foreach (var t in Tasks)
        {
            if (string.Equals(t.Id, id, StringComparison.Ordinal))
            {
                return t;
            }
        }

        return null;
    }

    public static bool IsKnownPeriod(string? period) =>
        period is not null && Array.IndexOf(Periods, period) >= 0;

    /// <summary>
    /// 这一笔属于哪个周期。日常回业务日、周常回业务周首；两者都只由日历算，
    /// 不在这里写死 05:00 或周一。返回值直接当库里的 period_key 用。
    /// </summary>
    public static DateOnly PeriodKey(CalendarTable calendar, string period, DateTimeOffset now) =>
        period == "weekly" ? calendar.WeekStartDate(now) : calendar.DayKey(now);

    /// <summary>
    /// 读一份城池任务表。缺表由调用方决定要不要降级；这里只做硬校验：
    /// 坏表整份起不来，不带着半截配置跑。
    /// </summary>
    public static CityTaskTable Load(
        string path, CityTable cities, StoneExchangeTable stones, RealmTable realms)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配置文件 " + path);
        }

        var rawText = File.ReadAllText(path);
        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(rawText, JsonOptions)
                ?? throw new ConfigException("配置是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配置不是合法 JSON：" + ex.Message);
        }

        using (var doc = JsonDocument.Parse(rawText))
        {
            if (!doc.RootElement.TryGetProperty("tasks", out var tasksNode)
                || tasksNode.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("city_tasks.json 缺 tasks 段");
            }

            foreach (var key in doc.RootElement.EnumerateObject())
            {
                if (Array.IndexOf(TopKeys, key.Name) < 0)
                {
                    throw new ConfigException("city_tasks.json 顶层有未知键：" + key.Name);
                }
            }

            var index = 0;
            foreach (var taskNode in tasksNode.EnumerateArray())
            {
                if (taskNode.ValueKind != JsonValueKind.Object)
                {
                    throw new ConfigException("city_tasks.json 的 tasks[" + index + "] 不是对象");
                }

                foreach (var key in taskNode.EnumerateObject())
                {
                    if (Array.IndexOf(TaskKeys, key.Name) < 0)
                    {
                        throw new ConfigException("city_tasks.json 的 tasks[" + index
                            + "] 有未知键：" + key.Name);
                    }
                }

                if (taskNode.TryGetProperty("rewards", out var rewardsNode)
                    && rewardsNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rewardNode in rewardsNode.EnumerateArray())
                    {
                        if (rewardNode.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        foreach (var key in rewardNode.EnumerateObject())
                        {
                            if (Array.IndexOf(RewardKeys, key.Name) < 0)
                            {
                                throw new ConfigException("city_tasks.json 的 tasks[" + index
                                    + "].rewards 有未知键：" + key.Name);
                            }
                        }
                    }
                }

                index++;
            }
        }

        if (!string.Equals(wire.Schema, WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException("city_tasks.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("city_tasks.json 的 version 必须是正整数");
        }

        if (wire.Tasks is null || wire.Tasks.Count == 0)
        {
            throw new ConfigException("city_tasks.json 的 tasks 不能为空（缺段）");
        }

        var cityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var city in cities.Cities)
        {
            cityIds.Add(city.CityId);
        }

        var tasks = new List<Task>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in wire.Tasks)
        {
            if (t is null)
            {
                throw new ConfigException("city_tasks.json 的 tasks 有空项");
            }

            var id = (t.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("city_tasks.json 有任务缺 id");
            }

            if (id.Length > 64 || id.Contains(' ') || id.Contains('/'))
            {
                throw new ConfigException("city_tasks.json 的任务 id 不合法：" + id);
            }

            if (!seen.Add(id))
            {
                throw new ConfigException("city_tasks.json 的任务 id 重复：" + id);
            }

            var name = (t.Name ?? "").Trim();
            if (name.Length == 0)
            {
                throw new ConfigException("city_tasks.json 的 " + id + " 缺 name");
            }

            var cityId = (t.CityId ?? "").Trim();
            if (cityId.Length == 0)
            {
                throw new ConfigException("city_tasks.json 的 " + id + " 缺 city_id");
            }

            // 城池表是唯一正本：任务挂在不存在的城池上，抽成记账就无从查税。
            if (!cityIds.Contains(cityId))
            {
                throw new ConfigException("city_tasks.json 的 " + id
                    + " city_id 不在 config/city.json 里：" + cityId);
            }

            if (!IsKnownPeriod(t.Period))
            {
                throw new ConfigException("city_tasks.json 的 " + id
                    + " period 只认 daily / weekly，现在是 " + (t.Period ?? "空"));
            }

            if (t.MaxPerPeriod < 1 || t.MaxPerPeriod > 100)
            {
                throw new ConfigException("city_tasks.json 的 " + id
                    + " max_per_period 必须在 1..100，现在是 " + t.MaxPerPeriod);
            }

            if (t.RequireLevel < 0 || t.RequireLevel > 1000)
            {
                throw new ConfigException("city_tasks.json 的 " + id
                    + " require_level 必须在 0..1000（0 表示不卡），现在是 " + t.RequireLevel);
            }

            var requireRealm = (t.RequireRealm ?? "").Trim();
            if (requireRealm.Length > 0)
            {
                var normalized = realms.Normalize(requireRealm);
                if (normalized.Length == 0 || realms.Rank(normalized) < 1)
                {
                    throw new ConfigException("city_tasks.json 的 " + id
                        + " require_realm 不在 config/realms.json 的顺序表里：" + requireRealm);
                }

                requireRealm = normalized;
            }

            if (t.Rewards is null || t.Rewards.Count == 0)
            {
                throw new ConfigException("city_tasks.json 的 " + id
                    + " 缺 rewards（完成必须有一条灵石来源）");
            }

            var rewards = new List<Reward>();
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            var rewardIndex = 0;
            foreach (var r in t.Rewards)
            {
                if (r is null)
                {
                    throw new ConfigException("city_tasks.json 的 " + id
                        + ".rewards[" + rewardIndex + "] 是空项");
                }

                var kind = (r.Currency ?? "").Trim();
                if (kind.Length == 0)
                {
                    throw new ConfigException("city_tasks.json 的 " + id
                        + ".rewards[" + rewardIndex + "] 缺 currency");
                }

                // 方案：灵石只按城池任务发放。这里只认灵石五档，金币 / 元宝 / 家园币
                // 在加载期就长不出来，免得任务变成第二条发金币的路。
                if (stones.IndexOf(kind) < 0)
                {
                    throw new ConfigException("city_tasks.json 的 " + id
                        + " 奖励币种必须是灵石五档（config/stone_exchange.json）：" + kind);
                }

                if (!kinds.Add(kind))
                {
                    throw new ConfigException("city_tasks.json 的 " + id
                        + " 奖励币种重复：" + kind);
                }

                if (r.Amount < 1 || r.Amount > 1_000_000)
                {
                    throw new ConfigException("city_tasks.json 的 " + id
                        + " 奖励数量必须在 1..1000000，现在是 " + r.Amount);
                }

                rewards.Add(new Reward(kind, r.Amount));
                rewardIndex++;
            }

            tasks.Add(new Task(id, name, cityId, t.Period!, t.MaxPerPeriod,
                t.RequireLevel, requireRealm, rewards, t.Pending));
        }

        return new CityTaskTable(wire.Version, wire.Pending, (wire.Note ?? "").Trim(), tasks);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("pending")] public bool Pending { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("tasks")] public List<WireTask?>? Tasks { get; set; }
    }

    sealed class WireTask
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("city_id")] public string? CityId { get; set; }
        [JsonPropertyName("period")] public string? Period { get; set; }
        [JsonPropertyName("max_per_period")] public int MaxPerPeriod { get; set; }
        [JsonPropertyName("require_level")] public int RequireLevel { get; set; }
        [JsonPropertyName("require_realm")] public string? RequireRealm { get; set; }
        [JsonPropertyName("rewards")] public List<WireReward?>? Rewards { get; set; }
        [JsonPropertyName("pending")] public bool Pending { get; set; }
    }

    sealed class WireReward
    {
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
    }
}
