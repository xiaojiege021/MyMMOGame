namespace Character;

public sealed class CharacterProcess
{
    public async Task RunAsync(CancellationToken stopping)
    {
        Console.WriteLine("ready name=Character pid=" + Environment.ProcessId);
        try
        {
            await Task.Delay(Timeout.Infinite, stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Character pid=" + Environment.ProcessId);
    }
}
