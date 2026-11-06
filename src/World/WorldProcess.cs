namespace World;

public sealed class WorldProcess
{
    public async Task RunAsync(CancellationToken stopping)
    {
        Console.WriteLine("ready name=World pid=" + Environment.ProcessId);
        try
        {
            await Task.Delay(Timeout.Infinite, stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=World pid=" + Environment.ProcessId);
    }
}
