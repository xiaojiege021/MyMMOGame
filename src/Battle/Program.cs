using System.Runtime.InteropServices;

// 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
var configPath = "config/battle.json";
var arenaOnly = false;
string? arenaPath = null;
var battlefieldOnly = false;
string? battlefieldPath = null;
var siegeOnly = false;
string? siegePath = null;
var sectTechOnly = false;
string? sectTechPath = null;
var siegeViewOnly = false;
var siegeViewArgs = new List<string>();
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
        // 只读入口：打印战场表就退出。与真实启动共用同一个 BattlefieldTable.Load。
        case "--battlefield-table":
            battlefieldOnly = true;
            break;
        case "--battlefield-table-path" when i + 1 < args.Length:
            battlefieldOnly = true;
            battlefieldPath = args[++i];
            break;
        // 只读入口：打印城战表就退出，不起服务、不连库。与真实启动共用同一份加载。
        case "--siege-table":
            siegeOnly = true;
            break;
        case "--siege-table-path" when i + 1 < args.Length:
            siegeOnly = true;
            siegePath = args[++i];
            break;
        // 只读入口：打印宗门科技折算表就退出，不起服务、不连库、不查社交服。
        case "--sect-tech-table":
            sectTechOnly = true;
            break;
        case "--sect-tech-table-path" when i + 1 < args.Length:
            sectTechOnly = true;
            sectTechPath = args[++i];
            break;
        // 只读入口：不连库不起服，直接核对城战同屏档位的纯计算。
        case "--siege-view":
            siegeViewOnly = true;
            while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                siegeViewArgs.Add(args[++i]);
            }
            break;
    }
}

if (sectTechOnly)
{
    try
    {
        Battle.SectTechTable table = sectTechPath is null
            ? Battle.BattleConfig.Load(configPath).SectTech
            : Battle.SectTechTable.Load(sectTechPath);

        if (table.IsMissing)
        {
            Console.WriteLine("sect_tech=missing");
            return 0;
        }

        Console.WriteLine($"sect_tech_table_version={table.Version}"
            + $" power_pct_per_level={table.PowerPctPerLevel}"
            + $" max_level={table.MaxLevel}"
            + $" pending={(table.Pending ? "true" : "false")}");
        // 纯计算：等级 0 恒 1，等级越高系数越大；数字全部从配置现取，不写死。
        Console.WriteLine($"sect_tech_factor=level=0 {table.Factor(0):0.###}"
            + $" level=1 {table.Factor(1):0.###}"
            + $" level=max {table.Factor(table.MaxLevel):0.###}");
        return 0;
    }
    catch (Battle.ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

if (siegeViewOnly)
{
    return Battle.SiegeViewCli.Run(siegeViewArgs);
}

if (battlefieldOnly)
{
    try
    {
        Battle.BattlefieldTable table = battlefieldPath is null
            ? Battle.BattleConfig.Load(configPath).Battlefield
            : Battle.BattlefieldTable.Load(battlefieldPath);

        if (table.IsMissing)
        {
            Console.WriteLine("battlefield=missing");
            return 0;
        }

        Console.WriteLine($"battlefield_table_version={table.Version} mode={table.ModeId}"
            + $" team_size={table.TeamSize} teams={table.Teams} players={table.Players}"
            + $" ratio={table.Ratio} fill_timeout_ms={table.FillTimeoutMs}");
        Console.WriteLine($"battlefield_equipment=attack_per_level={table.AttackPerLevel}"
            + $" hp_per_level={table.HpPerLevel} mana_per_level={table.ManaPerLevel}"
            + $" heal_per_level={table.HealPerLevel}");
        Console.WriteLine($"battlefield_power=attack_weight={table.AttackWeight}"
            + $" hp_weight={table.HpWeight} mana_weight={table.ManaWeight}"
            + $" heal_weight={table.HealWeight}");
        return 0;
    }
    catch (Battle.ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

if (siegeOnly)
{
    try
    {
        Battle.SiegeTable table = siegePath is null
            ? Battle.BattleConfig.Load(configPath).Siege
            : Battle.SiegeTable.Load(siegePath);

        if (table.IsMissing)
        {
            Console.WriteLine("siege=missing");
            return 0;
        }

        Console.WriteLine($"siege_table_version={table.Version} mode={table.ModeId}"
            + $" team_size={table.TeamSize} teams={table.Teams}"
            + $" players={table.Players} balance=none fill_timeout_ms={table.FillTimeoutMs}"
            + $" view_full_model_limit={table.ViewFullModelLimit}");
        Console.WriteLine($"siege_equipment=attack_per_level={table.AttackPerLevel}"
            + $" hp_per_level={table.HpPerLevel} mana_per_level={table.ManaPerLevel}"
            + $" heal_per_level={table.HealPerLevel}");
        Console.WriteLine($"siege_power=attack_weight={table.AttackWeight}"
            + $" hp_weight={table.HpWeight} mana_weight={table.ManaWeight}"
            + $" heal_weight={table.HealWeight}");
        return 0;
    }
    catch (Battle.ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
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
