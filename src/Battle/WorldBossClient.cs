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
/// Q11：名额的回包。ok=false 时 reason 直接透传成客户端的 EnterRejected.reason
/// （worldboss_closed / worldboss_fought / worldboss_full / worldboss_line_full /
/// worldboss_not_on_entrance_line / slot_missing / window_changed）。
/// upstream_off / upstream_error 表示世界服没配或不可达：这时按本进程的人数判满，
/// 不因为上游挂了就把人挡在门外，也不假装占到了名额。
/// </summary>
public sealed record WorldBossSlotReply(
    bool Ok, string Reason, bool Duplicate, string WindowKey, long ExpiresAtMs,
    int Current, int Cap, string LineKey, int LineOccupancy, int LineCapacity)
{
    public bool UpstreamDown => Reason is "upstream_off" or "upstream_error";
}

/// <summary>
/// P3-5a：把这一场的事实值交给地表入口。入口在世界服，战斗服不自己关入口、
/// 也不自己记名字。上行没配 / 不可达只记日志——技能结算、经验结算照旧，
/// 一场已经打过的仗不会因为世界服没起来就白打。
/// Q11 起同一个口还负责名额：占位、续约、释放。名额只在世界服，战斗服不自己发。
/// </summary>
public interface IWorldBossSink : IDisposable
{
    WorldBossOutcomeReply Apply(WorldBossOutcomeFact fact);

    WorldBossSlotReply Hold(ulong characterId);

    WorldBossSlotReply Renew(ulong characterId, bool entered);

    WorldBossSlotReply Release(ulong characterId, string reason);
}

public sealed class NoWorldBossSink : IWorldBossSink
{
    public WorldBossOutcomeReply Apply(WorldBossOutcomeFact fact) =>
        new(false, "upstream_off", false, fact.WindowKey, 0, "", false);

    public WorldBossSlotReply Hold(ulong characterId) =>
        new(false, "upstream_off", false, "", 0, 0, 0, "", 0, 0);

    public WorldBossSlotReply Renew(ulong characterId, bool entered) =>
        new(false, "upstream_off", false, "", 0, 0, 0, "", 0, 0);

    public WorldBossSlotReply Release(ulong characterId, string reason) =>
        new(false, "upstream_off", false, "", 0, 0, 0, "", 0, 0);

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

    public WorldBossSlotReply Hold(ulong characterId)
    {
        try
        {
            var reply = _client.HoldWorldBossSlot(
                new HoldWorldBossSlotRequest { CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5));
            return new WorldBossSlotReply(
                reply.Ok, reply.Reason, reply.Duplicate, reply.WindowKey,
                (long)reply.ExpiresAtMs, (int)reply.Current, (int)reply.Cap, reply.LineKey,
                (int)reply.LineOccupancy, (int)reply.LineCapacity);
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"world_boss_slot_error=op=hold character={characterId} status={ex.StatusCode}");
            return Down();
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                   or OperationCanceledException or IOException)
        {
            Console.WriteLine($"world_boss_slot_error=op=hold character={characterId} kind={ex.GetType().Name}");
            return Down();
        }
    }

    public WorldBossSlotReply Renew(ulong characterId, bool entered)
    {
        try
        {
            var reply = _client.RenewWorldBossSlot(
                new RenewWorldBossSlotRequest { CharacterId = characterId, Entered = entered },
                deadline: DateTime.UtcNow.AddSeconds(5));
            return new WorldBossSlotReply(
                reply.Ok, reply.Reason, false, reply.WindowKey, (long)reply.ExpiresAtMs,
                (int)reply.Current, (int)reply.Cap, "", 0, 0);
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"world_boss_slot_error=op=renew character={characterId} status={ex.StatusCode}");
            return Down();
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                   or OperationCanceledException or IOException)
        {
            Console.WriteLine($"world_boss_slot_error=op=renew character={characterId} kind={ex.GetType().Name}");
            return Down();
        }
    }

    public WorldBossSlotReply Release(ulong characterId, string reason)
    {
        try
        {
            var reply = _client.ReleaseWorldBossSlot(
                new ReleaseWorldBossSlotRequest { CharacterId = characterId, Reason = reason },
                deadline: DateTime.UtcNow.AddSeconds(5));
            return new WorldBossSlotReply(
                reply.Ok, reply.Reason, false, "", 0, (int)reply.Current, 0, "", 0, 0);
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"world_boss_slot_error=op=release character={characterId} status={ex.StatusCode}");
            return Down();
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                   or OperationCanceledException or IOException)
        {
            Console.WriteLine($"world_boss_slot_error=op=release character={characterId} kind={ex.GetType().Name}");
            return Down();
        }
    }

    static WorldBossSlotReply Down() =>
        new(false, "upstream_error", false, "", 0, 0, 0, "", 0, 0);

    public void Dispose() => _channel.Dispose();
}