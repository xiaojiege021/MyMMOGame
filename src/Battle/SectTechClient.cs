using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Social;

namespace Battle;

/// <summary>
/// P3-3a3：城战开场要从社交服读「这个角色所属宗门的科技等级」。
/// 宗门数据在社交 schema（技术架构 3 / 6 节），战斗服不直连库、不算伤害公式之外的东西；
/// 这里只取等级事实值，折算与加成全在战斗服按 config/sect_tech.json 算。
///
/// 上游不可达 / 没宗门 / 没科技行一律回 0：城战照常开场，只是没有加成。
/// 与 P2-1c 判同宗门的降级口径一致，不让「社交服没起」拖垮老链路。
/// </summary>
public interface ISectTechSource : IDisposable
{
    /// <summary>只在城战进场调用；0 表示没有加成。</summary>
    int Get(ulong characterId);
}

/// <summary>没配 social_upstream：不查，科技恒 0，老链路（含城战）照跑。</summary>
public sealed class NoSectTechSource : ISectTechSource
{
    public int Get(ulong characterId) => 0;

    public void Dispose()
    {
    }
}

/// <summary>服间调用实现：社交服只读回一个等级，写库与加成都不在这里。</summary>
public sealed class GrpcSectTechSource : ISectTechSource
{
    readonly GrpcChannel _channel;
    readonly SocialService.SocialServiceClient _client;

    public GrpcSectTechSource(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new SocialService.SocialServiceClient(_channel);
    }

    public int Get(ulong characterId)
    {
        try
        {
            var reply = _client.DescribeSectTech(
                new DescribeSectTechRequest { CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(2));
            return reply.Ok && reply.Found ? Math.Max(0, reply.TechLevel) : 0;
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"sect_tech_error=character={characterId} status={ex.StatusCode}");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                   or OperationCanceledException or IOException)
        {
            Console.WriteLine($"sect_tech_error=character={characterId} kind={ex.GetType().Name}");
            return 0;
        }
    }

    public void Dispose() => _channel.Dispose();
}
