using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// Q5/J2 主城修炼 / 境界任务的 HTTP 入口。网关只做三件事：认令牌、复核角色归属、
/// 服务端生成事务号后转给角色服。任务、次数、门槛、奖励与周期全在
/// config/city_tasks.json 与 config/calendar.json；灵石数量、城池抽成比例、
/// 等级与境界判定一条都不从请求里取，也不在网关本地算。
/// 业务前提不满足是 HTTP 200 + ok=false + reason；身份与参数错才走 401 / 400 / 404。
/// </summary>
public static class CityTaskApi
{
    // 完成一条的请求体：只带角色号与任务编号。次数、奖励、税率一概带不进来。
    public sealed record CompleteBody(ulong? CharacterId, string? TaskId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(characterChannel);

        app.MapGet("/city/tasks", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            var characterId = ParseId(context.Request.Query["character_id"].ToString());
            if (characterId == 0)
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

                var reply = await character.DescribeCityTasksAsync(
                    new DescribeCityTasksRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("city_tasks account=" + account
                    + " id=" + characterId
                    + " ok=" + reply.Ok
                    + " reason=" + reply.Reason
                    + " level=" + reply.Level
                    + " realm=" + reply.Realm
                    + " rows=" + reply.Tasks.Count
                    + " config_missing=" + (reply.ConfigMissing ? "true" : "false"));

                await WriteAsync(context, 200, new
                {
                    ok = reply.Ok,
                    reason = reply.Reason,
                    character_id = reply.CharacterId,
                    level = reply.Level,
                    realm = reply.Realm,
                    table_version = reply.TableVersion,
                    pending = reply.Pending,
                    config_missing = reply.ConfigMissing,
                    stones = reply.Stones.Select(s => new
                    {
                        kind = s.Kind,
                        amount = s.Amount,
                    }),
                    tasks = reply.Tasks.Select(t => new
                    {
                        task_id = t.TaskId,
                        name = t.Name,
                        city_id = t.CityId,
                        period = t.Period,
                        done = t.Done,
                        max_per_period = t.MaxPerPeriod,
                        require_level = t.RequireLevel,
                        require_realm = t.RequireRealm,
                        rewards = t.Rewards.Select(r => new
                        {
                            currency = r.Currency,
                            amount = r.Amount,
                        }),
                        period_key = t.PeriodKey,
                        eligible = t.Eligible,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("city_tasks_reject account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, ex.StatusCode == StatusCode.NotFound ? 404 : 502,
                    new { error = ex.Status.Detail });
            }
        });

        app.MapPost("/city/tasks/complete", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            CompleteBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CompleteBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.TaskId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var taskId = body.TaskId!.Trim();
            try
            {
                if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                // 事务号服务端生成：客户端带不进来，grant_txn 主键挡重放。
                var txnId = "city_task:" + Guid.NewGuid().ToString("N");
                var reply = await character.CompleteCityTaskAsync(
                    new CompleteCityTaskRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                        TaskId = taskId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("city_task_complete account=" + account
                    + " id=" + body.CharacterId.Value
                    + " task=" + taskId
                    + " txn=" + txnId
                    + " ok=" + reply.Ok
                    + " reason=" + reply.Reason
                    + " duplicate=" + reply.Duplicate
                    + " done=" + reply.Done
                    + " max=" + reply.MaxPerPeriod
                    + " period=" + reply.PeriodKey
                    + " tax_pct=" + reply.TaxPct
                    + " grants=" + string.Join(",", reply.Grants.Select(
                        g => g.Currency + ":" + g.Amount + ":" + g.BalanceBefore + "->"
                            + g.BalanceAfter + ":tax" + g.TaxAmount)));

                await WriteAsync(context, 200, new
                {
                    ok = reply.Ok,
                    reason = reply.Reason,
                    duplicate = reply.Duplicate,
                    task_id = reply.TaskId,
                    city_id = reply.CityId,
                    done = reply.Done,
                    max_per_period = reply.MaxPerPeriod,
                    period_key = reply.PeriodKey,
                    txn_id = reply.TxnId,
                    tax_pct = reply.TaxPct,
                    grants = reply.Grants.Select(g => new
                    {
                        currency = g.Currency,
                        amount = g.Amount,
                        balance_before = g.BalanceBefore,
                        balance_after = g.BalanceAfter,
                        tax_amount = g.TaxAmount,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("city_task_complete_reject account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, ex.StatusCode == StatusCode.NotFound ? 404 : 502,
                    new { error = ex.Status.Detail });
            }
        });
    }

    static ulong ParseId(string raw) =>
        ulong.TryParse(raw, out var value) ? value : 0;
    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
