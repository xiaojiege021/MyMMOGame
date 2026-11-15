using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Social;

public enum DecorOwnership
{
    Owned,
    NotOwned,
    Unavailable,
}

/// <summary>
/// C3：摆件所有权只读问角色服（技术架构 3 节「角色服是背包唯一写入」）。
/// 社交服不读角色库、不写角色库、不消耗背包：摆放只是登记一份展示事实，
/// 摆件本体仍留在背包里。ListInventory 是 P1-1b3 已有的只读 RPC，本卡不改它、
/// 不改 character.proto，也不给摆件新增一个写入口。
/// </summary>
public sealed class HomesteadInventory : IDisposable
{
    readonly GrpcChannel channel;
    readonly CharacterService.CharacterServiceClient client;

    public HomesteadInventory(string upstream)
    {
        channel = GrpcChannel.ForAddress(upstream);
        client = new CharacterService.CharacterServiceClient(channel);
    }

    public async Task<DecorOwnership> CheckAsync(
        string account, ulong characterId, string itemId, CancellationToken stopping)
    {
        try
        {
            var reply = await client.ListInventoryAsync(
                new ListInventoryRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: stopping);

            // 只认「确实在背包里、绑定态、数量大于 0」的那一件；请求里带不进拥有事实。
            return reply.Slots.Any(s => s.ItemId == itemId && s.Bound && s.Count > 0)
                ? DecorOwnership.Owned
                : DecorOwnership.NotOwned;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return DecorOwnership.NotOwned;
        }
        catch (RpcException)
        {
            // 角色服没起来 / 超时：这次摆放不成立，不回假成功、不写社交库。
            return DecorOwnership.Unavailable;
        }
    }

    public void Dispose() => channel.Dispose();
}
