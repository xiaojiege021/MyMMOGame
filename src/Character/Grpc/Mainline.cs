using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

    /// <summary>
    /// C1：主线十二场只读口。只回角色库里的剧情进度与下一场编号，十二场内容
    /// 在 config/mainline.json，不进协议、不发给客户端自己算。
    /// </summary>
    public override async Task<DescribeMainlineResponse> DescribeMainline(
        DescribeMainlineRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var characterId = (long)request.CharacterId;
        if (account.Length == 0 || characterId <= 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_or_character_missing"));
        }

        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var state = await mainlines.StateAsync(characterId, account, context.CancellationToken);
        if (!state.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var count = mainlineTable.IsMissing ? 0 : mainlineTable.Stages.Count;
        return new DescribeMainlineResponse
        {
            CharacterId = request.CharacterId,
            Found = true,
            MainStage = state.MainStage,
            Alignment = state.Alignment,
            // 个人线停在第 12 场：走到尽头与配置缺表都回 0，不给客户端自己外推。
            NextStage = count > 0 && state.MainStage < count ? state.MainStage + 1 : 0,
            StageCount = count,
            ConfigMissing = mainlineTable.IsMissing,
            Level = state.Level,
            Realm = state.Realm,
        };
    }


    /// <summary>
    /// N7 交任务：txn_id 须与那场主线 SettleExp 相同；一笔事务里发 mainline_drops 并推进 main_stage。
    /// </summary>
    public override async Task<TurnInMainlineResponse> TurnInMainline(
        TurnInMainlineRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        var characterId = (long)request.CharacterId;
        if (account.Length == 0 || characterId <= 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_or_character_missing"));
        }

        if (txnId.Length == 0 || txnId.Length > MainlineRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_id_invalid"));
        }

        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var result = await mainlines.TurnInAsync(
            characterId, account, txnId, request.Stage, request.Alignment ?? "",
            context.CancellationToken);

        // Q6/J4：主线交任务给通行证点数（来源按配置的 mainline_stage）。重放与补交都按同一个
        // 事件号幂等：加点流水只有一笔，换季也不重置（period=once）。
        if (result.Ok || result.Duplicate)
        {
            var mainlineSource = seasonPassProgressTable.FindMainline();
            if (mainlineSource is not null && result.Stage >= 1)
            {
                await AwardPassPointsAsync(
                    characterId, mainlineSource, result.Stage.ToString(), txnId,
                    context.CancellationToken);
            }
        }

        return new TurnInMainlineResponse
        {
            Ok = result.Ok,
            Reason = result.Reason,
            Duplicate = result.Duplicate,
            Stage = result.Stage,
            MainStage = result.MainStage,
            Alignment = result.Alignment,
            TxnId = txnId,
        };
    }


    /// <summary>
    /// Q6/J4：给通行证加点。只由服务端事件触发（副本通关 / 主线交任务），客户端没有入口。
    /// 加点的幂等键从事件的事务号派生：同一事件重放算同一笔，补一次不双加。
    /// 加点失败不拖垮经验结算与交任务本身——记一行 pass_points= 的失败原因，
    /// 下一次同一事件重放（或核对入口）会把这一笔补上。
    /// </summary>
    async Task AwardPassPointsAsync(
        long characterId, SeasonPassSourceDef source, string refId, string eventTxn,
        CancellationToken stopping)
    {
        try
        {
            var season = await seasonSource.CurrentSeasonAsync(stopping);
            if (season is null)
            {
                logger.LogWarning(
                    "pass_points id={Id} source={Source} reason=season_unavailable",
                    characterId, source.Id);
                return;
            }

            // 派生键：事件号长短不一，先取 sha256 前 32 个十六进制字符，再加前缀。
            var digest = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(eventTxn));
            var txn = "pass_pt:" + Convert.ToHexString(digest)[..32];
            var outcome = await seasonPassPoints.AwardAsync(
                characterId, season, source.Id, refId, txn, DateTimeOffset.UtcNow, stopping);
            logger.LogInformation(
                "pass_points id={Id} season={Season} source={Source} ref={Ref} ok={Ok} "
                + "reason={Reason} points={Points} before={Before} after={After} "
                + "duplicate={Duplicate} txn={Txn}",
                characterId, season, source.Id, refId, outcome.Ok,
                outcome.Reason.Length == 0 ? "-" : outcome.Reason, outcome.Points,
                outcome.PointsBefore, outcome.PointsAfter, outcome.Duplicate, txn);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "pass_points id={Id} source={Source} error={Error}",
                characterId, source.Id, ex.Message);
        }
    }


    /// <summary>
    /// Q5/J2：主城修炼 / 境界任务面板。只读，不写库。
    /// 任务表没加载到（老卡环境没有 config/city_tasks.json）时回 config_missing，
    /// 不编一份假内容；门槛与次数都是服务端算出来的展示事实。
    /// </summary>
    public override async Task<DescribeCityTasksResponse> DescribeCityTasks(
        DescribeCityTasksRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var characterId = (long)request.CharacterId;
        if (account.Length == 0 || characterId <= 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_or_character_missing"));
        }

        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        if (cityTasks is null || cityTaskTable is null)
        {
            return new DescribeCityTasksResponse
            {
                Ok = true,
                CharacterId = request.CharacterId,
                ConfigMissing = true,
            };
        }

        var board = await cityTasks.BoardAsync(
            characterId, DateTimeOffset.UtcNow, context.CancellationToken);
        if (!board.Ok)
        {
            return new DescribeCityTasksResponse
            {
                Ok = false,
                Reason = board.Reason,
                CharacterId = request.CharacterId,
                ConfigMissing = false,
            };
        }

        var response = new DescribeCityTasksResponse
        {
            Ok = true,
            CharacterId = request.CharacterId,
            Level = board.Level,
            Realm = board.Realm,
            TableVersion = cityTaskTable.Version,
            Pending = cityTaskTable.Pending,
        };

        foreach (var row in board.Rows)
        {
            var fact = new CityTaskFact
            {
                TaskId = row.TaskId,
                Name = row.Name,
                CityId = row.CityId,
                Period = row.Period,
                Done = row.Done,
                MaxPerPeriod = row.MaxPerPeriod,
                RequireLevel = row.RequireLevel,
                RequireRealm = row.RequireRealm,
                PeriodKey = row.PeriodKey,
                Eligible = row.Eligible,
            };
            foreach (var reward in row.Rewards)
            {
                fact.Rewards.Add(new CityTaskRewardFact
                {
                    Currency = reward.Currency,
                    Amount = reward.Amount,
                });
            }

            response.Tasks.Add(fact);
        }

        foreach (var pair in board.Stones.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            response.Stones.Add(new CityCurrencyBalance { Kind = pair.Key, Amount = pair.Value });
        }

        return response;
    }


    /// <summary>
    /// Q5/J2：完成一条城池任务。业务拒绝（task_unknown / duplicate / level_too_low /
    /// realm_too_low / limit_reached / city_missing）是 ok=false + reason；
    /// 身份与参数错才走 gRPC 错误码。请求里的数量、税率、等级、境界一概不采信。
    /// </summary>
    public override async Task<CompleteCityTaskResponse> CompleteCityTask(
        CompleteCityTaskRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        var taskId = (request.TaskId ?? "").Trim();
        var characterId = (long)request.CharacterId;
        if (account.Length == 0 || characterId <= 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_or_character_missing"));
        }

        if (txnId.Length == 0 || txnId.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_id_invalid"));
        }

        if (taskId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "task_id_missing"));
        }

        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        if (cityTasks is null || cityTaskTable is null)
        {
            return new CompleteCityTaskResponse
            {
                Ok = false,
                Reason = "config_missing",
                TaskId = taskId,
                TxnId = txnId,
            };
        }

        var result = await cityTasks.CompleteAsync(
            characterId, taskId, txnId, DateTimeOffset.UtcNow, context.CancellationToken);

        var response = new CompleteCityTaskResponse
        {
            Ok = result.Ok,
            Reason = result.Reason,
            Duplicate = string.Equals(result.Reason, "duplicate", StringComparison.Ordinal),
            TaskId = result.TaskId,
            CityId = result.CityId,
            Done = result.Done,
            MaxPerPeriod = result.MaxPerPeriod,
            PeriodKey = result.PeriodKey,
            TxnId = txnId,
            TaxPct = result.TaxPct,
        };

        foreach (var grant in result.Grants)
        {
            response.Grants.Add(new CityTaskGrantFact
            {
                Currency = grant.Currency,
                Amount = grant.Amount,
                BalanceBefore = grant.BalanceBefore,
                BalanceAfter = grant.BalanceAfter,
                TaxAmount = grant.TaxStone,
            });
        }

        return response;
    }
}
