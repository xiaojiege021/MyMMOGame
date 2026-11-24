using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Trade;
using Mmo.Shared.Domain;

namespace Gateway;

/// <summary>
/// P3-4a1 赛季只读 HTTP 入口。网关只认令牌，再把请求转给全局服务的 DescribeSeason；
/// 当前是哪一季、目标是哪一处秘境、上一季的目标是什么，全由全局服务从配置确认。
/// 请求里除了令牌不带任何参数，query 中塞 season_id / target_dungeon / reward 一律不读，
/// 客户端表达不了「把赛季改成哪一季」，也带不进任何奖励。
/// </summary>
public static class SeasonApi
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void Map(
        WebApplication app, TokenStore tokens, GatewayConfig config, CalendarTable calendar)
    {
        var tradeChannel = GrpcChannel.ForAddress(config.GlobalUpstream);
        var trade = new TradeService.TradeServiceClient(tradeChannel);

        app.MapGet("/season/current", async context =>
        {
            try
            {
                var window = calendar.SeasonOf(DateTimeOffset.UtcNow);
                var reply = await trade.DescribeSeasonAsync(
                    new DescribeSeasonRequest(),
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    found = reply.Found,
                    season_id = reply.SeasonId,
                    target_dungeon = reply.TargetDungeon,
                    previous_season_id = reply.PreviousSeasonId,
                    previous_target_dungeon = reply.PreviousTargetDungeon,
                    // Q3/G1：赛季边界也只从 calendar.json 算，与全局服务同一个时区与日界。
                    // 当前是哪一季仍由全局服务回答；这里只给展示用的边界时刻。
                    season_index = window.Index,
                    season_starts_at_ms = window.StartsAt.ToUnixTimeMilliseconds(),
                    season_ends_at_ms = window.EndsAt.ToUnixTimeMilliseconds(),
                    season_starts_at_local = calendar.FormatLocal(window.StartsAt),
                    season_ends_at_local = calendar.FormatLocal(window.EndsAt),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("season_current_error=" + ex.StatusCode);
                await WriteAsync(context, 503, new { error = "season_upstream_unavailable" });
            }
        });
    }
    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
