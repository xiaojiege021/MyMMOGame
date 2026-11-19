using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Battle;

/// <summary>
/// 吃丹之前的只读查询结果：这个编号是哪种丹、回复落在哪个池。
/// 编号到池的映射只在角色服的 config/pill.json，战斗服不自己记一张表，也不读配置。
/// 没查到（不是丹、或是突破丹）或没连上角色服时 Ok 为假，这一手就不必去扣丹。
/// </summary>
public sealed record PillInfo(
    bool Ok,
    string Reason,
    string ItemId,
    PillPool Pool,
    long CountLeft);

/// <summary>
/// 吃掉一颗丹的结果。Ok 为真时 HpRestore / ManaRestore / CooldownMs /
/// CooldownGroup / Pool 才是配置里的数；拒绝路径一律给 0、空串与 Unspecified——
/// 没生效的数值不给，也不拿别的数顶。
/// </summary>
public sealed record PillSpendResult(
    bool Ok,
    string Reason,
    long CountLeft,
    long HpRestore,
    long ManaRestore,
    int CooldownMs,
    string CooldownGroup,
    PillPool Pool,
    bool Duplicate);

/// <summary>
/// 把「扣一颗丹」交给角色服——背包是持久数据，只有角色服能写（技术架构第 3 节）。
/// 战斗服不碰背包，也不自己记丹的持有量。
/// Describe 是只读的一次查询，用来在扣丹之前判满池与冷却；不扣、不写库。
/// 冷却组选的是和符同一套（heal / mana），所以「红药和红符共用冷却」
/// 「蓝药和蓝符共用冷却」不靠约定，靠的是同一个组名。
/// </summary>
public interface IPillSpend : IDisposable
{
    PillInfo Describe(ulong characterId, string itemId);

    PillSpendResult Spend(ulong characterId, string itemId, string txnId);
}

/// <summary>
/// 没配 character_upstream 时走这里：这一场没有角色服，丹从背包来不了。
/// 一律拒绝——宁可说「这场吃不了丹」，也不做「本地凭空变出一颗丹」。
/// </summary>
public sealed class NoPillSpend : IPillSpend
{
    public PillInfo Describe(ulong characterId, string itemId) =>
        new(false, "upstream_off", itemId, PillPool.Unspecified, 0);

    public PillSpendResult Spend(ulong characterId, string itemId, string txnId) =>
        new(false, "upstream_off", 0, 0, 0, 0, "", PillPool.Unspecified, false);

    public void Dispose()
    {
    }
}

/// <summary>
/// 服间调用实现。一次吃丹 = 一次只读查询 + 一次扣丹：查询只认编号与池，
/// 扣丹才写背包，事务号 = 场次号 + 角色编号 + 这一场的第几颗，
/// 同一个号重放由角色服的 grant_txn 挡住，不会把一颗丹扣成两次。
/// 角色服没起来时拒绝并记日志，不改本地生命与蓝量——扣不掉就不回。
/// </summary>
public sealed class GrpcPillSpend : IPillSpend, IDisposable
{
    readonly GrpcChannel _channel;
    readonly CharacterService.CharacterServiceClient _client;

    public GrpcPillSpend(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new CharacterService.CharacterServiceClient(_channel);
    }

    public PillInfo Describe(ulong characterId, string itemId)
    {
        try
        {
            var reply = _client.DescribePill(
                new DescribePillRequest
                {
                    CharacterId = characterId,
                    ItemId = itemId,
                },
                deadline: DateTime.UtcNow.AddSeconds(5));

            if (!reply.Ok)
            {
                Console.WriteLine("pill_describe=reject character=" + characterId
                    + " item=" + itemId + " reason=" + reply.Reason);
                return new PillInfo(false, reply.Reason, itemId,
                    PillPool.Unspecified, reply.CountLeft);
            }

            Console.WriteLine("pill_describe=ok character=" + characterId
                + " item=" + itemId + " pool=" + reply.Pool + " left=" + reply.CountLeft);
            return new PillInfo(true, "", itemId, reply.Pool, reply.CountLeft);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            Console.WriteLine("pill_describe=error character=" + characterId
                + " item=" + itemId + " " + ex.Message.Replace('\n', ' '));
            return new PillInfo(false, "server_error", itemId,
                PillPool.Unspecified, 0);
        }
    }

    public PillSpendResult Spend(ulong characterId, string itemId, string txnId)
    {
        try
        {
            var reply = _client.ConsumePill(
                new ConsumePillRequest
                {
                    CharacterId = characterId,
                    ItemId = itemId,
                    TxnId = txnId,
                },
                deadline: DateTime.UtcNow.AddSeconds(5));

            if (!reply.Ok)
            {
                Console.WriteLine("pill_spend=reject character=" + characterId
                    + " item=" + itemId + " reason=" + reply.Reason);
                return new PillSpendResult(false, reply.Reason, reply.CountLeft,
                    0, 0, 0, "", PillPool.Unspecified, false);
            }

            Console.WriteLine("pill_spend=ok character=" + characterId
                + " item=" + itemId + " left=" + reply.CountLeft
                + " duplicate=" + (reply.Duplicate ? "true" : "false"));
            return new PillSpendResult(true, "", reply.CountLeft,
                reply.HpRestore, reply.ManaRestore, (int)reply.CooldownMs,
                reply.CooldownGroup, reply.Pool, reply.Duplicate);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            Console.WriteLine("pill_spend=error character=" + characterId
                + " item=" + itemId + " " + ex.Message.Replace('\n', ' '));
            return new PillSpendResult(false, "server_error", 0,
                0, 0, 0, "", PillPool.Unspecified, false);
        }
    }

    public void Dispose() => _channel.Dispose();
}
