using Grpc.Core;
using Mmo.Proto.Social;

namespace Social;

public sealed partial class SocialGrpcService
{
    // ---- Q4/J1：宗门日常 / 周常面板 + 完成一条 ----
    // 任务、次数、奖励与周期键都只由社交服按 config/sect_tasks.json 与
    // config/calendar.json 判定；等级门槛按角色服只读快照判，请求里带不进来。
    public override async Task<DescribeSectTasksResponse> DescribeSectTasks(
        DescribeSectTasksRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0 || request.SectId == 0)
        {
            return new DescribeSectTasksResponse
            {
                Ok = false, Reason = "request_missing", SectId = request.SectId,
            };
        }

        var board = await tasks.BoardAsync(
            (long)request.SectId, (long)request.CharacterId,
            DateTimeOffset.UtcNow, context.CancellationToken);
        var reply = new DescribeSectTasksResponse
        {
            Ok = board.Ok,
            Reason = board.Reason,
            SectId = (ulong)board.SectId,
            Level = board.Level,
            Construction = board.Construction,
            Contribution = board.Contribution,
        };
        foreach (var row in board.Rows)
        {
            reply.Tasks.Add(new SectTaskEntry
            {
                TaskId = row.TaskId,
                Name = row.Name,
                Period = row.Period,
                Done = row.Done,
                MaxPerPeriod = row.MaxPerPeriod,
                RequireLevel = row.RequireLevel,
                RewardContribution = row.RewardContribution,
                RewardConstruction = row.RewardConstruction,
                PeriodKey = row.PeriodKey,
            });
        }

        return reply;
    }

    public override async Task<CompleteSectTaskResponse> CompleteSectTask(
        CompleteSectTaskRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var taskId = (request.TaskId ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0 || request.SectId == 0
            || taskId.Length == 0 || txnId.Length == 0)
        {
            return CompleteTaskFail("request_missing", request.SectId, taskId, txnId);
        }

        // 任务编号必须在配置里：不在就 task_unknown，连角色服都不问。
        if (taskTable.TaskFor(taskId) is null)
        {
            return CompleteTaskFail("task_unknown", request.SectId, taskId, txnId);
        }

        // 等级门槛只认角色服的只读事实；查不到 / 连不上都不假装通过。
        var level = await levelProbe.LevelAsync(request.CharacterId, context.CancellationToken);
        if (!level.Ok)
        {
            return CompleteTaskFail(level.Reason, request.SectId, taskId, txnId);
        }

        var outcome = await tasks.CompleteAsync(
            (long)request.SectId, (long)request.CharacterId, level.Level, taskId, txnId,
            DateTimeOffset.UtcNow, context.CancellationToken);
        return new CompleteSectTaskResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            SectId = (ulong)outcome.SectId,
            TaskId = outcome.TaskId,
            Done = outcome.Done,
            MaxPerPeriod = outcome.MaxPerPeriod,
            ContributionBefore = outcome.ContributionBefore,
            ContributionAfter = outcome.ContributionAfter,
            ConstructionBefore = outcome.ConstructionBefore,
            ConstructionAfter = outcome.ConstructionAfter,
            GrantedContribution = outcome.GrantedContribution,
            GrantedConstruction = outcome.GrantedConstruction,
            PeriodKey = outcome.PeriodKey,
            TxnId = txnId,
        };
    }

    static CompleteSectTaskResponse CompleteTaskFail(
        string reason, ulong sectId, string taskId, string txnId) =>
        new()
        {
            Ok = false, Reason = reason, SectId = sectId, TaskId = taskId, TxnId = txnId,
        };
}
