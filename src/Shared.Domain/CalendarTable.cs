using System.Globalization;
using System.Text.Json;

namespace Mmo.Shared.Domain;

/// <summary>
/// 重置日历（config/calendar.json）。Q3/G1 起所有进程共用这一份：日界、周界、赛季边界
/// 与展示时区都只从这里算，产品代码里不再出现第二套数据库时钟。
///
/// 日界：本地时区到 <see cref="DailyResetHour"/> 才算新的一天；05:00 之前仍属于前一天，
/// 所以 <see cref="DayKey"/> 回的是「业务日」，不是数据库的当天日期。
/// 周界：本地时区 <see cref="WeeklyResetDay"/> 的 <see cref="WeeklyResetHour"/> 起算；
/// 与日重置小时分开，两个值都在配置里。
/// 赛季：<see cref="SeasonStartDate"/> 当天 05:00（同一日界）起算，按 <see cref="SeasonLengthDays"/>
/// 天切一段；改时区或改日重置小时，赛季边界跟着一起挪。
///
/// 表里没有第二个时区：世界 Boss 的展示时刻也用这里的时区，Q11 只做界面格式化，不另读一份。
/// </summary>
public sealed class CalendarTable
{
    public const string WrittenSchema = "calendar/2";

    static readonly string[] TopKeys =
    [
        "schema", "version", "pending", "note", "timezone",
        "daily_reset_hour", "weekly_reset", "season",
    ];

    static readonly string[] WeeklyKeys = ["day", "hour"];
    static readonly string[] SeasonKeys = ["start_date", "length_days"];

    static readonly string[] DayNames =
        ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    CalendarTable(
        int version, bool pending, string timezone, TimeZoneInfo zone,
        int dailyResetHour, DayOfWeek weeklyResetDay, int weeklyResetHour,
        DateOnly seasonStartDate, int seasonLengthDays)
    {
        Version = version;
        Pending = pending;
        TimeZoneId = timezone;
        Zone = zone;
        DailyResetHour = dailyResetHour;
        WeeklyResetDay = weeklyResetDay;
        WeeklyResetHour = weeklyResetHour;
        SeasonStartDate = seasonStartDate;
        SeasonLengthDays = seasonLengthDays;
    }

    public int Version { get; }
    public bool Pending { get; }
    public string TimeZoneId { get; }
    public TimeZoneInfo Zone { get; }
    public int DailyResetHour { get; }
    public DayOfWeek WeeklyResetDay { get; }
    public int WeeklyResetHour { get; }
    public DateOnly SeasonStartDate { get; }
    public int SeasonLengthDays { get; }

