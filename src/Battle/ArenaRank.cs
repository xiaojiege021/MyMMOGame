using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Battle;

/// <summary>
/// B11：竞技场一场通关之后，把「这一场赢了」交给角色服记段位分。
/// 战斗服只提供事务号、模式与参战角色；加多少分、落到哪一档、发什么外观全在
/// 角色服的 config/arena_rank.json，战斗服不读段位表，也不在这里算分数。
/// 散场（没打完）不报：方案没写失败惩罚，不自己编一套扣分。
/// </summary>
public interface IArenaRank : IDisposable
{
    /// <summary>
    /// 逐人报一场胜仗。settlementId 是这一场的本地结算号，同一个号重放由角色服挡住。
    /// </summary>
    void ReportWin(string settlementId, string mode, IReadOnlyCollection<ulong> memberIds);
}

/// <summary>
/// 没配 exp_upstream 时走这里：P0 的本机口径，不碰角色数据，竞技场照常打完。
/// </summary>
public sealed class NoArenaRank : IArenaRank
{
    public void ReportWin(string settlementId, string mode, IReadOnlyCollection<ulong> memberIds)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// 服间调用实现。每人这一场一份段位分，事务号 = 结算号 + 角色编号，
/// 同一个号再提交由角色服的流水主键挡住。上报失败只记日志，不影响这一场打完；
/// 角色服没起来时，竞技场仍然能打，只是这一场不计分。
/// </summary>
public sealed class GrpcArenaRank : IArenaRank, IDisposable
{
    readonly GrpcChannel _channel;
    readonly CharacterService.CharacterServiceClient _client;

    public GrpcArenaRank(string upstream)
    {
        _channel = GrpcChannel.ForAddress(upstream);
        _client = new CharacterService.CharacterServiceClient(_channel);
    }

    public void ReportWin(string settlementId, string mode, IReadOnlyCollection<ulong> memberIds)
    {
        foreach (var characterId in memberIds)
        {
            var txnId = settlementId + ":a:" + characterId.ToString("x16");
            try
            {
                var reply = _client.ReportArenaResult(
                    new ReportArenaResultRequest
                    {
                        CharacterId = characterId,
                        TxnId = txnId,
                        Mode = mode,
                        Outcome = "win",
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5));

                Console.WriteLine("arena_rank=ok txn=" + txnId
                    + " character=" + characterId
                    + " mode=" + mode
                    + " score=" + reply.Score
                    + " delta=" + reply.ScoreDelta
                    + " tier=" + reply.TierKey
                    + " tier_up=" + (reply.TierUp ? "true" : "false")
                    + " wins=" + reply.Wins
                    + " duplicate=" + (reply.Duplicate ? "true" : "false"));
            }
            catch (Exception ex) when (ex is RpcException or InvalidOperationException)
            {
                Console.WriteLine("arena_rank=error txn=" + txnId
                    + " character=" + characterId
                    + " " + ex.Message.Replace('\n', ' '));
            }
        }
    }

    public void Dispose() => _channel.Dispose();
}
