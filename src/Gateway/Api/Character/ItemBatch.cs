using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：Q10 排序 / 批量使用。</summary>
public static partial class CharacterApi
{
    static void RegisterItemBatch(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // Q10：排序。种类 → 品阶 → 物品编号，紧凑到第 1 格；回包是排完之后的整份背包。
        app.MapPost("/inventory/sort", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            SortInventoryBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SortInventoryBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var sort = await client.SortInventoryAsync(
                    new SortInventoryRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("bag_sort account=" + account
                    + " id=" + body.CharacterId.Value
                    + " ok=" + sort.Ok
                    + " reason=" + sort.Reason
                    + " moved=" + sort.Moved
                    + " rows=" + sort.Slots.Count);

                await WriteAsync(context, 200, new
                {
                    ok = sort.Ok,
                    reason = sort.Reason,
                    moved = sort.Moved,
                    slots = sort.Slots.Select(s => new
                    {
                        slot = s.Slot,
                        item_id = s.ItemId,
                        count = s.Count,
                        bound = s.Bound,
                        // 排完序的整份背包：这一行穿在哪由角色服给，网关只转发，不漏字段。
                        equipped_slot = s.EquippedSlot,
                    }),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_sort", account, ex);
            }
        });

        // Q10：批量使用。一次用叠里的 N 个；效果入口不在本卡（归 B1），
        // 角色服只回「这一格能不能用、用了几个」。
        app.MapPost("/inventory/use-batch", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            UseItemBatchBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<UseItemBatchBody>(
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
                txnId = "bag_use:" + Guid.NewGuid().ToString("N");
            }

            try
            {
                var use = await client.UseItemBatchAsync(
                    new UseItemBatchRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        Count = body.Count.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("bag_use account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " count=" + body.Count.Value
                    + " ok=" + use.Ok
                    + " reason=" + use.Reason
                    + " item=" + use.ItemId
                    + " used=" + use.Used
                    + " left=" + use.CountLeft);

                await WriteAsync(context, 200, new
                {
                    ok = use.Ok,
                    reason = use.Reason,
                    item_id = use.ItemId,
                    used = use.Used,
                    count_left = use.CountLeft,
                    duplicate = use.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_use", account, ex);
            }
        });
    }
}
