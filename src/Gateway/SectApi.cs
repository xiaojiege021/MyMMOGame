using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>
/// 宗门的 HTTP 入口（P2-1a1 / a2 / a3 / a4）：建宗门 / 收人 / 任命免职 /
/// 传位（P2-1a3 只给副掌门）/ 退出宗门（掌门不能直接退）/
/// 升级（P2-1a4 只耗建设、建设不从商城买）。
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

    // P2-1a3 传位请求体：发起者 / 宗门 / 传位目标（现任副掌门）。
    // 谁是新掌门之外的判定值（掌门权威、席位数字）一概带不进来。
    public sealed record TransferBody(
        ulong? CharacterId, ulong? SectId, ulong? TargetCharacterId);

    // P2-1a3 退出请求体：不带 target（谁是新掌门、席位数字都带不进来）。
    public sealed record LeaveBody(ulong? CharacterId, ulong? SectId);

    // P2-1a4 升级请求体：只带发起者 / 宗门。目标等级、建设数、消耗一概带不进来，
    // 升到几级、扣多少建设只由社交服按 config/sect.json 判定。
    public sealed record UpgradeBody(ulong? CharacterId, ulong? SectId);

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
        // P2-1a3：传位只给副掌门 / 掌门不能直接退出。
        MapLeadershipRoute(app, tokens, social, character, "/sect/transfer", "sect_transfer", true);
        MapLeadershipRoute(app, tokens, social, character, "/sect/leave", "sect_leave", false);

        // P2-1a4：升级只耗建设。认令牌 + OwnsAsync + 服务端生成 txn_id，
        // 请求体只带 character_id / sect_id，网关一个数值都不算、不碰社交库/角色库。
        app.MapPost("/sect/upgrade", async context =>
        {
            if (!Authorize(context, tokens, "sect_upgrade", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            UpgradeBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<UpgradeBody>(
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

                var txnId = "sect_upgrade:" + Guid.NewGuid().ToString("N");
                var result = await social.UpgradeSectAsync(
                    new UpgradeSectRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                        SectId = body.SectId.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("sect_upgrade account=" + account
                    + " id=" + body.CharacterId.Value
                    + " sect=" + body.SectId.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " old_level=" + result.OldLevel
                    + " new_level=" + result.NewLevel
                    + " cap=" + result.MemberCap
                    + " before=" + result.ConstructionBefore
                    + " after=" + result.ConstructionAfter
                    + " cost=" + result.UpgradeCost);

                await WriteAsync(context, 200, UpgradeSectBody(result));
            }
            catch (RpcException ex)
            {
                Console.WriteLine("sect_upgrade_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });
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

    // P2-1a3 回包全是只读事实值；业务拒绝时社交服把未生效数值给 0。
    static object LeadershipSectBody(LeadershipResponse r) => new
    {
        ok = r.Ok,
        reason = r.Reason,
        sect_id = r.SectId,
        old_master = r.OldMaster,
        new_master = r.NewMaster,
        master_count = r.MasterCount,
        vice_master_count = r.ViceMasterCount,
        member_count = r.MemberCount,
        txn_id = r.TxnId,
    };

    // P2-1a4 回包全是只读事实值；业务拒绝时社交服把未生效数值给 0。
    static object UpgradeSectBody(UpgradeResponse r) => new
    {
        ok = r.Ok,
        reason = r.Reason,
        sect_id = r.SectId,
        old_level = r.OldLevel,
        new_level = r.NewLevel,
        member_cap = r.MemberCap,
        construction_before = r.ConstructionBefore,
        construction_after = r.ConstructionAfter,
        upgrade_cost = r.UpgradeCost,
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

    // P2-1a3：传位 / 退出共用一条出口——认令牌、复核发起者归属、服务端生成 txn_id，
    // 掌门权威与席位判定都在社交服，网关一个都不算。退出请求体不带 target。
    static void MapLeadershipRoute(
        WebApplication app, TokenStore tokens,
        SocialService.SocialServiceClient social,
        CharacterService.CharacterServiceClient character,
        string path, string what, bool transfer)
    {
        app.MapPost(path, async context =>
        {
            if (!Authorize(context, tokens, what, out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            ulong characterId = 0, sectId = 0, targetId = 0;
            try
            {
                if (transfer)
                {
                    var body = await JsonSerializer.DeserializeAsync<TransferBody>(
                        context.Request.Body, Json, context.RequestAborted);
                    characterId = body?.CharacterId ?? 0;
                    sectId = body?.SectId ?? 0;
                    targetId = body?.TargetCharacterId ?? 0;
                }
                else
                {
                    var body = await JsonSerializer.DeserializeAsync<LeaveBody>(
                        context.Request.Body, Json, context.RequestAborted);
                    characterId = body?.CharacterId ?? 0;
                    sectId = body?.SectId ?? 0;
                }
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (characterId == 0 || sectId == 0 || (transfer && targetId == 0))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(character, account, characterId, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                // 事务号服务端生成：客户端带不进来，sect_transfer 用它挡重放。
                var txnId = what + ":" + Guid.NewGuid().ToString("N");
                LeadershipResponse result;
                if (transfer)
                {
                    result = await social.TransferLeadershipAsync(
                        new LeadershipRequest
                        {
                            Account = account,
                            CharacterId = characterId,
                            TxnId = txnId,
                            SectId = sectId,
                            TargetCharacterId = targetId,
                        },
                        deadline: DateTime.UtcNow.AddSeconds(10),
                        cancellationToken: context.RequestAborted);
                }
                else
                {
                    result = await social.LeaveSectAsync(
                        new LeaveSectRequest
                        {
                            Account = account,
                            CharacterId = characterId,
                            TxnId = txnId,
                            SectId = sectId,
                        },
                        deadline: DateTime.UtcNow.AddSeconds(10),
                        cancellationToken: context.RequestAborted);
                }

                Console.WriteLine(what + " account=" + account
                    + " id=" + characterId
                    + " sect=" + sectId
                    + (transfer ? " target=" + targetId : "")
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " old_master=" + result.OldMaster
                    + " new_master=" + result.NewMaster
                    + " master_count=" + result.MasterCount
                    + " vice_master_count=" + result.ViceMasterCount);

                await WriteAsync(context, 200, LeadershipSectBody(result));
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
