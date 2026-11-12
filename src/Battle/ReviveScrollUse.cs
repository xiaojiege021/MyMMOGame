using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Battle;

/// <summary>
/// 用卷轴之前的只读查询结果。是不是卷轴、冷却到没到、背包有几张、拉起按什么
/// 比例回血，全由角色服读配置与角色库说了算——战斗服不解析配置文件，
/// 也不在本进程里记一份「这个人的卷轴冷却」。
/// </summary>
public sealed record ReviveScrollInfo(
    bool Ok,
    string Reason,
    string ItemId,
    long CountLeft,
    int CooldownMs,
    int HpRestorePct,
    int ExpCostPct,
    long ReadyAtMs,
    long NowMs,
    long Level,
    long Exp);

/// <summary>
/// 真用掉一张卷轴的结果。Ok 为真时比例、经验变化与冷却才是生效值；
/// 拒绝路径一律给 0 与空串——没生效的数值不给，也不拿别的数顶。
/// </summary>
public sealed record ReviveScrollUseResult(
    bool Ok,
    string Reason,
    string ItemId,
    long CountLeft,
    int HpRestorePct,
    long ExpCost,
    long Level,
    long ExpAfter,
    long ReadyAtMs,
    int CooldownMs,
    bool Duplicate);

/// <summary>
/// 把「扣一张卷轴」交给角色服：背包、当前级经验与跨场冷却都是持久数据，
/// 只有角色服能写（技术架构第 3 节）。生命是战斗态，仍由战斗服按回包拉起。
/// Describe 是只读的一次查询，用来在扣之前判倒地、冷却与背包；不写库。
/// </summary>
public interface IReviveScrollUse : IDisposable
{
    ReviveScrollInfo Describe(ulong characterId, string itemId);

    ReviveScrollUseResult Use(ulong characterId, string itemId, string txnId);
}

/// <summary>
/// 没配 character_upstream 时走这里：这一场没有角色服，卷轴从背包来不了。
/// 一律拒绝——宁可说「这场用不了」，也不做「本地凭空变出一张卷轴」。
/// </summary>
public sealed class NoReviveScrollUse : IReviveScrollUse
{
    public ReviveScrollInfo Describe(ulong characterId, string itemId) =>
        new(false, "upstream_off", itemId, 0, 0, 0, 0, 0, 0, 0, 0);

    public ReviveScrollUseResult Use(ulong characterId, string itemId, string txnId) =>
        new(false, "upstream_off", itemId, 0, 0, 0, 0, 0, 0, 0, false);

    public void Dispose()
    {
    }
}

/// <summary>
/// 服间调用实现。一次用卷 = 一次只读查询 + 一次扣卷，事务号由场次号、
/// 角色编号与这一场第几张拼成；同一个号重放由角色服的 grant_txn 挡住。
/// 角色服没起来时拒绝并记日志，不改本地生命与蓝量——扣不掉就不拉。
/// </summary>
public sealed class GrpcReviveScrollUse : IReviveScrollUse, IDisposable
{
    readonly GrpcChannel _channel;
    readonly CharacterService.CharacterServiceClient _client;

    public GrpcReviveScrollUse(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new CharacterService.CharacterServiceClient(_channel);
    }

    public ReviveScrollInfo Describe(ulong characterId, string itemId)
    {
        try
        {
            var reply = _client.DescribeReviveScroll(
                new DescribeReviveScrollRequest
                {
                    CharacterId = characterId,
                    ItemId = itemId,
                },
                deadline: DateTime.UtcNow.AddSeconds(5));

            if (!reply.Ok)
            {
                Console.WriteLine("scroll_describe=reject character=" + characterId
                    + " item=" + itemId + " reason=" + reply.Reason);
                return new ReviveScrollInfo(
                    false, reply.Reason, itemId, 0, 0, 0, 0, 0, 0, 0, 0);
            }

            Console.WriteLine("scroll_describe=ok character=" + characterId
                + " item=" + itemId + " count=" + reply.CountLeft
                + " cooldown_ms=" + reply.CooldownMs
                + " ready_at=" + reply.ReadyAtMs + " now=" + reply.NowMs
                + " level=" + reply.Level + " exp=" + reply.Exp);
            return new ReviveScrollInfo(
                true, "", reply.ItemId, reply.CountLeft,
                (int)reply.CooldownMs, (int)reply.HpRestorePct, (int)reply.ExpCostPct,
                reply.ReadyAtMs, reply.NowMs, reply.Level, reply.Exp);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            Console.WriteLine("scroll_describe=error character=" + characterId
                + " item=" + itemId + " " + ex.Message.Replace('\n', ' '));
            return new ReviveScrollInfo(
                false, "server_error", itemId, 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    public ReviveScrollUseResult Use(ulong characterId, string itemId, string txnId)
    {
        try
        {
            var reply = _client.ConsumeReviveScroll(
                new ConsumeReviveScrollRequest
                {
                    CharacterId = characterId,
                    ItemId = itemId,
                    TxnId = txnId,
                },
                deadline: DateTime.UtcNow.AddSeconds(5));

            if (!reply.Ok)
            {
                Console.WriteLine("scroll_spend=reject character=" + characterId
                    + " item=" + itemId + " txn=" + txnId
                    + " reason=" + reply.Reason + " left=" + reply.CountLeft);
                return new ReviveScrollUseResult(
                    false, reply.Reason, itemId, reply.CountLeft, 0, 0, 0, 0, 0, 0,
                    reply.Duplicate);
            }

            Console.WriteLine("scroll_spend=ok character=" + characterId
                + " item=" + itemId + " txn=" + txnId
                + " left=" + reply.CountLeft
                + " hp_restore_pct=" + reply.HpRestorePct
                + " exp_cost=" + reply.ExpCost
                + " level=" + reply.Level + " exp_after=" + reply.ExpAfter
                + " cooldown_ms=" + reply.CooldownMs + " ready_at=" + reply.ReadyAtMs);
            return new ReviveScrollUseResult(
                true, "", reply.ItemId, reply.CountLeft,
                (int)reply.HpRestorePct, reply.ExpCost, reply.Level, reply.ExpAfter,
                reply.ReadyAtMs, (int)reply.CooldownMs, false);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            Console.WriteLine("scroll_spend=error character=" + characterId
                + " item=" + itemId + " txn=" + txnId
                + " " + ex.Message.Replace('\n', ' '));
            return new ReviveScrollUseResult(
                false, "server_error", itemId, 0, 0, 0, 0, 0, 0, 0, false);
        }
    }

    public void Dispose() => _channel.Dispose();
}
