using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Social;

public sealed record FounderGrantResult(bool Ok, string Reason, bool Duplicate);

/// <summary>
/// 创建宗门的金币消耗走角色服的 ApplyGrant：角色服是持久货币的唯一写入方，
/// 社交服不碰角色 schema。事务号由网关生成（客户端带不进来），角色服的
/// grant_txn 主键保证同一个号重放不重复扣，整笔在一个事务里。
///
/// 方案 11.1 写定「创建消耗金币」但没给数字：founder_gold_cost 在
/// config/sect.json 里标待定。为 0（待定未启用）时这条链路不扣款；数字定了
/// 把配置改成正数、重启社交服即生效，不用改代码，也不在代码里写死金币数。
/// </summary>
public sealed class SectFounderGrant : IDisposable
{
    readonly GrpcChannel channel;
    readonly CharacterService.CharacterServiceClient client;

    public SectFounderGrant(string upstream)
    {
        channel = GrpcChannel.ForAddress(upstream);
        client = new CharacterService.CharacterServiceClient(channel);
    }

    public async Task<FounderGrantResult> ChargeAsync(
        string account, ulong characterId, string txnId, SectTable sect,
        CancellationToken stopping)
    {
        var cost = sect.Create.FounderGoldCost;
        if (cost == 0)
        {
            // 待定数字没启用：不扣款，也不向角色库写一行。
            return new FounderGrantResult(true, "", false);
        }

        var request = new ApplyGrantRequest
        {
            Account = account,
            CharacterId = characterId,
            TxnId = txnId,
        };
        request.Entries.Add(new GrantEntry
        {
            Currency = new CurrencyDelta
            {
                Kind = sect.Create.FounderGoldKind,
                Delta = -cost,
            },
        });

        try
        {
            var reply = await client.ApplyGrantAsync(
                request,
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: stopping);
            // duplicate 表示这个事务号已经扣过：整笔不做，不重复建宗门。
            return new FounderGrantResult(true, "", reply.Duplicate);
        }
        catch (RpcException ex)
        {
            // 角色服拒绝（金币不足等）或没起来：这次创建不成立，不回假成功。
            return new FounderGrantResult(false, "grant_failed:" + ex.Status.Detail, false);
        }
    }

    public void Dispose() => channel.Dispose();
}
