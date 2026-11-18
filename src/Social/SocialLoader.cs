using CalendarTable = Mmo.Shared.Domain.CalendarTable;

namespace Social;

/// <summary>
/// 真实启动与所有只读入口（--sect-list / --sect-config / --sect-state /
/// --sect-members / --sect-task-*）以及 --migrate 共用的同一份加载链：
/// 进程配置 + 数值正本 + 重置日历 + 宗门任务表，同一组参数、同一套硬校验。
/// 坏配置在这一步就起不来，不会带着半截配置跑；改了 sect_table / calendar_table /
/// sect_task_table，只读入口与真实启动读到的都是同一份。
///
/// Q4 起日历与任务表都是必读：宗门日常 / 周常的周期边界只从日历算，
/// 任务、次数与奖励只从任务表读；两份缺一份都起不来。
/// </summary>
public sealed record SocialTables(
    SocialConfig Config, SectTable Sect, CalendarTable Calendar,
    SectTaskTable Tasks);

public static class SocialLoader
{
    public static SocialTables LoadAll(
        string configPath, string? sectTablePath,
        string? calendarTablePath = null, string? sectTaskTablePath = null)
    {
        var config = SocialConfig.Load(configPath);
        var sect = SectTable.Load(sectTablePath ?? config.SectTable);
        CalendarTable calendar;
        try
        {
            calendar = CalendarTable.Load(calendarTablePath ?? config.CalendarTable);
        }
        catch (Mmo.Shared.Domain.ConfigException ex)
        {
            // 日历与宗门配置共用一种对外错误：社交服的只读口只认 ConfigException。
            throw new ConfigException(ex.Message);
        }

        var tasks = SectTaskTable.Load(sectTaskTablePath ?? config.SectTaskTable);
        return new SocialTables(config, sect, calendar, tasks);
    }
}
