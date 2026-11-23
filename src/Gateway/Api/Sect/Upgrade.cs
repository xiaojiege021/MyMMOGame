using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>SectApi HTTP：升级只耗建设。</summary>
public static partial class SectApi
{
    static void RegisterUpgrade(WebApplication app, TokenStore tokens, SocialService.SocialServiceClient social, CharacterService.CharacterServiceClient character)
    {
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
}
