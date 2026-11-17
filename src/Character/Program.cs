using System.Runtime.InteropServices;
using Character;

// 角色服进程只保留纯启动：读 --config → 构造 CharacterProcess → 注册 SIGTERM/SIGINT → RunAsync。
// 133 个核对 flag 的解析与只读 / 连库核对链已迁到 tools/probe/CliProbe（D1b）。
var configPath = "config/character.json";
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--config" && i + 1 < args.Length)
    {
        configPath = args[++i];
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

return await new CharacterProcess(configPath).RunAsync(stop.Token);
