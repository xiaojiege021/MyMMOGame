using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>SectApi HTTP：入宗。</summary>
public static partial class SectApi
{
    static void RegisterJoin(WebApplication app, TokenStore tokens, SocialService.SocialServiceClient social, CharacterService.CharacterServiceClient character)
    {
        app.MapPost("/sect/join", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

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
                if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
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
    }
}
