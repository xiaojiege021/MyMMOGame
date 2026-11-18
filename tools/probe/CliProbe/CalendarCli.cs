using System.Globalization;

using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// Q3/G1：日历只读入口。把 config/calendar.json 算出来的业务日、周首、赛季边界与
/// 展示时刻原样打出来，不连库、不启动进程；坏表在这一步就报 config_error。
/// 与角色服 / 世界服 / 战斗服 / 网关 / 全局服务共用同一个 <see cref="CalendarTable"/>。
/// </summary>
internal static class CalendarCli
{
    /// <summary>Q3/G1：--calendar / --calendar-path / --at 三个 flag 的解析。
    /// --at 可给 ISO 8601，也可给毫秒时间戳。</summary>
    public static void Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--calendar":
                calendarState = true;
                break;
            case "--calendar-path" when i + 1 < args.Length:
                calendarPath = args[++i];
                break;
            case "--at" when i + 1 < args.Length:
                calendarAt = args[++i];
                break;
        }
    }

    public static int? TryReadOnly()
    {
        if (!calendarState)
        {
            return null;
        }

        CalendarTable calendar;
        try
        {
            calendar = CalendarTable.Load(
                string.IsNullOrWhiteSpace(calendarPath)
                    ? CharacterConfig.DefaultCalendarTable
                    : calendarPath);
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

        if (!TryParseInstant(calendarAt, out var at))
        {
            Console.WriteLine("calendar_at_error=--at 要写成 ISO 8601 或毫秒时间戳："
                + (calendarAt ?? ""));
            return 2;
        }

        var season = calendar.SeasonOf(at);
        Console.WriteLine("calendar=ok version=" + calendar.Version
            + " pending=" + (calendar.Pending ? "true" : "false")
            + " timezone=" + calendar.TimeZoneId
            + " daily_reset_hour=" + calendar.DailyResetHour
            + " weekly_reset=" + calendar.WeeklyResetDay.ToString().ToLowerInvariant()
            + "@" + calendar.WeeklyResetHour
            + " season_start=" + calendar.SeasonStartDate.ToString("yyyy-MM-dd")
            + " season_length_days=" + calendar.SeasonLengthDays);
        Console.WriteLine("calendar_at=at_ms=" + at.ToUnixTimeMilliseconds()
            + " local=" + calendar.FormatLocal(at)
            + " day=" + calendar.DayKey(at).ToString("yyyy-MM-dd")
            + " week_start=" + calendar.WeekStartDate(at).ToString("yyyy-MM-dd")
            + " week_start_ms=" + calendar.WeekStartInstant(at).ToUnixTimeMilliseconds()
            + " season_index=" + season.Index
            + " season_start_ms=" + season.StartsAt.ToUnixTimeMilliseconds()
            + " season_end_ms=" + season.EndsAt.ToUnixTimeMilliseconds()
            + " season_start_local=" + calendar.FormatLocal(season.StartsAt)
            + " season_end_local=" + calendar.FormatLocal(season.EndsAt));
        // 世界 Boss 的展示时刻与上面同一个时区；这里只格式化，不判开没开。
        Console.WriteLine("calendar_boss=display_timezone=" + calendar.TimeZoneId
            + " display_sample_ms=" + at.ToUnixTimeMilliseconds()
            + " display_sample_local=" + calendar.FormatLocal(at));
        return 0;
    }

    /// <summary>--at 的解析（ISO 8601 或毫秒时间戳）。Q5 的城池任务入口复用同一份。</summary>
    internal static bool TryParseInstant(string? text, out DateTimeOffset value)
    {
        value = DateTimeOffset.UtcNow;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return true;
        }

        return DateTimeOffset.TryParse(
            trimmed, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }
}
