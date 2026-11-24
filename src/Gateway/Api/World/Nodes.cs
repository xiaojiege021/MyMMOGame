using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.World;
using Mmo.Shared.Domain;
using CharacterClient = Mmo.Proto.Character.CharacterService.CharacterServiceClient;
using WorldClient = Mmo.Proto.World.WorldService.WorldServiceClient;

namespace Gateway;

/// <summary>WorldApi HTTP：采集节点与地点只读。</summary>
public static partial class WorldApi
{
    static void RegisterNodes(WebApplication app, WorldClient world, CharacterClient characters, TokenStore tokens, CalendarTable calendar)
    {
        // 只读：运行期采集节点全集（基准 + 铺开），供 U4 采集屏列出节点。
        app.MapGet("/world/gather/nodes", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            try
            {
                var result = await world.ListGatherNodesAsync(
                    new ListGatherNodesRequest(),
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    count = result.Count,
                    nodes = result.Nodes.Select(n => new
                    {
                        node_id = n.NodeId,
                        kind = n.Kind,
                        region_id = n.RegionId,
                        item_id = n.ItemId,
                        count = n.Count,
                        bag_slot = n.BagSlot,
                        respawn_s = n.RespawnS,
                    }).ToArray(),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "world_gather_nodes", account, ex);
            }
        });

        // 只读：地点、开口的人与势力的编号，供客户端与核对比对方案 2.5 到 2.7。
        // N14：可选的 character_id —— 给了就复核归属，再让世界服按这个角色的主线进度判开放；
        // 不给就只读静态表（老屏与老核对走这条，一个字不用改）。
        app.MapGet("/world/regions", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            ulong characterId = 0;
            var raw = context.Request.Query["character_id"].ToString();
            if (raw.Length > 0)
            {
                if (!ulong.TryParse(raw, out characterId) || characterId == 0)
                {
                    await WriteAsync(context, 400, new { error = "bad_request" });
                    return;
                }

                if (!await GatewayAuth.OwnsAsync(characters, account, characterId, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }
            }

            try
            {
                var result = await world.ListRegionsAsync(
                    new ListRegionsRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    version = result.Version,
                    character_id = characterId,
                    regions = result.Regions.Select(r => new
                    {
                        id = r.Id,
                        name = r.Name,
                        chapters = r.Chapters.ToArray(),
                        neighbors = r.Neighbors.ToArray(),
                        dialogue_ids = r.DialogueIds.ToArray(),
                        open = r.Open,
                        reason = r.Reason,
                        unlock_stage = r.UnlockStage,
                        main_stage = r.MainStage,
                    }).ToArray(),
                    speakers = result.Speakers.ToArray(),
                    factions = result.Factions.ToArray(),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "world_regions", account, ex);
            }
        });
    }
}
