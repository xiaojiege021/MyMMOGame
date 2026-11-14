using System.Runtime.InteropServices;

var configPath = "config/global.json";
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--config" && i + 1 < args.Length)
    {
        configPath = args[++i];
    }
}

if (args.Length == 1 && args[0] == "--self-check")
{
    var code = await new Global.ConfigSelfCheck().RunAsync();
    Console.WriteLine(code == 0 ? "self_check=ok" : "self_check=fail");
    return code;
}

// P3-4a1 赛季表只读核对：不连库、不启动进程，与真实启动共用同一个 SeasonTable.Load。
// --season-table 读产品正本三份表；--season-table-path 可以逐份换成临时表验坏配置。
// 路径只给核对脚本用，服务端启动时按 --config 所在目录热载，不认这两个参数。
if (args.Length >= 1 && (args[0] == "--season-table" || args[0] == "--season-table-path"))
{
    var seasonPath = "config/season.json";
    var dungeonsPath = "config/dungeons.json";
    var realmsPath = "config/realms.json";
    if (args[0] == "--season-table-path")
    {
        if (args.Length < 2)
        {
            Console.WriteLine("config_error=--season-table-path 需要赛季表路径");
            return 2;
        }

        seasonPath = args[1];
        if (args.Length > 2)
        {
            dungeonsPath = args[2];
        }

        if (args.Length > 3)
        {
            realmsPath = args[3];
        }
    }

    try
    {
        var table = Global.SeasonTable.Load(
            File.ReadAllText(seasonPath),
            File.ReadAllText(dungeonsPath),
            File.ReadAllText(realmsPath));
        Console.WriteLine("season_table_state=ok schema=" + Global.SeasonTable.WrittenSchema
            + " version=" + table.Version + " count=" + table.Count);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            Console.WriteLine("season_row=" + (i + 1) + " id=" + row.Id
                + " target=" + row.TargetDungeon);
        }

        Console.WriteLine("season_current=" + table.CurrentSeason
            + " target=" + table.CurrentTarget
            + " previous=" + table.PreviousSeason
            + " previous_target=" + table.PreviousTarget);
        return 0;
    }
    catch (Global.ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
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

return await new Global.GlobalProcess(configPath).RunAsync(stop.Token);
