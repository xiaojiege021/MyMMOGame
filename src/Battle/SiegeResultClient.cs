using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Battle;

/// <summary>战斗服交给角色服的胜负事实值。税率、旧归属、奖励都不在这里。</summary>
public sealed record SiegeResultFact(
    string TxnId, string CityId, string MatchId, uint WinnerTeam, ulong WinnerSect);

/// <summary>角色服事务内读回的城池事实值，不是请求回声。</summary>
public sealed record SiegeResultReply(
    bool Ok, string Reason, bool Duplicate, string CityId, string OwnerSect, int TaxPct);

/// <summary>P3-3a4：胜方宗门只从社交服只读查，战斗服不连库、不编宗门。</summary>
public interface ISiegeSectSource : IDisposable
{
    /// <summary>null 表示没宗门 / 上游不可用，这一场就不写回归属。</summary>
    ulong? SectOf(ulong characterId);
}

/// <summary>P3-3a4：胜负写回交给角色服（城池正本与社交 schema 都在这个进程写）。</summary>
public interface ISiegeResultSink : IDisposable
{
    SiegeResultReply Apply(SiegeResultFact fact);
}

public sealed class NoSiegeSectSource : ISiegeSectSource
{
    public ulong? SectOf(ulong characterId) => null;

    public void Dispose()
    {
    }
}

public sealed class NoSiegeResultSink : ISiegeResultSink
{
    public SiegeResultReply Apply(SiegeResultFact fact) =>
        new(false, "upstream_off", false, fact.CityId, "", 0);

    public void Dispose()
    {
    }
}

/// <summary>复用 P2-1c 已有的只读 RPC：建宗门的人不进名册也查得到。</summary>
public sealed class GrpcSiegeSectSource : ISiegeSectSource
{
    readonly GrpcChannel _channel;
    readonly SocialService.SocialServiceClient _client;

    public GrpcSiegeSectSource(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new SocialService.SocialServiceClient(_channel);
    }

    public ulong? SectOf(ulong characterId)
    {
        try
        {
            var reply = _client.DescribeCharacterSect(
                new DescribeCharacterSectRequest { CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(2));
            return reply.Ok && reply.Found && reply.SectId != 0 ? reply.SectId : null;
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"siege_sect_error=character={characterId} status={ex.StatusCode}");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                   or OperationCanceledException or IOException)
        {
            Console.WriteLine($"siege_sect_error=character={characterId} kind={ex.GetType().Name}");
            return null;
        }
    }

    public void Dispose() => _channel.Dispose();
}

/// <summary>写回失败只记日志，不影响这一场已经发生的技能结算。</summary>
public sealed class GrpcSiegeResultSink : ISiegeResultSink
{
    readonly GrpcChannel _channel;
    readonly CharacterService.CharacterServiceClient _client;

    public GrpcSiegeResultSink(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new CharacterService.CharacterServiceClient(_channel);
    }

    public SiegeResultReply Apply(SiegeResultFact fact)
    {
        try
        {
            var reply = _client.ApplySiegeResult(
                new ApplySiegeResultRequest
                {
                    TxnId = fact.TxnId,
                    CityId = fact.CityId,
                    MatchId = fact.MatchId,
                    WinnerTeam = fact.WinnerTeam,
                    WinnerSect = fact.WinnerSect,
                },
                deadline: DateTime.UtcNow.AddSeconds(5));
            return new SiegeResultReply(
                reply.Ok, reply.Reason, reply.Duplicate,
                reply.CityId, reply.OwnerSect, reply.TaxPct);
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"siege_result_error=txn={fact.TxnId} status={ex.StatusCode}");
            return new SiegeResultReply(false, "upstream_error", false, fact.CityId, "", 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                   or OperationCanceledException or IOException)
        {
            Console.WriteLine($"siege_result_error=txn={fact.TxnId} kind={ex.GetType().Name}");
            return new SiegeResultReply(false, "upstream_error", false, fact.CityId, "", 0);
        }
    }

    public void Dispose() => _channel.Dispose();
}
