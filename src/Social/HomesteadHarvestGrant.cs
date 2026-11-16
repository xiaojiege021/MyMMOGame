using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Social;

public sealed record HarvestGrantResult(
    bool Ok, string Reason, bool Duplicate, long HomeCoinBalance);

/// <summary>收获家园币：角色服是货币唯一写入方，经 ApplyGrant 发 home_coin。</summary>
public sealed class HomesteadHarvestGrant : IDisposable
{
    readonly GrpcChannel channel;
    readonly CharacterService.CharacterServiceClient client;

    public HomesteadHarvestGrant(string upstream)
    {
        channel = GrpcChannel.ForAddress(upstream);
        client = new CharacterService.CharacterServiceClient(channel);
    }

    public async Task<HarvestGrantResult> GrantAsync(
        string account, ulong characterId, string txnId, long homeCoinDelta,
        CancellationToken stopping)
    {
        if (homeCoinDelta <= 0)
        {
            return new HarvestGrantResult(false, "yield_zero", false, 0);
        }

        var request = new ApplyGrantRequest
        {
            Account = account,
            CharacterId = characterId,
            TxnId = txnId,
        };
        request.Entries.Add(new GrantEntry
        {
            Currency = new CurrencyDelta { Kind = "home_coin", Delta = homeCoinDelta },
        });

        try
        {
            var reply = await client.ApplyGrantAsync(
                request,
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: stopping);
            var bal = reply.Balances.FirstOrDefault(b => b.Kind == "home_coin")?.Amount ?? 0;
            return new HarvestGrantResult(true, "", reply.Duplicate, bal);
        }
        catch (RpcException ex)
        {
            return new HarvestGrantResult(false, "grant_failed:" + ex.Status.Detail, false, 0);
        }
    }

    public void Dispose() => channel.Dispose();
}