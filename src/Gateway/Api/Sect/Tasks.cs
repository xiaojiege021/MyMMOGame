using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>SectApi HTTP：宗门日常 / 周常面板与完成。</summary>
public static partial class SectApi
{
    static void RegisterTasks(WebApplication app, TokenStore tokens, SocialService.SocialServiceClient social, CharacterService.CharacterServiceClient character)
    {
        // ---- Q4/J1：宗门日常 / 周常面板 + 完成一条 ----
        // 面板只读：任务、次数、奖励、周期键全由社交服给事实值，网关一个都不算。
        app.MapGet("/sect/tasks", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
                || characterId == 0
                || !ulong.TryParse(context.Request.Query["sect_id"].ToString(), out var sectId)
                || sectId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await GatewayAuth.OwnsAsync(character, account, characterId, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var reply = await social.DescribeSectTasksAsync(
                    new DescribeSectTasksRequest
                    {
                        Account = account,
                        CharacterId = characterId,
                        SectId = sectId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("sect_tasks account=" + account
                    + " id=" + characterId
                    + " sect=" + sectId
                    + " ok=" + reply.Ok
                    + " reason=" + reply.Reason
                    + " level=" + reply.Level
                    + " construction=" + reply.Construction
                    + " contribution=" + reply.Contribution
                    + " rows=" + reply.Tasks.Count);

                await WriteAsync(context, 200, new
                {
                    ok = reply.Ok,
                    reason = reply.Reason,
                    sect_id = reply.SectId,
                    level = reply.Level,
                    construction = reply.Construction,
                    contribution = reply.Contribution,
                    tasks = reply.Tasks.Select(t => new
                    {
                        task_id = t.TaskId,
                        name = t.Name,
                        period = t.Period,
                        done = t.Done,
                        max_per_period = t.MaxPerPeriod,
                        require_level = t.RequireLevel,
                        reward_contribution = t.RewardContribution,
                        reward_construction = t.RewardConstruction,
                        period_key = t.PeriodKey,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("sect_tasks_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });

        app.MapPost("/sect/tasks/complete", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            SectTaskBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SectTaskBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.SectId is null || body.SectId == 0
                || string.IsNullOrWhiteSpace(body.TaskId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                // 事务号服务端生成：客户端带不进来，sect_task_ledger.txn_id 挡重放。
                var txnId = "sect_task:" + Guid.NewGuid().ToString("N");
                var result = await social.CompleteSectTaskAsync(
                    new CompleteSectTaskRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                        SectId = body.SectId.Value,
                        TaskId = body.TaskId!.Trim(),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("sect_task_complete account=" + account
                    + " id=" + body.CharacterId.Value
                    + " sect=" + body.SectId.Value
                    + " task=" + body.TaskId!.Trim()
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " done=" + result.Done
                    + " max=" + result.MaxPerPeriod
                    + " contribution=" + result.ContributionBefore + "->" + result.ContributionAfter
                    + " construction=" + result.ConstructionBefore + "->" + result.ConstructionAfter
                    + " period=" + result.PeriodKey);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    sect_id = result.SectId,
                    task_id = result.TaskId,
                    done = result.Done,
                    max_per_period = result.MaxPerPeriod,
                    contribution_before = result.ContributionBefore,
                    contribution_after = result.ContributionAfter,
                    construction_before = result.ConstructionBefore,
                    construction_after = result.ConstructionAfter,
                    granted_contribution = result.GrantedContribution,
                    granted_construction = result.GrantedConstruction,
                    period_key = result.PeriodKey,
                    txn_id = txnId,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("sect_task_complete_error account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, 502, new { error = ex.Status.Detail });
            }
        });
    }
}
