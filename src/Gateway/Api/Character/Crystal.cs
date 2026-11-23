using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：结晶装上 / 拆下。</summary>
public static partial class CharacterApi
{
    static void RegisterCrystal(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // 能量结晶装上（方案第 9 章第 3 层）。类别、部位与判定全在角色服；
        // 网关只认令牌、复核归属、生成事务号，再把三个格位编号转过去。
        // 部位不对、位号越界、位已占用等业务拒绝也是 200 + ok=false。
        app.MapPost("/character/crystal/install", async context =>
        {
            if (!TryAuthorize(context, tokens, "crystal_install", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            InstallCrystalBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<InstallCrystalBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.Slot is null || body.Slot == 0
                || body.CrystalSlot is null || body.CrystalSlot == 0
                || body.Socket is null || body.Socket == 0)
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

                var txnId = "crystal_install:" + Guid.NewGuid().ToString("N");
                var result = await client.InstallCrystalAsync(
                    new InstallCrystalRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        CrystalSlot = body.CrystalSlot.Value,
                        Socket = body.Socket.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("crystal_install account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " crystal_slot=" + body.CrystalSlot.Value
                    + " socket=" + body.Socket.Value
                    + " txn=" + txnId
                    + " ok=" + (result.Ok ? "true" : "false")
                    + " reason=" + result.Reason
                    + " item=" + result.CrystalItem
                    + " part=" + result.Part
                    + " duplicate=" + (result.Duplicate ? "true" : "false"));

                await WriteCrystalAsync(context, result);
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "crystal_install", account, ex);
            }
        });

        // 拆下：结晶回背包复用，不扣金币、不消耗、不改强化与绑定。
        app.MapPost("/character/crystal/remove", async context =>
        {
            if (!TryAuthorize(context, tokens, "crystal_remove", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            RemoveCrystalBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<RemoveCrystalBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.Slot is null || body.Slot == 0
                || body.Socket is null || body.Socket == 0)
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

                var txnId = "crystal_remove:" + Guid.NewGuid().ToString("N");
                var result = await client.RemoveCrystalAsync(
                    new RemoveCrystalRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        Socket = body.Socket.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("crystal_remove account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " socket=" + body.Socket.Value
                    + " txn=" + txnId
                    + " ok=" + (result.Ok ? "true" : "false")
                    + " reason=" + result.Reason
                    + " item=" + result.CrystalItem
                    + " left=" + result.CrystalLeft
                    + " duplicate=" + (result.Duplicate ? "true" : "false"));

                await WriteCrystalAsync(context, result);
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "crystal_remove", account, ex);
            }
        });
    }
}
