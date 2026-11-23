using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>SectApi HTTP：名册只读与建宗收人。</summary>
public static partial class SectApi
{
    static void RegisterMembership(WebApplication app, TokenStore tokens, SocialService.SocialServiceClient social, CharacterService.CharacterServiceClient character)
    {
        // N2：名册只读。认令牌后只转发 sect_id，职位和人数上限都在社交服。
        app.MapGet("/sect/roster", async context =>
        {
            if (!Authorize(context, tokens, "sect_roster", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            if (!ulong.TryParse(context.Request.Query["sect_id"].ToString(), out var sectId)
                || sectId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var reply = await social.DescribeSectRosterAsync(
                    new DescribeSectRosterRequest
                    {
                        Account = account,
                        SectId = sectId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("sect_roster account=" + account
                    + " sect=" + sectId
                    + " ok=" + reply.Ok
                    + " reason=" + reply.Reason
                    + " rows=" + reply.Members.Count);

                await WriteAsync(context, 200, new
                {
                    ok = reply.Ok,
                    reason = reply.Reason,
                    sect_id = reply.SectId,
                    members = reply.Members.Select(m => new
                    {
                        character_id = m.CharacterId,
                        position = m.Position,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("sect_roster_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

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
    }
}
