using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.World;
using Mmo.Shared.Domain;
using CharacterClient = Mmo.Proto.Character.CharacterService.CharacterServiceClient;
using WorldClient = Mmo.Proto.World.WorldService.WorldServiceClient;

namespace Gateway;

/// <summary>WorldApi HTTP：进 / 离世界。</summary>
public static partial class WorldApi
{
    static void RegisterTravel(WebApplication app, WorldClient world, CharacterClient characters, TokenStore tokens, CalendarTable calendar)
    {
        app.MapPost("/world/enter", async context =>
        {
            if (!TryAuthorize(context, tokens, "world_enter", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            EnterBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<EnterBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.RegionId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                // 与前面各行同口径：账号取自令牌，角色服复核这个角色确实属于这个账号。
                // 名字牌文本也由角色服给，客户端请求体里带不进 display_name。
                var displayName = await ResolveAsync(characters, account, body.CharacterId.Value, context);
                if (displayName is null)
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var result = await world.EnterLineAsync(
                    new EnterLineRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        RegionId = body.RegionId.Trim(),
                        DisplayName = displayName,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("world_enter account=" + account
                    + " id=" + body.CharacterId.Value
                    + " region=" + body.RegionId
                    + " entered=" + result.Entered
                    + " reason=" + result.Reason
                    + " line=" + result.LineId
                    + " no=" + result.LineNo
                    + " size=" + result.LineSize
                    + " cap=" + result.LineCapacity
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    entered = result.Entered,
                    reason = result.Reason,
                    line_id = result.LineId,
                    region_id = result.RegionId,
                    line_no = result.LineNo,
                    line_size = result.LineSize,
                    line_capacity = result.LineCapacity,
                    duplicate = result.Duplicate,
                    neighbors = result.Neighbors.ToArray(),
                    dialogue_ids = result.DialogueIds.ToArray(),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "world_enter", account, ex);
            }
        });

        app.MapPost("/world/leave", async context =>
        {
            if (!TryAuthorize(context, tokens, "world_leave", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            LeaveBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<LeaveBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0)
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

                var result = await world.LeaveLineAsync(
                    new LeaveLineRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    left = result.Left,
                    line_id = result.LineId,
                    line_size = result.LineSize,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "world_leave", account, ex);
            }
        });
    }
}
