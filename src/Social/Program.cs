using System.Runtime.InteropServices;
using Social;

// 14.8 D3h：参数解析与只读入口拆进 Cli/ 目录（SocialCli 参数面 / ConfigOnly 配置口 /
// ReadOnly 连库口 / Homestead 洞府口），这个文件只留「解析 → 按老顺序分派 → 真实启动」。
// 退出码 0 / 2 / 3 / 5 / 64 与启动回显一字不变；只读入口的行为由各自文件承载。
var cli = new SocialCli();
var parseCode = cli.Parse(args);
if (parseCode != 0)
{
    return parseCode;
}

// 不连库的配置口：命中任一个就返回它的退出码，否则落到连库口或真实启动。
if (cli.RunConfigOnly() is { } configCode)
{
    return configCode;
}

// 连库只读 / 仅核对用的写入口：命中就不起服（避免 bind 65000 打架）。
if (cli.HasReadOnlyEntry)
{
    return await cli.RunReadOnlyAsync();
}

// 真实启动：与上面所有只读入口共用同一份 Load + 同一组参数。

SocialTables startup;
HomesteadTable? startupHomestead;
HomesteadPlantTable? startupHomesteadPlant;
try
{
    // Q8 修正：真实启动也要吃 --party-table（Q7 只让只读口吃到了这个参数，
    // 真服一直是默认路径那份表；核对要切 need_greed 就切不动）。
    startup = SocialLoader.LoadAll(
        cli.configPath, cli.sectTablePath, cli.calendarTablePath, cli.sectTaskTablePath, cli.partyTablePath);
    startupHomestead = HomesteadTable.LoadIfPresent(cli.homesteadPath);
    startupHomesteadPlant = HomesteadPlantTable.LoadIfPresent(cli.homesteadPlantPath);
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

return await new SocialProcess(
    startup.Config, startup.Sect, startup.Calendar, startup.Tasks, startup.Party,
    startupHomestead, startupHomesteadPlant).RunAsync(stop.Token);
