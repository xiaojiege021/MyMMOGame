using System.Runtime.InteropServices;

// 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
var configPath = "config/battle.json";
var arenaOnly = false;
string? arenaPath = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        // 只读入口：打印竞技场表就退出，不起服务、不连库。与真实启动共用同一份加载。
        case "--arena-table":
            arenaOnly = true;
            break;
        case "--arena-table-path" when i + 1 < args.Length:
            arenaOnly = true;
            arenaPath = args[++i];
            break;
    }
}

if (arenaOnly)
{
    try
    {
        Battle.ArenaMatchTable table = arenaPath is null
            ? Battle.BattleConfig.Load(configPath).ArenaMatch
            : Battle.ArenaMatchTable.Load(arenaPath);

        Console.WriteLine($"arena_table_version={table.Version}"
            + $" modes={table.Modes.Count} templates={table.Templates.Count}");
        foreach (var mode in table.Modes.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            Console.WriteLine($"arena_mode={mode.Id} name={mode.Name}"
                + $" team_size={mode.TeamSize} teams={mode.Teams} players={mode.Players}");
        }

        foreach (var template in table.Templates
            .OrderBy(t => t.ClassId, StringComparer.Ordinal)
            .ThenBy(t => t.BranchId, StringComparer.Ordinal))
        {
            Console.WriteLine($"arena_template=class={template.ClassId} branch={template.BranchId}"
                + $" attack={template.Attack} hp_max={template.HpMax} mana_max={template.ManaMax}"
                + $" heal_power={template.HealPower} threat={template.ThreatStability}");
        }

        return 0;
    }
    catch (Battle.ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

using var stop = new CancellationTokenSource();
PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.Cancel();
});
PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    stop.Cancel();
});

return await new Battle.BattleProcess(configPath).RunAsync(stop.Token);
