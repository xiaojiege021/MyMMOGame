using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.World;
using Mmo.Shared.Domain;
using CharacterClient = Mmo.Proto.Character.CharacterService.CharacterServiceClient;
using WorldClient = Mmo.Proto.World.WorldService.WorldServiceClient;

namespace Gateway;

/// <summary>WorldApi HTTP：采矿与采药。</summary>
public static partial class WorldApi
{
    static void RegisterGather(WebApplication app, WorldClient world, CharacterClient characters, TokenStore tokens, CalendarTable calendar)
    {
        // 采矿与采药。账号取自令牌，复核角色归属后才转给世界服；成功与否、节点占用
        // 与产物都由世界服判，产物由角色服落进背包。网关不碰库，也不判采集。
        app.MapPost("/world/gather", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            GatherBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<GatherBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.NodeId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await GatewayAuth.OwnsAsync(characters, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var result = await world.GatherAsync(
                    new GatherRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        NodeId = body.NodeId.Trim(),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("world_gather account=" + account
                    + " id=" + body.CharacterId.Value
                    + " node=" + body.NodeId.Trim()
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " item=" + result.ItemId
                    + " count=" + result.Count
                    + " txn=" + result.TxnId);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    node_id = result.NodeId,
                    kind = result.Kind,
                    item_id = result.ItemId,
                    count = result.Count,
                    bag_slot = result.BagSlot,
                    txn_id = result.TxnId,
                    duplicate = result.Duplicate,
                    respawn_s = result.RespawnS,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "world_gather", account, ex);
            }
        });
    }
}
