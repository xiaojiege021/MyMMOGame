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
