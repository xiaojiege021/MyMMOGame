namespace Gateway;

public sealed class GatewayProcess
{
    public async Task RunAsync(CancellationToken stopping)
    {
        Console.WriteLine("ready name=Gateway pid=" + Environment.ProcessId);
        try
        {
            await Task.Delay(Timeout.Infinite, stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Gateway pid=" + Environment.ProcessId);
    }
}
