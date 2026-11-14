using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.World;

namespace Battle;

/// <summary>战斗服交回世界服入口的事实值：打掉没打掉、伤害最高的宗门是谁。</summary>
public sealed record WorldBossOutcomeFact(
    string TxnId, string EntranceId, string WindowKey, bool Killed,
    ulong WinnerSectId, string WinnerSectName, long DamageTotal);

/// <summary>世界服记完回给战斗服的只读事实值。</summary>
public sealed record WorldBossOutcomeReply(
    bool Ok, string Reason, bool Duplicate, string WindowKey,
    ulong OwnerSectId, string OwnerSectName, bool EntranceClosed);

/// <summary>
/// P3-5a：把这一场的事实值交给地表入口。入口在世界服，战斗服不自己关入口、
/// 也不自己记名字。上行没配 / 不可达只记日志——技能结算、经验结算照旧，
/// 一场已经打过的仗不会因为世界服没起来就白打。
/// </summary>
public interface IWorldBossSink : IDisposable
{
    WorldBossOutcomeReply Apply(WorldBossOutcomeFact fact);
}

public sealed class NoWorldBossSink : IWorldBossSink
{
    public WorldBossOutcomeReply Apply(WorldBossOutcomeFact fact) =>
        new(false, "upstream_off", false, fact.WindowKey, 0, "", false);

    public void Dispose()
    {
    }
}

public sealed class GrpcWorldBossSink : IWorldBossSink
{
    readonly GrpcChannel _channel;
    readonly WorldService.WorldServiceClient _client;

    public GrpcWorldBossSink(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new WorldService.WorldServiceClient(_channel);
    }

    public WorldBossOutcomeReply Apply(WorldBossOutcomeFact fact)
    {
        try
        {
            var reply = _client.RecordWorldBossOwner(
                new RecordWorldBossOwnerRequest
                {
                    TxnId = fact.TxnId,
                    EntranceId = fact.EntranceId,
                    WindowKey = fact.WindowKey,
                    Killed = fact.Killed,
                    WinnerSectId = fact.WinnerSectId,
                    WinnerSectName = fact.WinnerSectName,
                    DamageTotal = fact.DamageTotal,
                },
                deadline: DateTime.UtcNow.AddSeconds(5));

            return new WorldBossOutcomeReply(
                reply.Ok, reply.Reason, reply.Duplicate, reply.WindowKey,
                reply.OwnerSectId, reply.OwnerSectName, reply.EntranceClosed);
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"world_boss_error=txn={fact.TxnId} status={ex.StatusCode}");
            return new WorldBossOutcomeReply(false, "upstream_error", false, fact.WindowKey, 0, "", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                   or OperationCanceledException or IOException)
        {
            Console.WriteLine($"world_boss_error=txn={fact.TxnId} kind={ex.GetType().Name}");
            return new WorldBossOutcomeReply(false, "upstream_error", false, fact.WindowKey, 0, "", false);
        }
    }

    public void Dispose() => _channel.Dispose();
}