    /// <summary>日界：这一笔属于哪个业务日。本地 05:00 前仍算前一天。</summary>
    public DateOnly DayKey(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone).DateTime;
        var day = DateOnly.FromDateTime(local);
        return local.Hour < DailyResetHour ? day.AddDays(-1) : day;
    }

    /// <summary>周界：这一笔属于哪个业务周（返回周首的业务日，不是时间戳）。</summary>
    public DateOnly WeekStartDate(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone).DateTime;
        var day = DateOnly.FromDateTime(local);
        var back = ((int)day.DayOfWeek - (int)WeeklyResetDay + 7) % 7;
        var candidate = day.AddDays(-back);
        var candidateAt = candidate.ToDateTime(new TimeOnly(WeeklyResetHour, 0));
        if (candidateAt > local)
        {
            candidate = candidate.AddDays(-7);
        }

        return candidate;
    }

    /// <summary>
    /// 周界的时间戳。高难本按「进本记录 >= 本周周首」计数，比较的是 timestamptz，
    /// 所以这里给的是周首当天本地时区 <see cref="WeeklyResetHour"/> 的真实时刻。
    /// </summary>
    /// <summary>按 UTC 返回：Npgsql 只接受零偏移的 DateTimeOffset 写 timestamptz。</summary>
    public DateTimeOffset WeekStartInstant(DateTimeOffset now) =>
        AtLocal(WeekStartDate(now).ToDateTime(new TimeOnly(WeeklyResetHour, 0))).ToUniversalTime();

    /// <summary>日界的时间戳：业务日当天本地时区 <see cref="DailyResetHour"/>。</summary>
    public DateTimeOffset DayStartInstant(DateOnly day) =>
        AtLocal(day.ToDateTime(new TimeOnly(DailyResetHour, 0)));

    /// <summary>
    /// 这一刻落在第几段赛季（从 0 起）。边界按本地日界算；赛季还没开始时是负数，
    /// 调用方按「还没开赛」处理，不把它夹成第 0 段。
    /// </summary>
    public SeasonWindow SeasonOf(DateTimeOffset now)
    {
        var start = SeasonStart(0);
        var index = (int)Math.Floor((now - start).TotalDays / SeasonLengthDays);
        while (SeasonStart(index + 1) <= now)
        {
            index++;
        }

        while (SeasonStart(index) > now)
        {
            index--;
        }

        return new SeasonWindow(index, SeasonStart(index), SeasonStart(index + 1));
    }

    /// <summary>第 index 段赛季的起始时刻。负数表示赛季开始之前。</summary>
    public DateTimeOffset SeasonStart(int index) =>
        AtLocal(SeasonStartDate.AddDays(index * SeasonLengthDays)
            .ToDateTime(new TimeOnly(DailyResetHour, 0)));

    /// <summary>
    /// 把一个服务端时刻按配置时区写成可展示的 ISO 8601 字符串（带偏移）。
    /// 只做展示格式化，不参与任何判定；时刻本身仍是 UTC 毫秒。
    /// </summary>
    public string FormatLocal(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Zone)
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>零值 / 缺失时刻显示成空串，不编一个 1970 年的时间。</summary>
    public string FormatLocal(long unixMs) =>
        unixMs <= 0 ? "" : FormatLocal(DateTimeOffset.FromUnixTimeMilliseconds(unixMs));

    DateTimeOffset AtLocal(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    public static CalendarTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到日历表 " + path);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new ConfigException("日历表读不出来：" + ex.Message);
        }

        return LoadText(text, path);
    }

    /// <summary>读表 + 硬校验。坏表起不来，不带着半截时钟跑。</summary>
    public static CalendarTable LoadText(string text, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("日历表不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("日历表顶层必须是对象：" + source);
            }

            RejectExtraKeys(root, TopKeys, "顶层");

            var schema = Text(root, "schema");
            if (!string.Equals(schema, WrittenSchema, StringComparison.Ordinal))
            {
                throw new ConfigException(
                    "日历表的 schema 必须是 " + WrittenSchema + "，现在是「" + schema + "」");
            }

            var version = Int(root, "version");
            if (version < 1)
            {
                throw new ConfigException("日历表的 version 必须大于 0，现在是 " + version);
            }

            var pending = Bool(root, "pending");

            var timezone = RequiredText(root, "timezone", "timezone");
            TimeZoneInfo zone;
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new ConfigException(
                    "日历表的 timezone 不是可用的 IANA 时区：「" + timezone + "」（" + ex.Message + "）");
            }

            var dailyResetHour = Int(root, "daily_reset_hour");
            if (dailyResetHour is < 0 or > 23)
            {
                throw new ConfigException(
                    "日历表的 daily_reset_hour 必须在 0 到 23 之间，现在是 " + dailyResetHour);
            }

            var weekly = Block(root, "weekly_reset", WeeklyKeys, "weekly_reset");
            var dayText = RequiredText(weekly, "day", "weekly_reset.day").ToLowerInvariant();
            var dayIndex = Array.IndexOf(DayNames, dayText);
            if (dayIndex < 0)
            {
                throw new ConfigException(
                    "日历表的 weekly_reset.day 只认 sunday 到 saturday，现在是「" + dayText + "」");
            }

            var weeklyResetHour = Int(weekly, "hour");
            if (weeklyResetHour is < 0 or > 23)
            {
                throw new ConfigException(
                    "日历表的 weekly_reset.hour 必须在 0 到 23 之间，现在是 " + weeklyResetHour);
            }

            var season = Block(root, "season", SeasonKeys, "season");
            var startText = RequiredText(season, "start_date", "season.start_date");
            if (!DateOnly.TryParseExact(
                    startText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var startDate))
            {
                throw new ConfigException(
                    "日历表的 season.start_date 要写成 yyyy-MM-dd，现在是「" + startText + "」");
            }

            var lengthDays = Int(season, "length_days");
            if (lengthDays is < 1 or > 365)
            {
                throw new ConfigException(
                    "日历表的 season.length_days 必须在 1 到 365 之间，现在是 " + lengthDays);
            }

            return new CalendarTable(
                version, pending, timezone, zone, dailyResetHour,
                (DayOfWeek)dayIndex, weeklyResetHour, startDate, lengthDays);
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
            throw new ConfigException("日历表缺 " + name + " 段");
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
            throw new ConfigException("日历表缺 " + label);
        }

        return text;
    }

    static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : throw new ConfigException("日历表缺 " + name);

    /// <summary>一段赛季：[StartsAt, EndsAt)。Index 从 0 起，改时区会整体平移。</summary>
    public sealed record SeasonWindow(int Index, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
}
