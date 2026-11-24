using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：Q10 移动 / 丢弃。</summary>
public static partial class CharacterApi
{
    static void RegisterItemActions(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // Q10：移动一格。空格直接挪、同物品合并、不同物品交换；强化挂在背包行上，
        // 只改格号，所以不丢也不串。业务拒绝一律 HTTP 200 + ok=false。
        app.MapPost("/inventory/move", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            MoveItemBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<MoveItemBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.FromSlot is null || body.ToSlot is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var move = await client.MoveItemAsync(
                    new MoveItemRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        FromSlot = body.FromSlot.Value,
                        ToSlot = body.ToSlot.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("bag_move account=" + account
                    + " id=" + body.CharacterId.Value
                    + " from=" + body.FromSlot.Value
                    + " to=" + body.ToSlot.Value
                    + " ok=" + move.Ok
                    + " reason=" + move.Reason
                    + " merged=" + move.Merged
                    + " swapped=" + move.Swapped);

                await WriteAsync(context, 200, new
                {
                    ok = move.Ok,
                    reason = move.Reason,
                    source = new
                    {
                        slot = move.Source.Slot,
                        item_id = move.Source.ItemId,
                        count = move.Source.Count,
                        bound = move.Source.Bound,
                    },
                    target = new
                    {
                        slot = move.Target.Slot,
                        item_id = move.Target.ItemId,
                        count = move.Target.Count,
                        bound = move.Target.Bound,
                    },
                    merged = move.Merged,
                    swapped = move.Swapped,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_move", account, ex);
            }
        });

        // Q10：丢掉若干件。confirm 不带或 false 一律由角色服拒（not_confirmed），
        // 网关不替玩家确认。事务号可以客户端带，没带就现生成。
        app.MapPost("/inventory/discard", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            DiscardItemBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<DiscardItemBody>(
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

            var txnId = (body.TxnId ?? "").Trim();
            if (txnId.Length == 0)
            {
                txnId = "bag_discard:" + Guid.NewGuid().ToString("N");
            }

            try
            {
                var discard = await client.DiscardItemAsync(
                    new DiscardItemRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        Count = body.Count.Value,
                        Confirm = body.Confirm ?? false,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("bag_discard account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " count=" + body.Count.Value
                    + " ok=" + discard.Ok
                    + " reason=" + discard.Reason
                    + " item=" + discard.ItemId
                    + " left=" + discard.CountLeft
                    + " duplicate=" + discard.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = discard.Ok,
                    reason = discard.Reason,
                    item_id = discard.ItemId,
                    count_left = discard.CountLeft,
                    duplicate = discard.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_discard", account, ex);
            }
        });
    }
}
