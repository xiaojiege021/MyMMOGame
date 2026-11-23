using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：强化与转移。</summary>
public static partial class CharacterApi
{
    static void RegisterEnhance(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        app.MapPost("/character/enhance", async context =>
        {
            if (!TryAuthorize(context, tokens, "enhance", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            EnhanceBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<EnhanceBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.Slot is null || body.Slot == 0
                || string.IsNullOrWhiteSpace(body.StoneId))
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

                // 一次点击一个号：客户端带不进来，也不能靠重放扣两次材料。
                var txnId = "enhance:" + Guid.NewGuid().ToString("N");
                var result = await client.EnhanceAsync(
                    new EnhanceRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        StoneId = body.StoneId!.Trim(),
                        TxnId = txnId,
                        UseCoupon = body.UseCoupon ?? false,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("enhance account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " stone=" + body.StoneId!.Trim()
                    + " txn=" + txnId
                    + " coupon=" + (body.UseCoupon ?? false)
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " level=" + result.LevelBefore + "->" + result.Level
                    + " success=" + result.Success
                    + " tier=" + result.Tier
                    + " protected=" + result.ProtectedLevel
                    + " coupon_used=" + result.CouponUsed
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    level_before = result.LevelBefore,
                    level = result.Level,
                    max_level = result.MaxLevel,
                    success = result.Success,
                    tier = result.Tier,
                    required_stone = result.RequiredStone,
                    stone_cost = result.StoneCost,
                    gold_cost = result.GoldCost,
                    gold = result.Gold,
                    stone_left = result.StoneLeft,
                    duplicate = result.Duplicate,
                    success_rate = result.SuccessRate,
                    protected_level = result.ProtectedLevel,
                    coupon_used = result.CouponUsed,
                    coupon_left = result.CouponLeft,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "enhance", account, ex);
            }
        });

        // 强化转移（方案 9.3）。转移石、费用、两格等级与绑定规则全在角色服；
        // 网关只认令牌、复核归属、生成一个事务号，再把两个格子转过去。
        // 业务拒绝（来源不高于目标、格子空、不是装备、石头或金币不够）也是 200 + ok=false。
        app.MapPost("/character/transfer", async context =>
        {
            if (!TryAuthorize(context, tokens, "transfer", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            TransferBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<TransferBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.FromSlot is null || body.FromSlot == 0
                || body.ToSlot is null || body.ToSlot == 0)
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

                // 一次点击一个号：客户端带不进来，也不能靠重放扣两次材料。
                var txnId = "transfer:" + Guid.NewGuid().ToString("N");
                var result = await client.TransferEnhanceAsync(
                    new TransferEnhanceRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        FromSlot = body.FromSlot.Value,
                        ToSlot = body.ToSlot.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("transfer account=" + account
                    + " id=" + body.CharacterId.Value
                    + " from=" + body.FromSlot.Value
                    + " to=" + body.ToSlot.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " level=" + result.FromLevelBefore + ":" + result.ToLevelBefore
                    + "->" + result.FromLevel + ":" + result.ToLevel
                    + " bound=" + result.TargetBoundBefore + "->" + result.TargetBound
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    from_level_before = result.FromLevelBefore,
                    to_level_before = result.ToLevelBefore,
                    from_level = result.FromLevel,
                    to_level = result.ToLevel,
                    max_level = result.MaxLevel,
                    gold_cost = result.GoldCost,
                    gold = result.Gold,
                    stone_item = result.StoneItem,
                    stone_cost = result.StoneCost,
                    stone_left = result.StoneLeft,
                    target_bound_before = result.TargetBoundBefore,
                    target_bound = result.TargetBound,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "transfer", account, ex);
            }
        });
    }
}
