using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>SectApi HTTP：任命 / 免职：注册调用与共用出口。</summary>
public static partial class SectApi
{
    static void RegisterPosition(WebApplication app, TokenStore tokens, SocialService.SocialServiceClient social, CharacterService.CharacterServiceClient character)
    {
        // P2-1a2：任命 / 免职。认令牌、复核归属、服务端生成 txn_id。
        MapPositionRoute(app, tokens, social, character, "/sect/appoint", "sect_appoint", true);
        MapPositionRoute(app, tokens, social, character, "/sect/dismiss", "sect_dismiss", false);
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
            var account = GatewayAuth.AccountOf(context);

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
                if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
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
    }
}
