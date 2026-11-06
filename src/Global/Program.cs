using System.Runtime.InteropServices;

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

await new Global.GlobalProcess().RunAsync(stop.Token);
return 0;
