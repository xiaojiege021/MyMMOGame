using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>SectApi HTTP：传位 / 退出：注册调用与共用出口。</summary>
public static partial class SectApi
{
    static void RegisterLeadership(WebApplication app, TokenStore tokens, SocialService.SocialServiceClient social, CharacterService.CharacterServiceClient character)
    {
        // P2-1a3：传位只给副掌门 / 掌门不能直接退出。
        MapLeadershipRoute(app, tokens, social, character, "/sect/transfer", "sect_transfer", true);
        MapLeadershipRoute(app, tokens, social, character, "/sect/leave", "sect_leave", false);
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
    }
}
