using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace World;

public sealed record RealmResult(bool Ok, string Reason, string Realm);

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
                ? new RealmResult(false, "realm_unavailable", "")
                : new RealmResult(true, "", realm);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return new RealmResult(false, "character_not_found", "");
        }
        catch (RpcException ex)
        {
            return new RealmResult(false, "realm_unavailable:" + ex.Status.Detail, "");
        }
    }

    public void Dispose() => channel.Dispose();
}
