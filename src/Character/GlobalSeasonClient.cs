using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Trade;

namespace Character;

/// <summary>
/// 当前是哪一季。赛季归全局服务（技术架构 3 节、P3-4a1），角色服只读这一个事实值：
/// 通行证按季分组、按季领取，季不能由客户端说，也不能由角色服自己从配置猜。
/// </summary>
public interface ISeasonSource
{
    /// <summary>当前季编号。上游没起、没配或回包是空的，一律返回 null，调用方据此拒绝。</summary>
    Task<string?> CurrentSeasonAsync(CancellationToken stopping);
}

/// <summary>到全局服务 DescribeSeason 的只读客户端。明文 gRPC（h2c），channel 复用。</summary>
public sealed class GrpcSeasonSource(TradeService.TradeServiceClient client) : ISeasonSource
{
    public async Task<string?> CurrentSeasonAsync(CancellationToken stopping)
    {
        try
        {
            var reply = await client.DescribeSeasonAsync(
                new DescribeSeasonRequest(),
                deadline: DateTime.UtcNow.AddSeconds(3),
                cancellationToken: stopping);

            var id = (reply.SeasonId ?? "").Trim();
            if (!reply.Found || id.Length == 0)
            {
                Console.WriteLine("season_source=missing");
                return null;
            }

            return id;
        }
        catch (RpcException ex)
        {
            // 上游没起 / 不可达：不猜一季，直接让调用方拒绝这一笔。
            Console.WriteLine("season_source_error=" + ex.StatusCode);
            return null;
        }
    }
}

/// <summary>没有配全局服务上游时用这个：一律回 null，通行证的购买与领取都拒绝。</summary>
public sealed class NoSeasonSource : ISeasonSource
{
    public Task<string?> CurrentSeasonAsync(CancellationToken stopping) =>
        Task.FromResult<string?>(null);
}
