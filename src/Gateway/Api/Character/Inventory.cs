using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：背包只读与入包、能否交易。</summary>
public static partial class CharacterApi
{
    static void RegisterInventory(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // 往背包里放东西。绑定不绑定由物品表定，请求里说不了。
        app.MapPost("/inventory/add", async context =>
        {
            if (!TryAuthorize(context, tokens, "inventory_add", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            AddItemBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<AddItemBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.Slot is null || body.Count is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var slot = await client.AddItemAsync(
                    new AddItemRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        ItemId = body.ItemId ?? "",
                        Count = body.Count.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("inventory_add account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + slot.Slot
                    + " item=" + slot.ItemId
                    + " count=" + slot.Count
                    + " bound=" + slot.Bound);

                await WriteAsync(context, 200, new
                {
                    slot = slot.Slot,
                    item_id = slot.ItemId,
                    count = slot.Count,
                    bound = slot.Bound,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_add", account, ex);
            }
        });

        // 读整个背包。
        app.MapGet("/inventory", async context =>
        {
            if (!TryAuthorize(context, tokens, "inventory_list", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var list = await client.ListInventoryAsync(
                    new ListInventoryRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    bag_slots = list.BagSlots,
                    realm = list.Realm,
                    realm_display = list.RealmDisplay,
                    slots = list.Slots.Select(s => new
                    {
                        slot = s.Slot,
                        item_id = s.ItemId,
                        count = s.Count,
                        bound = s.Bound,
                    }),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_list", account, ex);
            }
        });

        // 这一格能不能直接交易。绑定物品这里给 false + bound_item。
        app.MapPost("/inventory/can-trade", async context =>
        {
            if (!TryAuthorize(context, tokens, "inventory_trade", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            CanTradeBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CanTradeBody>(
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
                var can = await client.CanTradeAsync(
                    new CanTradeRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("inventory_trade account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " tradable=" + can.Tradable
                    + " reason=" + can.Reason);

                await WriteAsync(context, 200, new
                {
                    tradable = can.Tradable,
                    reason = can.Reason,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_trade", account, ex);
            }
        });
    }
}
