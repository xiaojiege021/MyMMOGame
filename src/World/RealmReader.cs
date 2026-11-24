using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace World;

/// <summary>
/// 角色服这一次回的只读事实：境界（判飞行 / 跨区）与坐骑加成（判地表位移上限）。
/// 两者同一次 ListInventory 回来，世界服不另存一份可以过期的结论。
/// </summary>
public sealed record RealmResult(bool Ok, string Reason, string Realm, int MountSpeedBps);

/// <summary>
/// 飞行与跨区判定前，先向角色服问这个人当前的境界。世界服不连角色库，
/// 也不自己存一份境界；问不到、境界不认识或角色服没起来，一律按没有权限处理，
/// 不回一个「看起来有权限」的结果。
/// </summary>
public interface IRealmReader
{
    Task<RealmResult> ReadAsync(string account, ulong characterId, CancellationToken stopping);
}

public sealed class GrpcRealmReader : IRealmReader, IDisposable
{
    readonly GrpcChannel channel;
    readonly CharacterService.CharacterServiceClient client;

    public GrpcRealmReader(string upstream)
    {
        channel = GrpcChannel.ForAddress(upstream);
        client = new CharacterService.CharacterServiceClient(channel);
    }

    public async Task<RealmResult> ReadAsync(
        string account, ulong characterId, CancellationToken stopping)
    {
        // 复用角色服已有的只读回包：它与 OwnsAsync 是同一套归属条件，
        // 别人账号的角色在这里直接是 NotFound，不会泄漏境界。
        try
        {
            var reply = await client.ListInventoryAsync(
                new ListInventoryRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: stopping);
            var realm = (reply.Realm ?? "").Trim();
            return realm.Length == 0
                ? new RealmResult(false, "realm_unavailable", "", 0)
                : new RealmResult(true, "", realm, Math.Max(0, reply.MountSpeedBps));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return new RealmResult(false, "character_not_found", "", 0);
        }
        catch (RpcException ex)
        {
            return new RealmResult(false, "realm_unavailable:" + ex.Status.Detail, "", 0);
        }
    }

    public void Dispose() => channel.Dispose();
}
