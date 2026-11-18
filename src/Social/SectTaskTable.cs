using System.Text.Json;
using System.Text.Json.Serialization;
using CalendarTable = Mmo.Shared.Domain.CalendarTable;
using Mmo.Shared.Domain;

namespace Social;

/// <summary>
/// 宗门日常 / 周常（config/sect_tasks.json，Q4/J1）——任务、次数、奖励的唯一正本。
/// 方案 11.1 写定「建设和贡献来自宗门秘境和宗门任务，不从商城买」，但没给具体任务与
/// 数值，全部条目标 pending；改这份配置重启社交服生效，代码 / proto / SQL 不写第二套。
///
/// 次数（max_per_period）与周期（daily / weekly）在本表；**重置边界不在这里**：
/// 日常按 config/calendar.json 的业务日（<see cref="CalendarTable.DayKey"/>），
/// 周常按业务周首（<see cref="CalendarTable.WeekStartDate"/>）。Q3 起日历是唯一时钟，
/// 本表不另存小时数、社交服也不出现第二套数据库日期函数。
///
/// 等级门槛是**角色等级**（方案 11.1 的 45 级建宗门口径），社交服按角色服的只读
/// 战斗快照判；请求里带不进等级、次数、奖励与建设数。
/// </summary>
public sealed class SectTaskTable
{
    public const string WrittenSchema = "sect-task-table/1";

    static readonly string[] Periods = ["daily", "weekly"];

    static readonly string[] TopKeys = ["schema", "version", "pending", "note", "tasks"];
    static readonly string[] TaskKeys =
        ["id", "name", "period", "max_per_period", "require_level", "rewards", "pending"];
    static readonly string[] RewardKeys = ["contribution", "construction"];

    /// <summary>一条宗门任务。奖励只有贡献（给个人）与建设（给宗门）两种。</summary>
    public sealed record Task(
        string Id, string Name, string Period, int MaxPerPeriod, int RequireLevel,
        long Contribution, long Construction);

    public int Version { get; }
    public bool Pending { get; }
    public string Note { get; }
    public IReadOnlyList<Task> Tasks { get; }

    SectTaskTable(int version, bool pending, string note, IReadOnlyList<Task> tasks)
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

    /// <summary>周期名只认 daily / weekly（配置加载时已挡，这里给调用方复核用）。</summary>
    public static bool IsKnownPeriod(string? period) =>
        period is not null && Array.IndexOf(Periods, period) >= 0;

    /// <summary>
    /// 这一笔属于哪个周期。日常回业务日、周常回业务周首；两者都只由日历算，
    /// 不在这里写死 05:00 或周一。返回值直接当库里的 period_key 用。
    /// </summary>
    public static DateOnly PeriodKey(CalendarTable calendar, string period, DateTimeOffset now) =>
        period == "weekly" ? calendar.WeekStartDate(now) : calendar.DayKey(now);

    public static SectTaskTable Load(string path)
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

        // 硬口径：建设 / 贡献只从宗门任务与秘境来、不从商城买。配置里出现这种
        // 反口径整份起不来（与 sect.json 的同一组词）。
        foreach (var claim in ShopBuyConstructionClaims)
        {
            if (rawText.Contains(claim, StringComparison.Ordinal))
            {
                throw new ConfigException("sect_tasks.json 不允许出现「" + claim
                    + "」：建设和贡献只从宗门秘境和宗门任务来、不从商城买");
            }
        }

