using System.Text;
using Grpc.Core;
using Mmo.Proto.World;

namespace World;

public sealed partial class WorldGrpcService
{
    public override async Task<ListRegionsResponse> ListRegions(
        ListRegionsRequest request, ServerCallContext context)
    {
        var response = new ListRegionsResponse { Version = (uint)catalog.Version };

        // N14：带了角色就按 RegionGate 的结论逐块回「这一刻开没开」；
        // 两个字段都空着时只读静态表，老卡与老屏一个字不用改。
        var account = (request.Account ?? "").Trim();
        var withCharacter = account.Length > 0 && request.CharacterId != 0;
        var access = withCharacter
            ? await regions.EvaluateManyAsync(
                account, request.CharacterId, catalog.Regions, context.CancellationToken)
            : null;

        for (var i = 0; i < catalog.Regions.Count; i++)
        {
            var region = catalog.Regions[i];
            var one = access is null
                ? region.Open
                    ? new RegionAccess(true, "", 0, "", 0)
                    : new RegionAccess(false, "region_locked", 0, "", 0)
                : access[i];

            var info = new RegionInfo
            {
                Id = region.Id,
                Name = region.Name,
                Open = one.Ok,
                Reason = one.Reason,
                UnlockStage = one.UnlockStage,
                MainStage = one.MainStage,
            };
            info.Chapters.AddRange(region.Chapters.Select(c => (uint)c));
            info.Neighbors.AddRange(region.Neighbors);
            info.DialogueIds.AddRange(catalog.DialogueIdsFor(region.Id));
            response.Regions.Add(info);
        }

        if (withCharacter)
        {
            var open = 0;
            var locked = new List<string>();
            for (var i = 0; i < catalog.Regions.Count; i++)
            {
                if (access![i].Ok)
                {
                    open++;
                }
                else
                {
                    locked.Add(catalog.Regions[i].Id + ":" + access[i].Reason);
                }
            }

            Console.WriteLine("world_regions account=" + account
                + " character=" + request.CharacterId
                + " regions=" + catalog.Regions.Count
                + " open=" + open
                + " locked=" + (locked.Count == 0 ? "none" : string.Join(",", locked)));
        }

        response.Speakers.AddRange(catalog.Speakers);
        response.Factions.AddRange(catalog.Factions);
        return response;
    }

    public override Task<ListGatherNodesResponse> ListGatherNodes(
        ListGatherNodesRequest request, ServerCallContext context)
    {
        var response = new ListGatherNodesResponse { Count = (uint)gather.Nodes.Count };
        foreach (var node in gather.Nodes)
        {
            response.Nodes.Add(new GatherNodeInfo
            {
                NodeId = node.Id,
                Kind = node.Kind,
                RegionId = node.RegionId,
                ItemId = node.ItemId,
                Count = node.Count,
                BagSlot = (uint)node.BagSlot,
                RespawnS = node.RespawnSeconds,
            });
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// 采矿与采药。方案 9.5：矿脉给矿石、药草给药材，采集不耗疲劳；
    /// 灵石矿脉归主城辖区，不开放给玩家个人去挖，对它的请求一律拒绝。
    /// 成功与否、节点占用与刷新都在这里判，产物交给角色服的 ApplyGrant 写背包。
    /// 请求里只有节点编号，产物、数量、背包格、事务号与身份都带不进来。
    /// </summary>
    public override async Task<GatherResponse> Gather(
        GatherRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var nodeId = (request.NodeId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0 || nodeId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var node = gather.Find(nodeId);
        if (node is null)
        {
            return Reject(account, request.CharacterId, nodeId, "unknown_node");
        }

        // 灵石矿脉先判：不管人在哪、节点是否被占，个人采集都不成立，
        // 也不会走到角色服那笔发奖。灵石仍走主城任务，不进采集背包。
        if (string.Equals(node.Kind, GatherTable.SpiritStone, StringComparison.Ordinal))
        {
            return Reject(account, request.CharacterId, nodeId, "spirit_stone_forbidden");
        }

        var region = registry.RegionOf(account, request.CharacterId);
        if (region is null)
        {
            return Reject(account, request.CharacterId, nodeId, "not_in_line");
        }

        if (!string.Equals(region, node.RegionId, StringComparison.Ordinal))
        {
            return Reject(account, request.CharacterId, nodeId, "wrong_region");
        }

        if (!nodes.TryClaim(nodeId, node.RespawnSeconds, DateTimeOffset.UtcNow))
        {
            return Reject(account, request.CharacterId, nodeId, "node_busy");
        }

        // 每笔一个号，角色服的 grant_txn 主键挡住重放；号只由世界服生成。
        var txnId = "gather:" + Guid.NewGuid().ToString("N");
        var result = await grants.GrantAsync(
            account, request.CharacterId, txnId, node, context.CancellationToken);
        if (!result.Ok)
        {
            // 角色服没写成：把占用还回去，不回一个假的成功。
            nodes.Release(nodeId);
            return Reject(account, request.CharacterId, nodeId, result.Reason);
        }

        Console.WriteLine("gather account=" + account
            + " character=" + request.CharacterId
            + " node=" + nodeId
            + " kind=" + node.Kind
            + " item=" + node.ItemId
            + " count=" + node.Count
            + " slot=" + node.BagSlot
            + " txn=" + txnId
            + " duplicate=" + result.Duplicate);

        return new GatherResponse
        {
            Ok = true,
            NodeId = nodeId,
            Kind = node.Kind,
            ItemId = node.ItemId,
            Count = node.Count,
            BagSlot = (uint)node.BagSlot,
            TxnId = txnId,
            Duplicate = result.Duplicate,
            RespawnS = node.RespawnSeconds,
        };
    }

    static GatherResponse Reject(string account, ulong characterId, string nodeId, string reason)
    {
        Console.WriteLine("gather_reject account=" + account
            + " character=" + characterId
            + " node=" + nodeId
            + " reason=" + reason);
        // 被拒时未生效的产物字段一律是 0 / 空，不给客户端留一份「看起来发了」的回包。
        return new GatherResponse { Ok = false, Reason = reason, NodeId = nodeId };
    }
}
