using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace World;

public sealed record ProgressResult(bool Ok, string Reason, int MainStage, string Alignment);

/// <summary>
/// 剧情进度与立仙 / 立魔只在角色服（技术架构 6 节：主线走到第几场、立仙立魔在角色 schema）。
/// 世界服不连角色库，也不自己存一份；判「这块地表解锁没有」时现问一次。
/// 问不到一律按没解锁处理，不回一个「看起来能进」的结果。
/// </summary>
public interface IProgressReader
{
    Task<ProgressResult> ReadAsync(string account, ulong characterId, CancellationToken stopping);
}

public sealed class GrpcProgressReader : IProgressReader, IDisposable
{
    readonly GrpcChannel channel;
    readonly CharacterService.CharacterServiceClient client;

    public GrpcProgressReader(string upstream)
    {
        channel = GrpcChannel.ForAddress(upstream);
        client = new CharacterService.CharacterServiceClient(channel);
    }

    public async Task<ProgressResult> ReadAsync(
        string account, ulong characterId, CancellationToken stopping)
    {
        // 复用 C1 已有的只读回包：它先过 OwnsAsync，别人账号的角色在这里直接 NotFound，
        // 不会把别人的主线进度与立仙立魔泄漏出来。
        try
        {
            var reply = await client.DescribeMainlineAsync(
                new DescribeMainlineRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: stopping);
            if (!reply.Found)
            {
                return new ProgressResult(false, "character_not_found", 0, "");
            }

            return new ProgressResult(true, "", reply.MainStage, (reply.Alignment ?? "").Trim());
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return new ProgressResult(false, "character_not_found", 0, "");
        }
        catch (RpcException ex)
        {
            return new ProgressResult(false, "progress_unavailable:" + ex.Status.Detail, 0, "");
        }
    }

    public void Dispose() => channel.Dispose();
}

/// <summary>没有配上游时（只读口、纯计算自检）用它：一律读不到进度，判定按没解锁走。</summary>
public sealed class NoProgressReader : IProgressReader
{
    public Task<ProgressResult> ReadAsync(
        string account, ulong characterId, CancellationToken stopping) =>
        Task.FromResult(new ProgressResult(false, "progress_unavailable", 0, ""));
}
