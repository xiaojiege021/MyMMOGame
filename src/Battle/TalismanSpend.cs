using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Battle;

/// <summary>
/// 用掉一张符的结果。Ok 为真时 HpRestore / CooldownMs / CooldownGroup 才是配置里的数；
/// 拒绝路径一律给 0 与空串——没生效的数值不给，也不拿别的数顶。
/// </summary>
public sealed record TalismanSpendResult(
    bool Ok,
    string Reason,
    long CountLeft,
    long HpRestore,
    int CooldownMs,
    string CooldownGroup,
    bool Duplicate);

/// <summary>
/// 把「扣一张符」交给角色服——背包是持久数据，只有角色服能写（技术架构第 3 节）。
/// 战斗服不碰背包，也不自己记符的持有量。
/// </summary>
public interface ITalismanSpend : IDisposable
{
    TalismanSpendResult Spend(ulong characterId, string itemId, string txnId);
}

/// <summary>
/// 没配 character_upstream 时走这里：这一场没有角色服，符从背包来不了。
/// 红符一律拒绝——宁可说「这场用不了」，也不做「本地凭空变出一张符」。
/// </summary>
public sealed class NoTalismanSpend : ITalismanSpend
{
    public TalismanSpendResult Spend(ulong characterId, string itemId, string txnId) =>
        new(false, "upstream_off", 0, 0, 0, "", false);

    public void Dispose()
    {
    }
}

/// <summary>
/// 服间调用实现。一次用符 = 一次扣符：事务号 = 场次号 + 角色编号 + 这一场的第几张，
/// 同一个号重放由角色服的 grant_txn 挡住，不会把一张符扣成两次。
/// 角色服没起来时拒绝并记日志，不改本地生命——扣不掉就不回血。
/// </summary>
public sealed class GrpcTalismanSpend : ITalismanSpend, IDisposable
{
    readonly GrpcChannel _channel;
    readonly CharacterService.CharacterServiceClient _client;

    public GrpcTalismanSpend(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new CharacterService.CharacterServiceClient(_channel);
    }

    public TalismanSpendResult Spend(ulong characterId, string itemId, string txnId)
    {
        try
        {
            var reply = _client.ConsumeTalisman(
                new ConsumeTalismanRequest
                {
                    CharacterId = characterId,
                    ItemId = itemId,
                    TxnId = txnId,
                },
                deadline: DateTime.UtcNow.AddSeconds(5));

            if (!reply.Ok)
            {
                Console.WriteLine("talisman_spend=reject character=" + characterId
                    + " item=" + itemId
                    + " txn=" + txnId
                    + " reason=" + reply.Reason
                    + " left=" + reply.CountLeft);
                return new TalismanSpendResult(
                    false, reply.Reason, reply.CountLeft, 0, 0, "", reply.Duplicate);
            }

            Console.WriteLine("talisman_spend=ok character=" + characterId
                + " item=" + itemId
                + " txn=" + txnId
                + " left=" + reply.CountLeft
                + " hp_restore=" + reply.HpRestore
                + " cooldown_ms=" + reply.CooldownMs
                + " group=" + reply.CooldownGroup);

            return new TalismanSpendResult(
                true, "", reply.CountLeft, reply.HpRestore,
                (int)reply.CooldownMs, reply.CooldownGroup, false);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            // 扣不掉就不回血：这条不是「被拒绝」，是这一手没做成，客户端可以再点。
            Console.WriteLine("talisman_spend=error character=" + characterId
                + " item=" + itemId
                + " txn=" + txnId
                + " " + ex.Message.Replace('\n', ' '));
            return new TalismanSpendResult(false, "server_error", 0, 0, 0, "", false);
        }
    }

    public void Dispose() => _channel.Dispose();
}