        using (var doc = JsonDocument.Parse(rawText))
        {
            if (!doc.RootElement.TryGetProperty("tasks", out var tasksNode)
                || tasksNode.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("sect_tasks.json 缺 tasks 段");
            }

            foreach (var key in doc.RootElement.EnumerateObject())
            {
                if (Array.IndexOf(TopKeys, key.Name) < 0)
                {
                    throw new ConfigException("sect_tasks.json 顶层有未知键：" + key.Name);
                }
            }

            var index = 0;
            foreach (var taskNode in tasksNode.EnumerateArray())
            {
                if (taskNode.ValueKind != JsonValueKind.Object)
                {
                    throw new ConfigException("sect_tasks.json 的 tasks[" + index + "] 不是对象");
                }

                foreach (var key in taskNode.EnumerateObject())
                {
                    if (Array.IndexOf(TaskKeys, key.Name) < 0)
                    {
                        throw new ConfigException("sect_tasks.json 的 tasks[" + index
                            + "] 有未知键：" + key.Name);
                    }
                }

                if (taskNode.TryGetProperty("rewards", out var rewardsNode)
                    && rewardsNode.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in rewardsNode.EnumerateObject())
                    {
                        if (Array.IndexOf(RewardKeys, key.Name) < 0)
                        {
                            throw new ConfigException("sect_tasks.json 的 tasks[" + index
                                + "].rewards 有未知键：" + key.Name);
                        }
                    }
                }

                index++;
            }
        }

        if (!string.Equals(wire.Schema, WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException("sect_tasks.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("sect_tasks.json 的 version 必须是正整数");
        }

        if (wire.Tasks is null || wire.Tasks.Count == 0)
        {
            throw new ConfigException("sect_tasks.json 的 tasks 不能为空（缺段）");
        }

        var tasks = new List<Task>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in wire.Tasks)
        {
            if (t is null)
            {
                throw new ConfigException("sect_tasks.json 的 tasks 有空项");
            }

            var id = (t.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("sect_tasks.json 有任务缺 id");
            }

            if (id.Length > 64 || id.Contains(' ') || id.Contains('/'))
            {
                throw new ConfigException("sect_tasks.json 的任务 id 不合法：" + id);
            }

            if (!seen.Add(id))
            {
                throw new ConfigException("sect_tasks.json 的任务 id 重复：" + id);
            }

            var name = (t.Name ?? "").Trim();
            if (name.Length == 0)
            {
                throw new ConfigException("sect_tasks.json 的 " + id + " 缺 name");
            }

            if (!IsKnownPeriod(t.Period))
            {
                throw new ConfigException("sect_tasks.json 的 " + id
                    + " period 只认 daily / weekly，现在是 " + (t.Period ?? "空"));
            }

            if (t.MaxPerPeriod < 1 || t.MaxPerPeriod > 100)
            {
                throw new ConfigException("sect_tasks.json 的 " + id
                    + " max_per_period 必须在 1..100，现在是 " + t.MaxPerPeriod);
            }

            if (t.RequireLevel < 1 || t.RequireLevel > 1000)
            {
                throw new ConfigException("sect_tasks.json 的 " + id
                    + " require_level 必须在 1..1000，现在是 " + t.RequireLevel);
            }

            var r = t.Rewards;
            if (r is null)
            {
                throw new ConfigException("sect_tasks.json 的 " + id + " 缺 rewards 段");
            }

            if (r.Contribution < 0 || r.Construction < 0)
            {
                throw new ConfigException("sect_tasks.json 的 " + id
                    + " 奖励不能为负（贡献 " + r.Contribution + " / 建设 " + r.Construction + "）");
            }

            if (r.Contribution == 0 && r.Construction == 0)
            {
                throw new ConfigException("sect_tasks.json 的 " + id
                    + " 贡献与建设不能同时为 0（完成必须有一条来源）");
            }

            tasks.Add(new Task(id, name, t.Period!, t.MaxPerPeriod, t.RequireLevel,
                r.Contribution, r.Construction));
        }

        return new SectTaskTable(wire.Version, wire.Pending, (wire.Note ?? "").Trim(), tasks);
    }

    // 与 SectTable 同一组反口径词：建设和贡献只从宗门秘境和宗门任务来。
    static readonly string[] ShopBuyConstructionClaims =
    {
        "商城可买建设", "商城可以买建设", "建设可从商城买", "建设能在商城买",
        "商城出售建设", "商城可买贡献",
    };

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
        [JsonPropertyName("period")] public string? Period { get; set; }
        [JsonPropertyName("max_per_period")] public int MaxPerPeriod { get; set; }
        [JsonPropertyName("require_level")] public int RequireLevel { get; set; }
        [JsonPropertyName("rewards")] public WireRewards? Rewards { get; set; }
        [JsonPropertyName("pending")] public bool Pending { get; set; }
    }

    sealed class WireRewards
    {
        [JsonPropertyName("contribution")] public long Contribution { get; set; }
        [JsonPropertyName("construction")] public long Construction { get; set; }
    }
}
