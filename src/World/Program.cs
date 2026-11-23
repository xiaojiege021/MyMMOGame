using System.Runtime.InteropServices;
using World;

// 14.8 D3h：参数解析与只读入口拆进 Cli/ 目录（WorldCli 参数面 / State 采集与位置 /
// Regions 地表与解锁 / Boss 世界 Boss），这个文件只留「解析 → 按老顺序分派 → 真实启动」。
// 退出码 0 / 2 / 64 与启动回显一字不变。
var cli = new WorldCli();
var parseCode = cli.Parse(args);
if (parseCode != 0)
{
    return parseCode;
}

// 只读入口：命中任一个就返回它的退出码，否则起真实世界服。
if (cli.RunStateEntries() is { } entryCode)
{
    return entryCode;
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

return await new WorldProcess(
    cli.configPath, cli.gatherConfigPath, cli.flightConfigPath, cli.worldBossConfigPath,
    cli.gatherSpreadConfigPath, cli.regionUnlockPath)
    .RunAsync(stop.Token);

/// <summary>只给只读入口的占位自检用：固定结论的分线探测口，不读注册表。</summary>
sealed class ProbeStanding(bool onEntrance, string lineKey, int occupancy, int capacity) : IEntranceLine
{
    public LineStanding Where(ulong characterId) => new(onEntrance, lineKey, occupancy, capacity);
}
