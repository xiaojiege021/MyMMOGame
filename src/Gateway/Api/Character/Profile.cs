using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：Q12/K3 改名。</summary>
public static partial class CharacterApi
{
    static void RegisterProfile(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // 强化。三档材料、成功率与金币消耗全在角色服的 config/enhance.json；
        // 网关只认令牌、复核归属、生成一个事务号，再把请求转过去。
        // P1-4b 的 use_coupon 只是一个开关：券的编号、数量、来源都不在请求里，
        // 角色服按配置里唯一那一种券从背包扣。业务拒绝（材料不对、到顶、材料或金币
        // 不够、券不够）也是 200 + ok=false。
        // Q12/K3：改名。规则（长度 / 敏感词 / 唯一）与「扣哪张卡」全在角色服；
        // 网关只认令牌、复核归属、生成一个事务号。业务拒绝（重名、没卡、名字没变）
        // 也是 200 + ok=false，一分不扣。
        app.MapPost("/character/rename", async context =>
        {
            if (!TryAuthorize(context, tokens, "rename", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            RenameBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<RenameBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.NewName))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(client, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                // 一次点击一个号：客户端带不进来，也不能靠重放扣两张卡。
                var txnId = "rename:" + Guid.NewGuid().ToString("N");
                var result = await client.RenameCharacterAsync(
                    new RenameCharacterRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        NewName = body.NewName!.Trim(),
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("character_rename account=" + account
                    + " id=" + body.CharacterId.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " name=" + result.Name
                    + " item=" + result.ItemId
                    + " count_left=" + result.CountLeft
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    name = result.Name,
                    item_id = result.ItemId,
                    count_left = result.CountLeft,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "rename", account, ex);
            }
        });
    }
}
