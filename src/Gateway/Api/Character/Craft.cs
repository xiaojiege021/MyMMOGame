using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：K3 配方目录 / 状态 / 开始 / 收取。</summary>
public static partial class CharacterApi
{
    static void RegisterCraft(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // K3：配方目录。四个副职与每条配方的材料 / 产出 / 费用 / 成功率 / 耗时 /
        // 解锁等级都在角色服的 config/recipes.json；网关只认令牌、复核归属，不在本地算。
        app.MapGet("/craft/recipes", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await GatewayAuth.OwnsAsync(client, account, characterId, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var result = await client.DescribeRecipesAsync(
                    new DescribeRecipesRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    table_version = result.TableVersion,
                    pending = result.Pending,
                    level = result.Level,
                    professions = result.Professions.Select(p => new
                    {
                        id = p.Id,
                        name = p.Name,
                    }),
                    recipes = result.Recipes.Select(r => new
                    {
                        id = r.Id,
                        profession = r.Profession,
                        unlock_level = r.UnlockLevel,
                        unlocked = r.Unlocked,
                        inputs = r.Inputs.Select(i => new { item_id = i.ItemId, count = i.Count }),
                        fee = r.Fee.Select(f => new { kind = f.Kind, amount = f.Amount }),
                        output = new
                        {
                            item_id = r.Output is null ? "" : r.Output.ItemId,
                            count = r.Output is null ? 0 : r.Output.Count,
                        },
                        success_pct = r.SuccessPct,
                        craft_time_s = r.CraftTimeS,
                        note = r.Note,
                    }),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "craft_recipes", account, ex);
            }
        });

        // K3：当前有没有活、还差几秒。只读，客户端不能靠它掷点或改结果。
        app.MapGet("/craft/state", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await GatewayAuth.OwnsAsync(client, account, characterId, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var result = await client.DescribeCraftAsync(
                    new DescribeCraftRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    active = result.Active,
                    recipe_id = result.RecipeId,
                    profession = result.Profession,
                    craft_time_s = result.CraftTimeS,
                    started_at_unix = result.StartedAtUnix,
                    ready_at_unix = result.ReadyAtUnix,
                    remaining_s = result.RemainingS,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "craft_state", account, ex);
            }
        });

        // K3：开始制作。材料 / 费用在角色服那一笔事务里扣；业务拒绝是 200 + ok=false，
        // 客户端不能把一个拒绝当成「已经开始」。
        app.MapPost("/craft/start", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            CraftStartBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CraftStartBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.RecipeId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await GatewayAuth.OwnsAsync(client, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                // 一次点击一个号：客户端带不进来，也不能靠重放扣两次材料。
                var txnId = "craft_start:" + Guid.NewGuid().ToString("N");
                var result = await client.StartCraftAsync(
                    new StartCraftRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        RecipeId = body.RecipeId!.Trim(),
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("craft_start account=" + account
                    + " id=" + body.CharacterId.Value
                    + " recipe=" + body.RecipeId!.Trim()
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " duplicate=" + result.Duplicate
                    + " remaining_s=" + result.RemainingS);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    duplicate = result.Duplicate,
                    recipe_id = result.RecipeId,
                    profession = result.Profession,
                    craft_time_s = result.CraftTimeS,
                    started_at_unix = result.StartedAtUnix,
                    ready_at_unix = result.ReadyAtUnix,
                    remaining_s = result.RemainingS,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "craft_start", account, ex);
            }
        });

        // K3：收取。未到 ready_at 回 craft_not_ready；真的做失败是 ok=true + success=false
        // （材料照扣、产出为 0），与「被拒绝、一个字节都没动」分开。
        app.MapPost("/craft/finish", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            CraftFinishBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CraftFinishBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await GatewayAuth.OwnsAsync(client, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var txnId = "craft_finish:" + Guid.NewGuid().ToString("N");
                var result = await client.FinishCraftAsync(
                    new FinishCraftRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("craft_finish account=" + account
                    + " id=" + body.CharacterId.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " success=" + result.Success
                    + " output=" + result.OutputItemId + "x" + result.OutputCount
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    duplicate = result.Duplicate,
                    recipe_id = result.RecipeId,
                    profession = result.Profession,
                    success = result.Success,
                    output_item_id = result.OutputItemId,
                    output_count = result.OutputCount,
                    success_pct = result.SuccessPct,
                    craft_time_s = result.CraftTimeS,
                    ready_at_unix = result.ReadyAtUnix,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "craft_finish", account, ex);
            }
        });
    }
}
