using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Battle;

/// <summary>
/// 一场通关之后，把「这一笔等级经验」交给角色服结算。战斗服只提供事务号、
/// 来源和这一场的进本人数，不带任何经验数值：基础经验、组队加成与升级曲线
/// 全在角色服的 config/exp.json，角色服是持久数据的唯一写入方。
/// 活动、任务、竞技场等来源也调角色服同一个接口，不另写一套加成。
/// </summary>
public interface IExpSettlement : IDisposable
{
    void SettleParty(
        string settlementId, string source, int partySize, IReadOnlyCollection<ulong> memberIds);
}

/// <summary>
/// 没配 exp_upstream 时走这里：P0 的本机结算口径，只写本地结算记录，
/// 不改任何角色数据。老核对与不带副本的练习场都走这一条，行为与 P0 完全一致。
/// </summary>
public sealed class NoExpSettlement : IExpSettlement
{
    public void SettleParty(
        string settlementId, string source, int partySize, IReadOnlyCollection<ulong> memberIds)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// 服间调用实现。每人这一场一份个人经验，事务号 = 结算号 + 角色编号，
/// 同一个号再提交由角色服的幂等表挡住。结算失败只记日志，不影响本地结算记录
/// 与回包——角色服没起来时，P0 那一场仍然能正常打完。
/// </summary>
public sealed class GrpcExpSettlement : IExpSettlement, IDisposable
{
    readonly GrpcChannel _channel;
    readonly CharacterService.CharacterServiceClient _client;

    public GrpcExpSettlement(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new CharacterService.CharacterServiceClient(_channel);
    }

    public void SettleParty(
        string settlementId, string source, int partySize, IReadOnlyCollection<ulong> memberIds)
    {
        foreach (var characterId in memberIds)
        {
            var txnId = settlementId + ":" + characterId.ToString("x16");
            try
            {
                var reply = _client.SettleExp(
                    new SettleExpRequest
                    {
                        CharacterId = characterId,
                        TxnId = txnId,
                        Source = source,
                        PartySize = (uint)partySize,
                        // P2-1c：把整队成员交给角色服判同宗门；战斗服不解析谁同门。
                        MemberIds = { memberIds },
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5));

                Console.WriteLine("exp_settle=ok txn=" + txnId
                    + " character=" + characterId
                    + " party=" + partySize
                    + " gain=" + reply.ExpGain
                    + " bonus=" + reply.PartyBonus
                    + " same_sect=" + reply.SameSectBonus
                    + " level=" + reply.Level
                    + " duplicate=" + reply.Duplicate);
            }
            catch (Exception ex) when (ex is RpcException or InvalidOperationException)
            {
                Console.WriteLine("exp_settle=error txn=" + txnId
                    + " character=" + characterId
                    + " " + ex.Message.Replace('\n', ' '));
            }
        }
    }

    public void Dispose() => _channel.Dispose();
}
