using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.World;
using Mmo.Shared.Domain;
using CharacterClient = Mmo.Proto.Character.CharacterService.CharacterServiceClient;
using WorldClient = Mmo.Proto.World.WorldService.WorldServiceClient;

namespace Gateway;

/// <summary>WorldApi HTTP：世界 Boss 只读。</summary>
public static partial class WorldApi
{
    static void RegisterWorldBoss(WebApplication app, WorldClient world, CharacterClient characters, TokenStore tokens, CalendarTable calendar)
    {
        // 只读：地表入口这一刻开没开、还能进多少人、上一场记下的宗门名字。
        // 开门与否只由世界服的时钟与窗口决定，这条路由不接任何「请开门」的参数。
        app.MapGet("/world/worldboss", async context =>
        {
            if (!TryAuthorize(context, tokens, "world_worldboss", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            try
            {
                var result = await world.DescribeWorldBossAsync(
                    new DescribeWorldBossRequest(),
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    visible = result.Visible,
                    reason = result.Reason,
                    entrance_id = result.EntranceId,
                    entrance_name = result.EntranceName,
                    region_id = result.RegionId,
                    window_key = result.WindowKey,
                    starts_at_ms = result.StartsAtMs,
                    ends_at_ms = result.EndsAtMs,
                    next_starts_at_ms = result.NextStartsAtMs,
                    cap = result.Cap,
                    // Q11：这一刻发出去的名额与还能进几个人，以及名额的三个时长。
                    // 客户端只展示，不参与判定；名额账只在世界服。
                    current = result.Current,
                    remaining = result.Remaining,
                    hold_s = result.HoldS,
                    renew_s = result.RenewS,
                    lost_s = result.LostS,
                    killed = result.Killed,
                    owner_sect_id = result.OwnerSectId,
                    owner_sect_name = result.OwnerSectName,
                    owner_window_key = result.OwnerWindowKey,
                    owner_recorded_at_ms = result.OwnerRecordedAtMs,
                    // Q3/G1：世界 Boss 的时刻仍是服务端算出的 UTC 毫秒，展示时区只从
                    // config/calendar.json 读。改 timezone 后这三条跟着变；客户端不另算。
                    display_timezone = calendar.TimeZoneId,
                    starts_at_local = calendar.FormatLocal((long)result.StartsAtMs),
                    ends_at_local = calendar.FormatLocal((long)result.EndsAtMs),
                    next_starts_at_local = calendar.FormatLocal((long)result.NextStartsAtMs),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "world_worldboss", account, ex);
            }
        });
    }
}
