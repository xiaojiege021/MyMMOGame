namespace Global;

public sealed class GlobalProcess
{
    public async Task RunAsync(CancellationToken stopping)
    {
        Console.WriteLine("ready name=Global pid=" + Environment.ProcessId);
        try
        {
            await Task.Delay(Timeout.Infinite, stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Global pid=" + Environment.ProcessId);
    }
}
