using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>
/// C3 洞府摆放的 HTTP 入口（方案 12.1「只做展示和生活」、开发架构 4.4「摆放和访客可见」）。
///
/// 网关只做三件事：认令牌、复核角色归属、服务端生成事务号后转给社交服。
/// 格位表与可摆物品在社交服按 config/homestead.json / config/items.json 判定，
/// 摆件有没有到手由社交服只读问角色服 ListInventory——网关一个都不算，
/// 也不碰社交库或角色库。请求体只认角色号 / 格位 / 物品编号：owner、txn_id、
/// count、home_coin 一类字段连形状都没有，塞进来也进不了下游。
/// 业务前提不满足是 HTTP 200 + ok=false + reason；身份与参数错走 401 / 400 / 404。
/// </summary>
public static class HomesteadApi
{
    // 只这三个字段：摆放不消耗、不扣货币、不发奖，请求里没有数量 / 奖励 / 归属字段。
    public sealed record PlaceBody(ulong? CharacterId, string? SlotId, string? ItemId);

    public sealed record RemoveBody(ulong? CharacterId, string? SlotId);

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

        // 只读：访客按洞府主人角色号看摆放事实。除 token 与 owner 之外不读任何 query，
        // 别人的洞府改不了（摆 / 撤都要复核角色归属）。
        app.MapGet("/homestead", async context =>
        {
            if (!Authorize(context, tokens, "homestead", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            if (!ulong.TryParse(context.Request.Query["owner"].ToString(), out var owner)
                || owner == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var reply = await social.DescribeHomesteadAsync(
                    new DescribeHomesteadRequest
                    {
                        Account = account,
                        OwnerCharacterId = owner,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("homestead account=" + account
                    + " owner=" + owner
                    + " ok=" + reply.Ok
                    + " reason=" + reply.Reason
                    + " slot_count=" + reply.SlotCount
                    + " rows=" + reply.Placements.Count);

                await WriteAsync(context, 200, HomesteadBody(reply));
            }
            catch (RpcException ex)
            {
                Console.WriteLine("homestead_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        app.MapPost("/homestead/place", async context =>
        {
            if (!Authorize(context, tokens, "homestead_place", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            PlaceBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<PlaceBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.SlotId)
                || string.IsNullOrWhiteSpace(body.ItemId))
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

                // 事务号服务端生成：客户端带不进来，社交服 homestead_txn 用它挡重放。
                var txnId = "homestead_place:" + Guid.NewGuid().ToString("N");
                var result = await social.PlaceHomeDecorAsync(
                    new HomesteadRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                        SlotId = body.SlotId!.Trim(),
                        ItemId = body.ItemId!.Trim(),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("homestead_place account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.SlotId!.Trim()
                    + " item=" + body.ItemId!.Trim()
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " duplicate=" + result.Duplicate
                    + " rows=" + result.Placements.Count);

                await WriteAsync(context, 200, HomesteadBody(result));
            }
            catch (RpcException ex)
            {
                Console.WriteLine("homestead_place_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        app.MapPost("/homestead/remove", async context =>
        {
            if (!Authorize(context, tokens, "homestead_remove", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            RemoveBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<RemoveBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.SlotId))
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

                var txnId = "homestead_remove:" + Guid.NewGuid().ToString("N");
                var result = await social.RemoveHomeDecorAsync(
                    new HomesteadRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                        SlotId = body.SlotId!.Trim(),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("homestead_remove account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.SlotId!.Trim()
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " rows=" + result.Placements.Count);

                await WriteAsync(context, 200, HomesteadBody(result));
            }
            catch (RpcException ex)
            {
                Console.WriteLine("homestead_remove_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });
    }

    static object HomesteadBody(HomesteadResponse reply) => new
    {
        ok = reply.Ok,
        reason = reply.Reason,
        character_id = reply.CharacterId,
        slot_id = reply.SlotId,
        item_id = reply.ItemId,
        txn_id = reply.TxnId,
        duplicate = reply.Duplicate,
        slot_count = reply.SlotCount,
        placements = reply.Placements.Select(p => new
        {
            slot_id = p.SlotId,
            item_id = p.ItemId,
        }),
    };

    // 复用角色服已有的只读背包口复核归属，不为洞府另起一套身份数据。
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
