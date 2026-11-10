using Microsoft.Extensions.Hosting;

namespace World;

/// <summary>
/// 大世界同步节拍：方案写定 5–10Hz，取哪一个值在 config/world.json 的 sync_hz。
/// 广播只按这个节拍走，客户端上报频率、进线人数都不改它。
/// </summary>
public sealed class PositionBroadcaster(PositionHub hub, WorldConfig config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 频率只有一个来源：配置。代码里不写第二套数字。
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / config.SyncHz));
        Console.WriteLine("position_sync_hz=" + config.SyncHz);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                hub.Tick(DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // tick 挂了表世界就停在半路，必须打出来，不能当没看见。
            Console.WriteLine("position_tick_error=" + ex.GetType().Name + " " + ex.Message);
            throw;
        }
    }
}
