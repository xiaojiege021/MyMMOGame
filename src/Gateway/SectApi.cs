using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>
/// 宗门的 HTTP 入口（P2-1a1）：建 1 级宗门 /post /sect/create、收人 /post /sect/join。
/// 网关只做三件事：认令牌、复核角色归属、服务端生成事务号后转给社交服。
/// 人数上限、等级、人数都在社交服按 config/sect.json 判定，网关一个都不算，
/// 也不碰社交库或角色库。业务前提不满足是 HTTP 200 + ok=false + reason；
/// 身份与参数错才走 401 / 400 / 404。
/// </summary>
public static class SectApi
{
    public sealed record CreateBody(ulong? CharacterId, string? Name);

    public sealed record JoinBody(ulong? CharacterId, ulong? SectId);

    // P2-1a2 请求体只带这四个字段，席位数字带不进来。
    public sealed record PositionBody(
        ulong? CharacterId, ulong? SectId, ulong? TargetCharacterId, string? Position);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(characterChannel);
        var socialChannel = GrpcChannel.ForAddress(config.SocialUpstream);
        var social = new SocialService.SocialServiceClient(socialChannel);

        app.MapPost("/sect/create", async context =>
        {
            if (!Authorize(context, tokens, "sect_create", out var account))
            {
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

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.Name))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(character, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                // 事务号服务端生成：客户端带不进来，角色服 grant_txn 用它挡重放。
                var txnId = "sect_create:" + Guid.NewGuid().ToString("N");
                var result = await social.CreateSectAsync(
                    new CreateSectRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                        Name = body.Name!.Trim(),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("sect_create account=" + account
                    + " id=" + body.CharacterId.Value
                    + " name=" + body.Name!.Trim()
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " sect=" + result.SectId
                    + " level=" + result.Level
                    + " cap=" + result.MemberCap);

                await WriteAsync(context, 200, SectBody(result));
            }
            catch (RpcException ex)
            {
                Console.WriteLine("sect_create_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        app.MapPost("/sect/join", async context =>
        {
            if (!Authorize(context, tokens, "sect_join", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            JoinBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JoinBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.SectId is null || body.SectId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(character, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var txnId = "sect_join:" + Guid.NewGuid().ToString("N");
                var result = await social.JoinSectAsync(
                    new JoinSectRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                        SectId = body.SectId.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("sect_join account=" + account
                    + " id=" + body.CharacterId.Value
                    + " sect=" + body.SectId.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " level=" + result.Level
                    + " cap=" + result.MemberCap
                    + " count=" + result.MemberCount);

                await WriteAsync(context, 200, SectBody(result));
            }
            catch (RpcException ex)
            {
                Console.WriteLine("sect_join_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        // P2-1a2：任命 / 免职。认令牌、复核归属、服务端生成 txn_id。
        MapPositionRoute(app, tokens, social, character, "/sect/appoint", "sect_appoint", true);
        MapPositionRoute(app, tokens, social, character, "/sect/dismiss", "sect_dismiss", false);
    }

    static object SectBody(SectResponse r) => new
    {
        ok = r.Ok,
        reason = r.Reason,
        sect_id = r.SectId,
        level = r.Level,
        member_cap = r.MemberCap,
        member_count = r.MemberCount,
        name = r.Name,
        txn_id = r.TxnId,
    };

    static object PositionSectBody(PositionStateResponse r) => new
    {
        ok = r.Ok,
        reason = r.Reason,
        sect_id = r.SectId,
        position = r.Position,
        seat_cap = r.SeatCap,
        seat_count = r.SeatCount,
        txn_id = r.TxnId,
    };

    // 任命 / 免职共用一条出口：认令牌、复核归属、服务端生成 txn_id、转社交服。
    // 席位数字、等级、战绩都在社交服按 config/sect.json 判定，网关一个都不算。
    static void MapPositionRoute(
        WebApplication app, TokenStore tokens,
        SocialService.SocialServiceClient social,
        CharacterService.CharacterServiceClient character,
        string path, string what, bool appoint)
    {
        app.MapPost(path, async context =>
        {
            if (!Authorize(context, tokens, what, out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            PositionBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<PositionBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.SectId is null || body.SectId == 0
                || body.TargetCharacterId is null || body.TargetCharacterId == 0
                || string.IsNullOrWhiteSpace(body.Position))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(character, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var txnId = what + ":" + Guid.NewGuid().ToString("N");
                var req = new PositionRequest
                {
                    Account = account,
                    CharacterId = body.CharacterId.Value,
                    TxnId = txnId,
                    SectId = body.SectId.Value,
                    TargetCharacterId = body.TargetCharacterId.Value,
                    Position = body.Position!.Trim(),
                };
                PositionStateResponse result;
                if (appoint)
                {
                    result = await social.AppointPositionAsync(req,
                        deadline: DateTime.UtcNow.AddSeconds(10),
                        cancellationToken: context.RequestAborted);
                }
                else
                {
                    result = await social.DismissPositionAsync(req,
                        deadline: DateTime.UtcNow.AddSeconds(10),
                        cancellationToken: context.RequestAborted);
                }

                Console.WriteLine(what + " account=" + account
                    + " id=" + body.CharacterId.Value
                    + " sect=" + body.SectId.Value
                    + " target=" + body.TargetCharacterId.Value
                    + " position=" + body.Position!.Trim()
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " seat_cap=" + result.SeatCap
                    + " seat_count=" + result.SeatCount);

                await WriteAsync(context, 200, PositionSectBody(result));
            }
            catch (RpcException ex)
            {
                Console.WriteLine(what + "_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });
    }

    // 复用角色服已有的只读背包口复核归属，不为宗门另起一套身份数据。
    static async Task<bool> OwnsAsync(
        CharacterService.CharacterServiceClient client,
        string account, ulong characterId, HttpContext context)
    {
        try
        {
            await client.ListInventoryAsync(
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

    static bool Authorize(HttpContext context, TokenStore tokens, string what, out string account)
    {
        var token = context.Request.Query["token"].ToString();
        if (string.IsNullOrEmpty(token))
        {
            var header = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                token = header[prefix.Length..].Trim();
            }
        }

        if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out account, out _))
        {
            Console.WriteLine(what + "_reject reason=token");
            return false;
        }

        return true;
    }

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
