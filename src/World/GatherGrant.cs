using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace World;

public sealed record GatherGrantResult(bool Ok, string Reason, bool Duplicate);

/// <summary>
/// 采集成功后的那一步：把产物交给角色服。世界服不连角色库，也不自己往背包表里写一行。
/// 角色服的 ApplyGrant 用事务号做幂等，整笔在一个事务里，同一个号不会发第二次。
/// </summary>
public interface IGatherGrant
{
    Task<GatherGrantResult> GrantAsync(
        string account, ulong characterId, string txnId, GatherNode node, CancellationToken stopping);
}

public sealed class GrpcGatherGrant : IGatherGrant, IDisposable
{
    readonly GrpcChannel channel;
    readonly CharacterService.CharacterServiceClient client;

    public GrpcGatherGrant(string upstream)
    {
        channel = GrpcChannel.ForAddress(upstream);
        client = new CharacterService.CharacterServiceClient(channel);
    }

    public async Task<GatherGrantResult> GrantAsync(
        string account, ulong characterId, string txnId, GatherNode node, CancellationToken stopping)
    {
        // 只放配置里写定的那一种产物。客户端请求里带不进 item_id / count / bag_slot。
        var request = new ApplyGrantRequest
        {
            Account = account,
            CharacterId = characterId,
            TxnId = txnId,
        };
        request.Entries.Add(new GrantEntry
        {
            Item = new ItemDelta
            {
                Slot = (uint)node.BagSlot,
                ItemId = node.ItemId,
                Count = node.Count,
            },
        });

        try
        {
            var reply = await client.ApplyGrantAsync(
                request,
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: stopping);
            return new GatherGrantResult(true, "", reply.Duplicate);
        }
        catch (RpcException ex)
        {
            // 角色服拒绝或没起来：这次采集不成立，不回一个假的成功给客户端。
            return new GatherGrantResult(false, "grant_failed:" + ex.Status.Detail, false);
        }
    }

    public void Dispose() => channel.Dispose();
}
