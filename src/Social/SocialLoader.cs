namespace Social;

/// <summary>
/// 真实启动与所有只读入口（--sect-list / --sect-config / --sect-state /
/// --sect-members）以及 --migrate 共用的同一份加载链：进程配置 + 数值正本，
/// 同一组参数、同一套硬校验。坏配置在这一步就起不来，不会带着半截配置跑；
/// 改了 sect_table 或 sect.json，只读入口与真实启动读到的都是同一份。
/// </summary>
public static class SocialLoader
{
    public static (SocialConfig Config, SectTable Sect) LoadAll(
        string configPath, string? sectTablePath)
    {
        var config = SocialConfig.Load(configPath);
        var sect = SectTable.Load(sectTablePath ?? config.SectTable);
        return (config, sect);
    }
}
