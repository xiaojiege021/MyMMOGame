using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>
/// 队伍的 HTTP 入口（Q7/H1）：邀请 / 接受 / 发起准备 / 确认准备 / 离队 / 解散 / 只读。
/// 网关只做三件事：认令牌、复核角色归属、服务端生成事务号后转给社交服。
/// 人数上限、两个超时、队长继任方式都在社交服按 config/party.json 判定，网关一个都不算，
/// 也不碰任何库。业务前提不满足是 HTTP 200 + ok=false + reason；身份与参数错才走 401 / 400 / 404。
///
/// 队伍状态只在社交服进程内存（技术架构第 3 节），这里不存、不缓存，
/// 断线重连后读到的是服务端此刻的事实。
/// </summary>
public static class PartyApi
{
    public sealed record InviteBody(ulong? CharacterId, ulong? TargetCharacterId);

    public sealed record RespondBody(ulong? CharacterId, ulong? FromCharacterId, bool? Accept);

    public sealed record ReadyBody(ulong? CharacterId, bool? Ready);

    public sealed record MemberBody(ulong? CharacterId);

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

        // 只读：不在队伍时 in_party=false，其余事实值一律 0 / 空。
        app.MapGet("/party", async context =>
        {
            if (!Authorize(context, tokens, "party_describe", out var account))
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
                if (!await OwnsAsync(character, account, characterId, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var reply = await social.DescribePartyAsync(
                    new PartyRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, PartyBody(reply));
            }
            catch (RpcException ex)
            {
                Console.WriteLine("party_describe_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        MapWrite(app, tokens, character, social, "party_invite", "/party/invite",
            body => body is InviteBody i && i.CharacterId is > 0 && i.TargetCharacterId is > 0,
            async (body, account, ctx) =>
            {
                var b = (InviteBody)body;
                return await social.InvitePartyAsync(
                    new PartyInviteRequest
                    {
                        Account = account,
                        CharacterId = b.CharacterId!.Value,
                        TargetCharacterId = b.TargetCharacterId!.Value,
                        TxnId = NewTxn("party_invite"),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: ctx.RequestAborted);
            });

        MapWrite(app, tokens, character, social, "party_respond", "/party/respond",
            body => body is RespondBody r && r.CharacterId is > 0
                && r.FromCharacterId is > 0 && r.Accept is not null,
            async (body, account, ctx) =>
            {
                var b = (RespondBody)body;
                return await social.RespondPartyInviteAsync(
                    new PartyRespondRequest
                    {
                        Account = account,
                        CharacterId = b.CharacterId!.Value,
                        FromCharacterId = b.FromCharacterId!.Value,
                        Accept = b.Accept!.Value,
                        TxnId = NewTxn("party_respond"),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: ctx.RequestAborted);
            });

        MapWrite(app, tokens, character, social, "party_ready_start", "/party/ready/start",
            body => body is MemberBody m && m.CharacterId is > 0,
            async (body, account, ctx) =>
            {
                var b = (MemberBody)body;
                return await social.StartPartyReadyCheckAsync(
                    new PartyRequest
                    {
                        Account = account,
                        CharacterId = b.CharacterId!.Value,
                        TxnId = NewTxn("party_ready_start"),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: ctx.RequestAborted);
            });

        MapWrite(app, tokens, character, social, "party_ready", "/party/ready",
            body => body is ReadyBody r && r.CharacterId is > 0 && r.Ready is not null,
            async (body, account, ctx) =>
            {
                var b = (ReadyBody)body;
                return await social.SetPartyReadyAsync(
                    new PartyReadyRequest
                    {
                        Account = account,
                        CharacterId = b.CharacterId!.Value,
                        Ready = b.Ready!.Value,
                        TxnId = NewTxn("party_ready"),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: ctx.RequestAborted);
            });

        MapWrite(app, tokens, character, social, "party_leave", "/party/leave",
            body => body is MemberBody m && m.CharacterId is > 0,
            async (body, account, ctx) =>
            {
                var b = (MemberBody)body;
                return await social.LeavePartyAsync(
                    new PartyRequest
                    {
                        Account = account,
                        CharacterId = b.CharacterId!.Value,
                        TxnId = NewTxn("party_leave"),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: ctx.RequestAborted);
            });

        MapWrite(app, tokens, character, social, "party_disband", "/party/disband",
            body => body is MemberBody m && m.CharacterId is > 0,
            async (body, account, ctx) =>
            {
                var b = (MemberBody)body;
                return await social.DisbandPartyAsync(
                    new PartyRequest
                    {
                        Account = account,
                        CharacterId = b.CharacterId!.Value,
                        TxnId = NewTxn("party_disband"),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: ctx.RequestAborted);
            });
    }

    /// <summary>
    /// 六条写入口共用的骨架：认令牌 → 解析请求体（类型由 endpoint 决定）→ 复核归属 →
    /// 转发社交服 → 统一回包。参数错 400、不是自己的角色 404、社交服不可达 502。
    /// </summary>
    static void MapWrite(
        WebApplication app, TokenStore tokens,
        CharacterService.CharacterServiceClient character,
        SocialService.SocialServiceClient social,
        string what, string path,
        Func<object?, bool> valid,
        Func<object?, string, HttpContext, Task<PartyResponse>> call)
    {
        app.MapPost(path, async context =>
        {
            if (!Authorize(context, tokens, what, out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            object? body;
            try
            {
                body = what switch
                {
                    "party_invite" => await JsonSerializer.DeserializeAsync<InviteBody>(
                        context.Request.Body, Json, context.RequestAborted),
                    "party_respond" => await JsonSerializer.DeserializeAsync<RespondBody>(
                        context.Request.Body, Json, context.RequestAborted),
                    "party_ready" => await JsonSerializer.DeserializeAsync<ReadyBody>(
                        context.Request.Body, Json, context.RequestAborted),
                    _ => await JsonSerializer.DeserializeAsync<MemberBody>(
                        context.Request.Body, Json, context.RequestAborted),
                };
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body is null || !valid(body))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var characterId = body switch
            {
                InviteBody i => i.CharacterId!.Value,
                RespondBody r => r.CharacterId!.Value,
                ReadyBody y => y.CharacterId!.Value,
                MemberBody m => m.CharacterId!.Value,
                _ => 0UL,
            };

            try
            {
                if (!await OwnsAsync(character, account, characterId, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var reply = await call(body, account, context);
                Console.WriteLine(what + " account=" + account
                    + " id=" + characterId
                    + " txn=" + reply.TxnId
                    + " ok=" + (reply.Ok ? "true" : "false")
                    + " reason=" + reply.Reason
                    + " party=" + reply.PartyId
                    + " size=" + reply.Size
                    + " leader=" + reply.LeaderCharacterId
                    + " all_ready=" + (reply.AllReady ? "true" : "false"));

                await WriteAsync(context, 200, PartyBody(reply));
            }
            catch (RpcException ex)
            {
                Console.WriteLine(what + "_error account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });
    }

    static object PartyBody(PartyResponse reply) => new
    {
        ok = reply.Ok,
        reason = reply.Reason,
        in_party = reply.InParty,
        party_id = reply.PartyId,
        leader_character_id = reply.LeaderCharacterId,
        size = reply.Size,
        max_size = reply.MaxSize,
        all_ready = reply.AllReady,
        ready_deadline_ms = reply.ReadyDeadlineMs,
        // Q8 的分配口径本来就是 PartyResponse 的只读事实（社交服按 config/party.json 给），
        // U14 照抄上屏，所以这里一并带出去。客户端只显示，掷点仍在战斗服。
        loot_mode = reply.LootMode,
        loot_roll = reply.LootRoll,
        roll_min = reply.RollMin,
        roll_max = reply.RollMax,
        members = reply.Members.Select(m => new
        {
            character_id = m.CharacterId,
            is_leader = m.IsLeader,
            ready = m.Ready,
        }),
    };

    // 事务号服务端生成：客户端带不进来。队伍是幂等状态机，这个号只作核对，
    // 不参与挡重放（重发邀请是刷新超时，不是第二笔）。
    static string NewTxn(string what) => what + ":" + Guid.NewGuid().ToString("N");

    // 复用角色服已有的只读背包口复核归属，不为队伍另起一套身份数据。
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
