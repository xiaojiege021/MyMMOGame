using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.World;
using Mmo.Shared.Domain;
using CharacterClient = Mmo.Proto.Character.CharacterService.CharacterServiceClient;
using WorldClient = Mmo.Proto.World.WorldService.WorldServiceClient;

namespace Gateway;

/// <summary>WorldApi HTTP：飞行与跨区。</summary>
public static partial class WorldApi
{
    static void RegisterRoam(WebApplication app, WorldClient world, CharacterClient characters, TokenStore tokens, CalendarTable calendar)
    {
        // 飞行。网关只认令牌并复核归属，权限由世界服问角色服的境界后判；
        // 没权限时是业务拒绝，回 200 + ok=false，不混进 gRPC 错误码。
        app.MapPost("/world/fly", async context =>
        {
            if (!TryAuthorize(context, tokens, "world_fly", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            FlyBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<FlyBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0 || body.WantFly is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(characters, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var result = await world.FlyAsync(
                    new FlyRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        WantFly = body.WantFly.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    flying = result.Flying,
                    can_fly = result.CanFly,
                    realm = result.Realm,
                    required_realm = result.RequiredRealm,
                    y = result.Y,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "world_fly", account, ex);
            }
        });

        // 跨区。目标地点编号是客户端唯一能给的；当前区域、相连与门槛都在世界服判。
        app.MapPost("/world/region", async context =>
        {
            if (!TryAuthorize(context, tokens, "world_region", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            CrossRegionBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CrossRegionBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.TargetRegion))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(characters, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var result = await world.CrossRegionAsync(
                    new CrossRegionRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TargetRegion = body.TargetRegion.Trim(),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    region_id = result.RegionId,
                    line_id = result.LineId,
                    line_no = result.LineNo,
                    realm = result.Realm,
                    required_realm = result.RequiredRealm,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "world_region", account, ex);
            }
        });
    }
}
