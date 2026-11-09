using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// 建角色的入口。账号从令牌里取，不信请求体——客户端改不了自己是谁。
/// 真正的检查与落库在角色服（角色的唯一写入），这里只做鉴权、转发和错误码翻译。
/// </summary>
public static class CharacterApi
{
    public sealed record CreateBody(string? Name, string? Race, string? Appearance, string? ClassId, string? Branch);

    public sealed record LevelBody(ulong? CharacterId, int? Level);

    public sealed record AddItemBody(ulong? CharacterId, uint? Slot, string? ItemId, int? Count);

    public sealed record CanTradeBody(ulong? CharacterId, uint? Slot);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        // 通道建一次复用；地址在启动配置里定死，运行期不变。
        var channel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var client = new CharacterService.CharacterServiceClient(channel);

        app.MapPost("/character", async context =>
        {
            var token = TokenFrom(context.Request);
            if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out var account, out _))
            {
                Console.WriteLine("character_reject reason=token");
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            CreateBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CreateBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var request = new CreateCharacterRequest
            {
                Account = account,
                Name = (body.Name ?? "").Trim(),
                Race = (body.Race ?? "").Trim(),
                Appearance = body.Appearance ?? "",
                ClassId = (body.ClassId ?? "").Trim(),
                Branch = (body.Branch ?? "").Trim(),
            };

            try
            {
                var created = await client.CreateCharacterAsync(
                    request,
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("character_created account=" + account
                    + " id=" + created.CharacterId
                    + " race=" + created.Race
                    + " class=" + created.ClassId
                    + " branch=" + created.Branch
                    + " main_stage_empty=" + created.MainStageEmpty
                    + " alignment_empty=" + created.AlignmentEmpty);

                await WriteAsync(context, 200, new
                {
                    character_id = created.CharacterId,
                    name = created.Name,
                    race = created.Race,
                    appearance = created.Appearance,
                    @class = created.ClassId,
                    branch = created.Branch,
                    level = created.Level,
                    realm = created.Realm,
                    main_stage_empty = created.MainStageEmpty,
                    alignment_empty = created.AlignmentEmpty,
                });
            }
            catch (RpcException ex)
            {
                var code = ex.Status.Detail;
                var http = ex.StatusCode switch
                {
                    StatusCode.InvalidArgument => 400,
                    StatusCode.NotFound => 404,
                    StatusCode.AlreadyExists => 409,
                    StatusCode.Unavailable => 503,
                    _ => 502,
                };

                Console.WriteLine("character_reject account=" + account
                    + " code=" + code + " http=" + http);
                await WriteAsync(context, http, new { error = code });
            }
        });

        // 写等级。上限 100 由角色服判，网关只转；越界是 400，不是静默改成 100。
        app.MapPost("/character/level", async context =>
        {
            var token = TokenFrom(context.Request);
            if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out var account, out _))
            {
                Console.WriteLine("level_reject reason=token");
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            LevelBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<LevelBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body is null || body.CharacterId is null || body.Level is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var updated = await client.SetLevelAsync(
                    new SetLevelRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Level = body.Level.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("level_updated account=" + account
                    + " id=" + updated.CharacterId
                    + " level=" + updated.Level);

                await WriteAsync(context, 200, new
                {
                    character_id = updated.CharacterId,
                    level = updated.Level,
                });
            }
            catch (RpcException ex)
            {
                var code = ex.Status.Detail;
                var http = ex.StatusCode switch
                {
                    StatusCode.InvalidArgument => 400,
                    StatusCode.NotFound => 404,
                    StatusCode.Unavailable => 503,
                    _ => 502,
                };

                Console.WriteLine("level_reject account=" + account
                    + " code=" + code + " http=" + http);
                await WriteAsync(context, http, new { error = code });
            }
        });

        // 往背包里放东西。绑定不绑定由物品表定，请求里说不了。
        app.MapPost("/inventory/add", async context =>
        {
            if (!TryAuthorize(context, tokens, "inventory_add", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            AddItemBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<AddItemBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.Slot is null || body.Count is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var slot = await client.AddItemAsync(
                    new AddItemRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        ItemId = body.ItemId ?? "",
                        Count = body.Count.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("inventory_add account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + slot.Slot
                    + " item=" + slot.ItemId
                    + " count=" + slot.Count
                    + " bound=" + slot.Bound);

                await WriteAsync(context, 200, new
                {
                    slot = slot.Slot,
                    item_id = slot.ItemId,
                    count = slot.Count,
                    bound = slot.Bound,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_add", account, ex);
            }
        });

        // 读整个背包。
        app.MapGet("/inventory", async context =>
        {
            if (!TryAuthorize(context, tokens, "inventory_list", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var list = await client.ListInventoryAsync(
                    new ListInventoryRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    bag_slots = list.BagSlots,
                    slots = list.Slots.Select(s => new
                    {
                        slot = s.Slot,
                        item_id = s.ItemId,
                        count = s.Count,
                        bound = s.Bound,
                    }),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_list", account, ex);
            }
        });

        // 这一格能不能直接交易。绑定物品这里给 false + bound_item。
        app.MapPost("/inventory/can-trade", async context =>
        {
            if (!TryAuthorize(context, tokens, "inventory_trade", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            CanTradeBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CanTradeBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.Slot is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var can = await client.CanTradeAsync(
                    new CanTradeRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("inventory_trade account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " tradable=" + can.Tradable
                    + " reason=" + can.Reason);

                await WriteAsync(context, 200, new
                {
                    tradable = can.Tradable,
                    reason = can.Reason,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_trade", account, ex);
            }
        });
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

    static async Task WriteRpcErrorAsync(HttpContext context, string what, string account, RpcException ex)
    {
        var code = ex.Status.Detail;
        var http = ex.StatusCode switch
        {
            StatusCode.InvalidArgument => 400,
            StatusCode.NotFound => 404,
            StatusCode.AlreadyExists => 409,
            StatusCode.Unavailable => 503,
            _ => 502,
        };

        Console.WriteLine(what + "_reject account=" + account + " code=" + code + " http=" + http);
        await WriteAsync(context, http, new { error = code });
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
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return header[prefix.Length..].Trim();
        }

        return null;
    }

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
