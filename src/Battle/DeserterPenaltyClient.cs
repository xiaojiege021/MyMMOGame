using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Battle;

/// <summary>
/// Q9/H4：逃跑惩罚的事实值只落角色库（持久数据的唯一写入方），战斗服不直连库。
///
/// 两个动作分得很清：
///   - <see cref="RemainingMs"/>：进场前问一次「还要等多久」。0 表示可以进。
///   - <see cref="Record"/>：判出主动退 / 窗口超时之后写一次。到期时刻由角色服按
///     penalty_ms 现算，战斗服只传时长，不传绝对时刻（两台机时钟不一致也不会算错）。
///
/// 上游不可达时两边都不阻塞：读回 0（按「没被罚」放行），写只打一行日志。
/// 本机口径的兜底在战斗服内存里（BattleServer 的 _penaltyUntilMs），与这里取较大值。
/// </summary>
public interface IDeserterPenalty : IDisposable
{
    /// <summary>还要等多少毫秒才能进本。0 表示没被罚或已过期。</summary>
    long RemainingMs(ulong characterId);

    /// <summary>记一次惩罚，返回写完之后还要等多少毫秒。写不进去回 0。</summary>
    long Record(ulong characterId, ulong sessionId, string reason, long penaltyMs);
}

/// <summary>没配角色服（本机口径）：不读也不写，惩罚只活在战斗服内存里。</summary>
public sealed class NoDeserterPenalty : IDeserterPenalty
{
    public long RemainingMs(ulong characterId) => 0;

    public long Record(ulong characterId, ulong sessionId, string reason, long penaltyMs) => 0;

    public void Dispose()
    {
    }
}

/// <summary>服间调用：读事实值 / 写一次判罚，金币、经验、疲劳一个都不碰。</summary>
public sealed class GrpcDeserterPenalty : IDeserterPenalty
{
    readonly GrpcChannel _channel;
    readonly CharacterService.CharacterServiceClient _client;

    public GrpcDeserterPenalty(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new CharacterService.CharacterServiceClient(_channel);
    }

    public long RemainingMs(ulong characterId)
    {
        try
        {
            var reply = _client.DescribeDeserter(
                new DescribeDeserterRequest { CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(2));
            return reply.Ok ? Math.Max(0, reply.RemainingMs) : 0;
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"deserter_read_error=character={characterId} status={ex.StatusCode}");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                   or OperationCanceledException or IOException)
        {
            Console.WriteLine($"deserter_read_error=character={characterId} kind={ex.GetType().Name}");
            return 0;
        }
    }

    public long Record(ulong characterId, ulong sessionId, string reason, long penaltyMs)
    {
        try
        {
            var reply = _client.RecordDeserter(
                new RecordDeserterRequest
                {
                    CharacterId = characterId,
                    SessionId = sessionId,
                    Reason = reason,
                    PenaltyMs = penaltyMs,
                },
                deadline: DateTime.UtcNow.AddSeconds(2));
            return reply.Ok ? Math.Max(0, reply.RemainingMs) : 0;
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"deserter_write_error=character={characterId} status={ex.StatusCode}");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                   or OperationCanceledException or IOException)
        {
            Console.WriteLine($"deserter_write_error=character={characterId} kind={ex.GetType().Name}");
            return 0;
        }
    }

    public void Dispose() => _channel.Dispose();
}
