using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Social;

/// <summary>
/// Q4：宗门任务的等级门槛要判「角色等级」，社交服自己不留等级。这里只做一次
/// 只读调用，取角色服战斗快照里的 level（与战斗服同一份事实，不另读境界表），
/// 不写角色库、不发奖、不改背包。
///
/// 与 SectFounderGrant 同一套做法：服与服走明文 gRPC，失败不假装成功。
/// 角色不存在回 character_not_found；角色服没起来 / 超时回 character_unavailable，
/// 这一次完成不成立（请求里带不进等级，也不拿配置顶格值兜底）。
/// </summary>
public sealed class SectTaskLevelProbe : IDisposable
{
    readonly GrpcChannel channel;
    readonly CharacterService.CharacterServiceClient client;

    public SectTaskLevelProbe(string upstream)
    {
        channel = GrpcChannel.ForAddress(upstream);
        client = new CharacterService.CharacterServiceClient(channel);
    }

    public async Task<(bool Ok, string Reason, int Level)> LevelAsync(
        ulong characterId, CancellationToken stopping)
    {
        try
        {
            var reply = await client.DescribeBattleProfileAsync(
                new DescribeBattleProfileRequest { CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: stopping);
            return reply.Found
                ? (true, "", reply.Level)
                : (false, "character_not_found", 0);
        }
        catch (RpcException)
        {
            return (false, "character_unavailable", 0);
        }
    }

    public void Dispose() => channel.Dispose();
}
