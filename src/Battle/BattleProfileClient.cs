using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Battle;

/// <summary>
/// 战场要按「装备生效」算属性，而装备只在角色库里。这里只取事实值：
/// 职业、分支、等级、各槽强化等级之和。折算与战力全在战斗服按
/// config/battlefield.json 算，角色服不参与数值。
/// </summary>
public sealed record BattleProfile(
    string ClassId, string Branch, int Level, int EnhanceTotal, int EnhanceSlots);

public interface IBattleProfileSource : IDisposable
{
    /// <summary>查不到（角色不存在）或上游不可用时返回 null，调用方据此拒绝进场。</summary>
    BattleProfile? Get(ulong characterId);
}

/// <summary>没配 character_upstream：战场一律拒，不从别处编一份属性出来。</summary>
public sealed class NoBattleProfileSource : IBattleProfileSource
{
    public BattleProfile? Get(ulong characterId) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// 服间调用实现。上游不可达只记一行日志并返回 null——战场进不去，
/// 但不影响副本、练习场与竞技场。
/// </summary>
public sealed class GrpcBattleProfileSource : IBattleProfileSource
{
    readonly GrpcChannel _channel;
    readonly CharacterService.CharacterServiceClient _client;

    public GrpcBattleProfileSource(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new CharacterService.CharacterServiceClient(_channel);
    }

    public BattleProfile? Get(ulong characterId)
    {
        try
        {
            var reply = _client.DescribeBattleProfile(
                new DescribeBattleProfileRequest { CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5));
            return reply.Found
                ? new BattleProfile(
                    reply.ClassId, reply.Branch, reply.Level, reply.EnhanceTotal, reply.EnhanceSlots)
                : null;
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"battle_profile_error=character={characterId} status={ex.StatusCode}");
            return null;
        }
    }

    public void Dispose() => _channel.Dispose();
}
