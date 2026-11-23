using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：B9 穿戴 / 脱下。</summary>
public static partial class CharacterApi
{
    static void RegisterEquip(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // B9：穿上一件装备。请求只给背包格号——部位、等级门槛与职业锁都在角色服判；
        // 业务拒绝一律 200 + ok=false（slot_empty / slot_occupied / level_not_enough /
        // class_mismatch / not_equipment / already_equipped），越界格号由角色服按参数错回 4xx。
        app.MapPost("/character/equip", async context =>
        {
            if (!TryAuthorize(context, tokens, "character_equip", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            EquipItemBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<EquipItemBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.Slot is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var equip = await client.EquipItemAsync(
                    new EquipItemRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("character_equip account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " item=" + equip.ItemId
                    + " worn=" + equip.EquipSlot
                    + " attack=" + equip.EquipAttack
                    + " ok=" + equip.Ok
                    + " reason=" + equip.Reason);

                await WriteAsync(context, 200, new
                {
                    ok = equip.Ok,
                    reason = equip.Reason,
                    item_id = equip.ItemId,
                    equip_slot = equip.EquipSlot,
                    equip_attack = equip.EquipAttack,
                    level = equip.Level,
                    class_id = equip.ClassId,
                    bag_slot = equip.BagSlot,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "character_equip", account, ex);
            }
        });

        // B9：按部位脱下。脱下不换格，bag_slot 就是它原来那一格。
        app.MapPost("/character/unequip", async context =>
        {
            if (!TryAuthorize(context, tokens, "character_unequip", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            UnequipItemBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<UnequipItemBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || string.IsNullOrWhiteSpace(body.EquipSlot))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var unequip = await client.UnequipItemAsync(
                    new UnequipItemRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        EquipSlot = body.EquipSlot,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("character_unequip account=" + account
                    + " id=" + body.CharacterId.Value
                    + " worn=" + unequip.EquipSlot
                    + " item=" + unequip.ItemId
                    + " bag_slot=" + unequip.BagSlot
                    + " attack=" + unequip.EquipAttack
                    + " ok=" + unequip.Ok
                    + " reason=" + unequip.Reason);

                await WriteAsync(context, 200, new
                {
                    ok = unequip.Ok,
                    reason = unequip.Reason,
                    item_id = unequip.ItemId,
                    equip_slot = unequip.EquipSlot,
                    bag_slot = unequip.BagSlot,
                    equip_attack = unequip.EquipAttack,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "character_unequip", account, ex);
            }
        });
    }
}
