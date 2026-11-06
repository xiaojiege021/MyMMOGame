namespace Social;

public sealed class SocialProcess
{
    public async Task RunAsync(CancellationToken stopping)
    {
        Console.WriteLine("ready name=Social pid=" + Environment.ProcessId);
        try
        {
            await Task.Delay(Timeout.Infinite, stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Social pid=" + Environment.ProcessId);
    }
}
