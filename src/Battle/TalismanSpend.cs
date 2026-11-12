using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Battle;

/// <summary>
/// 用符之前的只读查询结果：这个编号是哪种符、回复落在哪个池。
/// 编号到池的映射只在角色服的配置里，战斗服不自己记一张表。
/// 没查到（不是符）或没连上角色服时 Ok 为假，这一手就不必去扣符。
/// </summary>
public sealed record TalismanInfo(
    bool Ok,
    string Reason,
    string ItemId,
    TalismanPool Pool,
    long CountLeft);

/// <summary>
/// 用掉一张符的结果。Ok 为真时 HpRestore / ManaRestore / CooldownMs /
/// CooldownGroup / Pool 才是配置里的数；拒绝路径一律给 0、空串与 Unspecified——
/// 没生效的数值不给，也不拿别的数顶。
/// </summary>
public sealed record TalismanSpendResult(
    bool Ok,
    string Reason,
    long CountLeft,
    long HpRestore,
    long ManaRestore,
    int CooldownMs,
    string CooldownGroup,
    TalismanPool Pool,
    bool Duplicate);

/// <summary>
/// 把「扣一张符」交给角色服——背包是持久数据，只有角色服能写（技术架构第 3 节）。
/// 战斗服不碰背包，也不自己记符的持有量。
/// Describe 是只读的一次查询，用来在扣符之前判满池与冷却；不扣、不写库。
/// </summary>
public interface ITalismanSpend : IDisposable
{
    TalismanInfo Describe(ulong characterId, string itemId);

    TalismanSpendResult Spend(ulong characterId, string itemId, string txnId);
}

/// <summary>
/// 没配 character_upstream 时走这里：这一场没有角色服，符从背包来不了。
/// 一律拒绝——宁可说「这场用不了」，也不做「本地凭空变出一张符」。
/// </summary>
public sealed class NoTalismanSpend : ITalismanSpend
{
    public TalismanInfo Describe(ulong characterId, string itemId) =>
        new(false, "upstream_off", itemId, TalismanPool.Unspecified, 0);

    public TalismanSpendResult Spend(ulong characterId, string itemId, string txnId) =>
        new(false, "upstream_off", 0, 0, 0, 0, "", TalismanPool.Unspecified, false);

    public void Dispose()
    {
    }
}

/// <summary>
/// 服间调用实现。一次用符 = 一次只读查询 + 一次扣符：
/// 查询只认编号与池，扣符才写背包，事务号 = 场次号 + 角色编号 + 这一场的第几张，
/// 同一个号重放由角色服的 grant_txn 挡住，不会把一张符扣成两次。
/// 角色服没起来时拒绝并记日志，不改本地生命与蓝量——扣不掉就不回。
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

    public TalismanInfo Describe(ulong characterId, string itemId)
    {
        try
        {
            var reply = _client.DescribeTalisman(
                new DescribeTalismanRequest
                {
                    CharacterId = characterId,
                    ItemId = itemId,
                },
                deadline: DateTime.UtcNow.AddSeconds(5));

            if (!reply.Ok)
            {
                Console.WriteLine("talisman_describe=reject character=" + characterId
                    + " item=" + itemId + " reason=" + reply.Reason);
                return new TalismanInfo(false, reply.Reason, itemId,
                    TalismanPool.Unspecified, reply.CountLeft);
            }

            Console.WriteLine("talisman_describe=ok character=" + characterId
                + " item=" + itemId + " pool=" + reply.Pool + " left=" + reply.CountLeft);
            return new TalismanInfo(true, "", itemId, reply.Pool, reply.CountLeft);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            Console.WriteLine("talisman_describe=error character=" + characterId
                + " item=" + itemId + " " + ex.Message.Replace('\n', ' '));
            return new TalismanInfo(false, "server_error", itemId,
                TalismanPool.Unspecified, 0);
        }
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
                    false, reply.Reason, reply.CountLeft, 0, 0, 0, "",
                    TalismanPool.Unspecified, reply.Duplicate);
            }

            Console.WriteLine("talisman_spend=ok character=" + characterId
                + " item=" + itemId
                + " txn=" + txnId
                + " left=" + reply.CountLeft
                + " pool=" + reply.Pool
                + " hp_restore=" + reply.HpRestore
                + " mana_restore=" + reply.ManaRestore
                + " cooldown_ms=" + reply.CooldownMs
                + " group=" + reply.CooldownGroup);

            return new TalismanSpendResult(
                true, "", reply.CountLeft, reply.HpRestore, reply.ManaRestore,
                (int)reply.CooldownMs, reply.CooldownGroup, reply.Pool, false);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            // 扣不掉就不回：这条不是「被拒绝」，是这一手没做成，客户端可以再点。
            Console.WriteLine("talisman_spend=error character=" + characterId
                + " item=" + itemId
                + " txn=" + txnId
                + " " + ex.Message.Replace('\n', ' '));
            return new TalismanSpendResult(false, "server_error", 0, 0, 0, 0, "",
                TalismanPool.Unspecified, false);
        }
    }

    public void Dispose() => _channel.Dispose();
}
