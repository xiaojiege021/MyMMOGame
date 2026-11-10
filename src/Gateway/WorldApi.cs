using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.World;
using CharacterClient = Mmo.Proto.Character.CharacterService.CharacterServiceClient;
using WorldClient = Mmo.Proto.World.WorldService.WorldServiceClient;

namespace Gateway;

/// <summary>
/// 表世界分线的入口。账号从令牌里取，不信请求体；复核角色归属后转给世界服。
/// 分线号、人数、容量、区域编号都不从客户端读，网关与世界服都不接受伪造的分线状态。
/// 网关只做鉴权、转发和错误码翻译，分线的唯一写入方是世界服。
/// </summary>
public static class WorldApi
{
    public sealed record EnterBody(ulong? CharacterId, string? RegionId);

    public sealed record LeaveBody(ulong? CharacterId);

    // 客户端只给节点编号。产物、数量、背包格、事务号与账号都不在请求体里，
    // 多塞这些字段也不会被读进世界服的 GatherRequest。
    public sealed record GatherBody(ulong? CharacterId, string? NodeId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        // 通道建一次复用；地址在启动配置里定死，运行期不变。
        var worldChannel = GrpcChannel.ForAddress(config.WorldUpstream);
        var world = new WorldClient(worldChannel);
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var characters = new CharacterClient(characterChannel);

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

        // 采矿与采药。账号取自令牌，复核角色归属后才转给世界服；成功与否、节点占用
        // 与产物都由世界服判，产物由角色服落进背包。网关不碰库，也不判采集。
        app.MapPost("/world/gather", async context =>
        {
            if (!TryAuthorize(context, tokens, "world_gather", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            GatherBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<GatherBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.NodeId))
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

                var result = await world.GatherAsync(
                    new GatherRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        NodeId = body.NodeId.Trim(),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("world_gather account=" + account
                    + " id=" + body.CharacterId.Value
                    + " node=" + body.NodeId.Trim()
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " item=" + result.ItemId
                    + " count=" + result.Count
                    + " txn=" + result.TxnId);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    node_id = result.NodeId,
                    kind = result.Kind,
                    item_id = result.ItemId,
                    count = result.Count,
                    bag_slot = result.BagSlot,
                    txn_id = result.TxnId,
                    duplicate = result.Duplicate,
                    respawn_s = result.RespawnS,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "world_gather", account, ex);
            }
        });

        // 只读：地点、开口的人与势力的编号，供客户端与核对比对方案 2.5 到 2.7。
        app.MapGet("/world/regions", async context =>
        {
            if (!TryAuthorize(context, tokens, "world_regions", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            try
            {
                var result = await world.ListRegionsAsync(
                    new ListRegionsRequest(),
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    version = result.Version,
                    regions = result.Regions.Select(r => new
                    {
                        id = r.Id,
                        name = r.Name,
                        chapters = r.Chapters.ToArray(),
                        neighbors = r.Neighbors.ToArray(),
                        dialogue_ids = r.DialogueIds.ToArray(),
                        open = r.Open,
                    }).ToArray(),
                    speakers = result.Speakers.ToArray(),
                    factions = result.Factions.ToArray(),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "world_regions", account, ex);
            }
        });
    }

    // 复核归属的同时取回名字，只走服与服这一段；请求体里没有这个名字字段。
    static async Task<string?> ResolveAsync(
        CharacterClient characters, string account, ulong characterId, HttpContext context)
    {
        try
        {
            var reply = await characters.ListInventoryAsync(
                new ListInventoryRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: context.RequestAborted);
            return reply.CharacterName;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    // 复用角色服已有的只读方法来复核归属，不为分线另起一套身份数据。
    static async Task<bool> OwnsAsync(
        CharacterClient characters, string account, ulong characterId, HttpContext context)
    {
        try
        {
            await characters.ListInventoryAsync(
                new ListInventoryRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: context.RequestAborted);
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }

    static bool TryAuthorize(HttpContext context, TokenStore tokens, string what, out string account)
    {
        var token = TokenFrom(context.Request);
        if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out account, out _))
        {
            Console.WriteLine(what + "_reject reason=token");
            return false;
        }

        return true;
    }

    static string? TokenFrom(HttpRequest request)
    {
        var fromQuery = request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(fromQuery))
        {
            return fromQuery;
        }

        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    static async Task WriteRpcErrorAsync(HttpContext context, string what, string account, RpcException ex)
    {
        var code = ex.Status.Detail;
        var http = ex.StatusCode switch
        {
            StatusCode.InvalidArgument => 400,
            StatusCode.NotFound => 404,
            StatusCode.AlreadyExists => 409,
            StatusCode.FailedPrecondition => 412,
            StatusCode.Unavailable => 503,
            _ => 502,
        };

        Console.WriteLine(what + "_reject account=" + account + " code=" + code + " http=" + http);
        await WriteAsync(context, http, new { error = code });
    }

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(body, Json);
    }
}
