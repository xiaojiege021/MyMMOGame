using Grpc.Core;
using Mmo.Proto.Social;

namespace Social;

/// <summary>
/// 社交服 gRPC（P2-1a1）。校验都在服务端：人数上限、等级、人数都从
/// config/sect.json 读，请求里带不进来。业务前提不满足是正常回包里的
/// ok=false + reason，不是 gRPC 错误码；身份与参数错由网关在 HTTP 层挡。
/// </summary>
public sealed class SocialGrpcService(
    SectTable sectTable,
    SectRepository sects,
    SectFounderGrant founderGrant,
    HomesteadRuntime homes,
    HomesteadPlantRuntime plants,
    SectTaskTable taskTable,
    SectTaskRepository tasks,
    SectTaskLevelProbe levelProbe,
    PartyRuntime? parties) : SocialService.SocialServiceBase
{
    public override async Task<SectResponse> CreateSect(
        CreateSectRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var name = (request.Name ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || name.Length == 0)
        {
            return Fail("request_missing");
        }

        // 创建金币：经角色服一笔 grant_txn 事务，社交服不碰角色 schema。
        // founder_gold_cost 待定（方案未给数字），为 0 时这条链路不扣款。
        var charge = await founderGrant.ChargeAsync(
            account, request.CharacterId, txnId, sectTable, context.CancellationToken);
        if (!charge.Ok)
        {
            return Fail(charge.Reason);
        }

        if (charge.Duplicate)
        {
            return Fail("duplicate");
        }

        var created = await sects.CreateAsync(
            name, (long)request.CharacterId, txnId, context.CancellationToken);
        return new SectResponse
        {
            Ok = created.Ok,
            Reason = created.Reason,
            SectId = (ulong)created.SectId,
            Level = created.Level,
            MemberCap = created.MemberCap,
            MemberCount = created.MemberCount,
            Name = name,
            TxnId = txnId,
        };
    }

    public override async Task<SectResponse> JoinSect(
        JoinSectRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || request.SectId == 0)
        {
            return Fail("request_missing");
        }

        // 事务号由网关生成，只作核对；重复入册由 character_id 唯一约束挡。
        var joined = await sects.JoinAsync(
            (long)request.SectId, (long)request.CharacterId, context.CancellationToken);
        return new SectResponse
        {
            Ok = joined.Ok,
            Reason = joined.Reason,
            SectId = (ulong)joined.SectId,
            Level = joined.Level,
            MemberCap = joined.MemberCap,
            MemberCount = joined.MemberCount,
            TxnId = txnId,
        };
    }

    public override async Task<SectResponse> DescribeSect(
        DescribeSectRequest request, ServerCallContext context)
    {
        var state = request.SectId != 0
            ? await sects.StateAsync((long)request.SectId, context.CancellationToken)
            : await sects.StateByNameAsync((request.Name ?? "").Trim(), context.CancellationToken);

        if (!state.Found)
        {
            return new SectResponse { Ok = false, Reason = "sect_not_found" };
        }

        return new SectResponse
        {
            Ok = true,
            SectId = (ulong)state.SectId,
            Name = state.Name,
            Level = state.Level,
            MemberCap = state.MemberCap,
            MemberCount = state.MemberCount,
        };
    }

    // ---- P2-1c：只读查一个角色归属的宗门编号 ----
    // 建宗门的人不进名册，判定看 sect_member.sect_id 与 sect.master_character_id；
    // 两条都查不到才算没宗门。只回宗门编号，不写库。角色服据此判整队是否同宗门。
    public override async Task<DescribeCharacterSectResponse> DescribeCharacterSect(
        DescribeCharacterSectRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            return new DescribeCharacterSectResponse
            {
                Ok = false, Reason = "request_missing", Found = false, SectId = 0,
            };
        }

        var sect = await sects.CharacterSectAsync(
            (long)request.CharacterId, context.CancellationToken);
        return new DescribeCharacterSectResponse
        {
            Ok = true,
            Reason = sect.Found ? "" : "not_in_sect",
            Found = sect.Found,
            SectId = (ulong)sect.SectId,
        };
    }

    // ---- N2：名册逐条只读 ----
    // 只回角色号和职位。不任免、不改人数上限、不写库。
    public override async Task<DescribeSectRosterResponse> DescribeSectRoster(
        DescribeSectRosterRequest request, ServerCallContext context)
    {
        if (request.SectId == 0)
        {
            return new DescribeSectRosterResponse
            {
                Ok = false, Reason = "request_missing", SectId = 0,
            };
        }

        var roster = await sects.RosterAsync((long)request.SectId, context.CancellationToken);
        var reply = new DescribeSectRosterResponse
        {
            Ok = roster.Found,
            Reason = roster.Found ? "" : "sect_not_found",
            SectId = (ulong)roster.SectId,
        };
        foreach (var member in roster.Members)
        {
            reply.Members.Add(new SectRosterMember
            {
                CharacterId = (ulong)member.CharacterId,
                Position = member.Position ?? "",
            });
        }

        return reply;
    }

    // ---- P3-3a3：只读查一个角色所属宗门的科技等级 ----
    // 战斗服开城战时来读；社交服只回等级事实值，不算伤害、不给加成系数、不写库。
    // 没宗门 / 没科技行都给 0，上游不可达由战斗服降级处理，这里不抛 gRPC 错误。
    public override async Task<DescribeSectTechResponse> DescribeSectTech(
        DescribeSectTechRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            return new DescribeSectTechResponse
            {
                Ok = false, Reason = "request_missing", Found = false, SectId = 0, TechLevel = 0,
            };
        }

        var state = await sects.SectTechAsync(
            (long)request.CharacterId, context.CancellationToken);
        return new DescribeSectTechResponse
        {
            Ok = true,
            Reason = state.Found ? "" : "not_in_sect",
            Found = state.Found,
            SectId = (ulong)state.SectId,
            TechLevel = state.TechLevel,
        };
    }

    // ---- P2-1a2：任命 / 免职 / 只读查席位 ----
    // 席位数字只从 config/sect.json 读，请求里带不进来；回包都是只读事实值。
    public override async Task<PositionStateResponse> AppointPosition(
        PositionRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        if (!PositionFieldsOk(request, txnId))
        {
            return PositionFail("request_missing", request, txnId);
        }

        var outcome = await sects.AppointAsync(
            (long)request.SectId, (long)request.CharacterId,
            (long)request.TargetCharacterId, (request.Position ?? "").Trim(),
            context.CancellationToken);
        return PositionBody(outcome, (request.Position ?? "").Trim(), txnId);
    }

    public override async Task<PositionStateResponse> DismissPosition(
        PositionRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        if (!PositionFieldsOk(request, txnId))
        {
            return PositionFail("request_missing", request, txnId);
        }

        var outcome = await sects.DismissAsync(
            (long)request.SectId, (long)request.CharacterId,
            (long)request.TargetCharacterId, (request.Position ?? "").Trim(),
            context.CancellationToken);
        return PositionBody(outcome, (request.Position ?? "").Trim(), txnId);
    }

    public override async Task<PositionStateResponse> DescribePositions(
        PositionRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || request.SectId == 0)
        {
            return new PositionStateResponse
            {
                Ok = false, Reason = "request_missing", SectId = request.SectId, TxnId = txnId,
            };
        }

        var state = await sects.PositionStateAsync((long)request.SectId, context.CancellationToken);
        if (!state.Found)
        {
            return new PositionStateResponse
            {
                Ok = false, Reason = "sect_not_found", SectId = (ulong)state.SectId, TxnId = txnId,
            };
        }

        return new PositionStateResponse
        {
            Ok = true,
            SectId = (ulong)state.SectId,
            TxnId = txnId,
            MasterCount = state.Master,
            ViceMasterCount = state.ViceMaster,
            ElderCount = state.Elder,
            StewardCount = state.Steward,
            DiscipleCount = state.Disciple,
        };
    }

    // ---- P2-1a3：传位只给副掌门 / 退出宗门 / 只读查掌门状态 ----
    // 掌门权威、席位计数都只在社交服判定，请求里带不进来；回包都是只读事实值。
    // 业务拒绝是正常回包里的 ok=false + reason，未生效数值给 0。
    public override async Task<LeadershipResponse> TransferLeadership(
        LeadershipRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        if (!LeadershipFieldsOk(request, txnId) || request.TargetCharacterId == 0)
        {
            return LeadershipFail("request_missing", request.SectId, txnId);
        }

        var outcome = await sects.TransferAsync(
            (long)request.SectId, (long)request.CharacterId,
            (long)request.TargetCharacterId, txnId, context.CancellationToken);
        return LeadershipBody(outcome, txnId);
    }

    public override async Task<LeadershipResponse> LeaveSect(
        LeaveSectRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || request.SectId == 0)
        {
            return LeadershipFail("request_missing", request.SectId, txnId);
        }

        var outcome = await sects.LeaveAsync(
            (long)request.SectId, (long)request.CharacterId, context.CancellationToken);
        return LeadershipBody(outcome, txnId);
    }

    public override async Task<LeadershipResponse> DescribeLeadership(
        LeadershipRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        if (!LeadershipFieldsOk(request, txnId))
        {
            return LeadershipFail("request_missing", request.SectId, txnId);
        }

        var state = await sects.LeadershipStateAsync(
            (long)request.SectId, context.CancellationToken);
        if (!state.Found)
        {
            return LeadershipFail("sect_not_found", request.SectId, txnId);
        }

        return new LeadershipResponse
        {
            Ok = true,
            SectId = (ulong)state.SectId,
            OldMaster = (ulong)state.LastTransferOldMaster,
            NewMaster = (ulong)state.MasterCharacterId,
            MasterCount = state.MasterCount,
            ViceMasterCount = state.ViceMasterCount,
            MemberCount = state.MemberCount,
            TxnId = txnId,
        };
    }

    static bool LeadershipFieldsOk(LeadershipRequest r, string txnId) =>
        (r.Account ?? "").Trim().Length > 0 && r.CharacterId != 0 && txnId.Length > 0
        && r.SectId != 0;

    static LeadershipResponse LeadershipBody(LeadershipOutcome o, string txnId) =>
        new()
        {
            Ok = o.Ok,
            Reason = o.Reason,
            SectId = (ulong)o.SectId,
            OldMaster = o.Ok ? (ulong)o.OldMaster : 0,
            NewMaster = o.Ok ? (ulong)o.NewMaster : 0,
            MasterCount = o.Ok ? o.MasterCount : 0,
            ViceMasterCount = o.Ok ? o.ViceMasterCount : 0,
            MemberCount = o.Ok ? o.MemberCount : 0,
            TxnId = txnId,
        };

    static LeadershipResponse LeadershipFail(string reason, ulong sectId, string txnId) =>
        new() { Ok = false, Reason = reason, SectId = sectId, TxnId = txnId };

    static bool PositionFieldsOk(PositionRequest r, string txnId) =>
        (r.Account ?? "").Trim().Length > 0 && r.CharacterId != 0 && txnId.Length > 0
        && r.SectId != 0 && r.TargetCharacterId != 0 && (r.Position ?? "").Trim().Length > 0;

    static PositionStateResponse PositionBody(PositionOutcome o, string requested, string txnId) =>
        new()
        {
            Ok = o.Ok,
            Reason = o.Reason,
            SectId = (ulong)o.SectId,
            Position = o.Ok ? o.Position : requested,
            SeatCap = o.SeatCap,
            SeatCount = o.SeatCount,
            TxnId = txnId,
        };

    static PositionStateResponse PositionFail(string reason, PositionRequest r, string txnId) =>
        new()
        {
            Ok = false, Reason = reason, SectId = r.SectId,
            Position = (r.Position ?? "").Trim(), TxnId = txnId,
        };

    // ---- P2-1a4：升级只耗建设 / 只读查升级事实 ----
    // 升级消耗、目标等级、名册上限都只由社交服按 config/sect.json 与库里的事实判定，
    // 请求里带不进来；回包都是只读事实值，业务拒绝时未生效数值给 0。
    public override async Task<UpgradeResponse> UpgradeSect(
        UpgradeSectRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || request.SectId == 0)
        {
            return UpgradeFail("request_missing", request.SectId, txnId);
        }

        var outcome = await sects.UpgradeAsync(
            (long)request.SectId, (long)request.CharacterId, txnId, context.CancellationToken);
        return UpgradeBody(outcome, txnId);
    }

    public override async Task<UpgradeResponse> DescribeUpgrade(
        UpgradeSectRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || request.SectId == 0)
        {
            return UpgradeFail("request_missing", request.SectId, txnId);
        }

        var state = await sects.UpgradeStateAsync(
            (long)request.SectId, context.CancellationToken);
        if (!state.Found)
        {
            return UpgradeFail("sect_not_found", request.SectId, txnId);
        }

        // 只读：old/new 都填当前等级，建设前后都填当前值，cost 填下一档消耗（5 级为 0）。
        return new UpgradeResponse
        {
            Ok = true,
            SectId = (ulong)state.SectId,
            OldLevel = state.Level,
            NewLevel = state.Level,
            MemberCap = state.MemberCap,
            ConstructionBefore = state.Construction,
            ConstructionAfter = state.Construction,
            UpgradeCost = state.NextCost,
            TxnId = txnId,
        };
    }

    static UpgradeResponse UpgradeBody(UpgradeOutcome o, string txnId) =>
        new()
        {
            Ok = o.Ok,
            Reason = o.Reason,
            SectId = (ulong)o.SectId,
            OldLevel = o.Ok ? o.OldLevel : 0,
            NewLevel = o.Ok ? o.NewLevel : 0,
            MemberCap = o.Ok ? o.MemberCap : 0,
            ConstructionBefore = o.Ok ? o.ConstructionBefore : 0,
            ConstructionAfter = o.Ok ? o.ConstructionAfter : 0,
            UpgradeCost = o.Ok ? o.UpgradeCost : 0,
            TxnId = txnId,
        };

    static UpgradeResponse UpgradeFail(string reason, ulong sectId, string txnId) =>
        new() { Ok = false, Reason = reason, SectId = sectId, TxnId = txnId };

    static SectResponse Fail(string reason) => new() { Ok = false, Reason = reason };

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

    // ---- C3：洞府摆放（只加内容和配置，不改上面任何一条宗门规则） ----
    // 三个 RPC 都只回答「谁的哪一格摆了哪一件」这份展示事实：不消耗背包、
    // 不改角色库、不发家园币、不加属性、不结算技能。业务前提是正常回包里的
    // ok=false + reason，不是 gRPC 错误码；身份与参数错由网关在 HTTP 层挡。
    public override async Task<HomesteadResponse> PlaceHomeDecor(
        HomesteadRequest request, ServerCallContext context)
    {
        // proto 的 string 在生成物里可空，先收成局部串再判空，避免把 null 传进下游。
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        var slotId = (request.SlotId ?? "").Trim();
        var itemId = (request.ItemId ?? "").Trim();

        if (homes.Table is null || homes.Repository is null)
        {
            return HomesteadFail("homestead_unavailable", request.CharacterId,
                slotId, itemId, txnId);
        }

        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || slotId.Length == 0 || itemId.Length == 0)
        {
            return HomesteadFail("request_missing", request.CharacterId,
                slotId, itemId, txnId);
        }

        var outcome = await homes.Repository.PlaceAsync(
            account, request.CharacterId, txnId, slotId, itemId,
            context.CancellationToken);
        return HomesteadBody(outcome);
    }

    public override async Task<HomesteadResponse> RemoveHomeDecor(
        HomesteadRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        var slotId = (request.SlotId ?? "").Trim();

        if (homes.Table is null || homes.Repository is null)
        {
            return HomesteadFail("homestead_unavailable", request.CharacterId,
                slotId, "", txnId);
        }

        if (request.CharacterId == 0 || txnId.Length == 0 || slotId.Length == 0)
        {
            return HomesteadFail("request_missing", request.CharacterId,
                slotId, "", txnId);
        }

        var outcome = await homes.Repository.RemoveAsync(
            request.CharacterId, txnId, slotId, context.CancellationToken);
        return HomesteadBody(outcome);
    }

    public override async Task<HomesteadResponse> DescribeHomestead(
        DescribeHomesteadRequest request, ServerCallContext context)
    {
        if (homes.Table is null || homes.Repository is null)
        {
            return HomesteadFail("homestead_unavailable", request.OwnerCharacterId,
                "", "", "");
        }

        if (request.OwnerCharacterId == 0)
        {
            return HomesteadFail("request_missing", 0, "", "", "");
        }

        // 访客可见：任何有效令牌（网关认过）都能按洞府主人角色号只读查看。
        // account 只作日志上下文，不参与判定；回包不改任何状态。
        var rows = await homes.Repository.DescribeAsync(
            (long)request.OwnerCharacterId, context.CancellationToken);
        return HomesteadBody(new HomesteadOutcome(true, "", false,
            (long)request.OwnerCharacterId, "", "", "", rows));
    }

    HomesteadResponse HomesteadBody(HomesteadOutcome o)
    {
        var response = new HomesteadResponse
        {
            Ok = o.Ok,
            Reason = o.Reason,
            Duplicate = o.Duplicate,
            CharacterId = (ulong)o.CharacterId,
            SlotId = o.SlotId,
            ItemId = o.ItemId,
            TxnId = o.TxnId,
            SlotCount = homes.Table?.Slots.Count ?? 0,
        };
        foreach (var placement in o.Placements)
        {
            response.Placements.Add(new HomesteadSlotFact
            {
                SlotId = placement.SlotId,
                ItemId = placement.ItemId,
            });
        }

        return response;
    }

    static HomesteadResponse HomesteadFail(
        string reason, ulong characterId, string slotId, string itemId, string txnId) =>
        new()
        {
            Ok = false,
            Reason = reason,
            CharacterId = characterId,
            SlotId = slotId ?? "",
            ItemId = itemId ?? "",
            TxnId = txnId ?? "",
        };

    public override async Task<HomesteadPlantResponse> PlantHomesteadCrop(
        HomesteadPlantRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        var slotId = (request.SlotId ?? "").Trim();
        var cropId = (request.CropId ?? "").Trim();

        if (plants.Table is null || plants.Repository is null)
        {
            return PlantFail("plant_unavailable", request.CharacterId, slotId, cropId, txnId);
        }

        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || slotId.Length == 0 || cropId.Length == 0)
        {
            return PlantFail("request_missing", request.CharacterId, slotId, cropId, txnId);
        }

        var outcome = await plants.Repository.PlantAsync(
            account, request.CharacterId, txnId, slotId, cropId, context.CancellationToken);
        return PlantBody(outcome);
    }

    public override async Task<HomesteadPlantResponse> HarvestHomesteadCrop(
        HomesteadPlantRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        var slotId = (request.SlotId ?? "").Trim();

        if (plants.Table is null || plants.Repository is null)
        {
            return PlantFail("plant_unavailable", request.CharacterId, slotId, "", txnId);
        }

        if (account.Length == 0 || request.CharacterId == 0
            || txnId.Length == 0 || slotId.Length == 0)
        {
            return PlantFail("request_missing", request.CharacterId, slotId, "", txnId);
        }

        var outcome = await plants.Repository.HarvestAsync(
            account, request.CharacterId, txnId, slotId, context.CancellationToken);
        return PlantBody(outcome);
    }

    public override async Task<HomesteadPlantResponse> DescribeHomesteadPlants(
        DescribeHomesteadPlantsRequest request, ServerCallContext context)
    {
        if (plants.Table is null || plants.Repository is null)
        {
            return PlantFail("plant_unavailable", request.OwnerCharacterId, "", "", "");
        }

        if (request.OwnerCharacterId == 0)
        {
            return PlantFail("request_missing", 0, "", "", "");
        }

        var rows = await plants.Repository.DescribeAsync(
            (long)request.OwnerCharacterId, context.CancellationToken);
        return PlantBody(new HomesteadPlantOutcome(true, "", false,
            (long)request.OwnerCharacterId, "", "", "", rows, 0, 0));
    }

    HomesteadPlantResponse PlantBody(HomesteadPlantOutcome o)
    {
        var response = new HomesteadPlantResponse
        {
            Ok = o.Ok,
            Reason = o.Reason,
            Duplicate = o.Duplicate,
            CharacterId = (ulong)o.CharacterId,
            SlotId = o.SlotId,
            CropId = o.CropId,
            TxnId = o.TxnId,
            SlotCount = plants.Table?.Slots.Count ?? 0,
            GrantedHomeCoin = o.GrantedHomeCoin,
            HomeCoinBalance = o.HomeCoinBalance,
        };

        foreach (var row in o.Plants)
        {
            var def = plants.Table?.FindCrop(row.CropId);
            var ready = def is null
                ? row.PlantedAt.ToUnixTimeSeconds()
                : row.PlantedAt.AddSeconds(def.GrowSeconds).ToUnixTimeSeconds();
            response.Plants.Add(new HomesteadPlantFact
            {
                SlotId = row.SlotId,
                CropId = row.CropId,
                PlantedAtUnix = row.PlantedAt.ToUnixTimeSeconds(),
                ReadyAtUnix = ready,
                Mature = row.Mature,
            });
        }

        return response;
    }

    static HomesteadPlantResponse PlantFail(
        string reason, ulong characterId, string slotId, string cropId, string txnId) =>
        new()
        {
            Ok = false,
            Reason = reason,
            CharacterId = characterId,
            SlotId = slotId ?? "",
            CropId = cropId ?? "",
            TxnId = txnId ?? "",
        };

    // ---- Q7/H1 队伍 ---------------------------------------------------------
    // 状态只在社交服内存，写操作由网关生成事务号（只作核对，不挡重放：重发邀请
    // 是刷新超时）。人数上限、两个超时、队长继任方式只由 PartyTable 判，
    // 请求里带不进人数、队长、deadline。

    public override Task<PartyResponse> InviteParty(
        PartyInviteRequest request, ServerCallContext context)
    {
        // Q7：缺 party.json 时运行时为 null，七个队伍 RPC 一律回 party_unavailable，
        // 宗门 / 洞府 / 任务那批老 RPC 一条不受影响。
        if (parties is null)
        {
            return Task.FromResult(PartyFail("party_unavailable", request.TxnId ?? ""));
        }

        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0 || txnId.Length == 0
            || request.TargetCharacterId == 0)
        {
            return Task.FromResult(PartyFail("request_missing", txnId));
        }

        var result = parties.Invite(
            request.CharacterId, request.TargetCharacterId, DateTimeOffset.UtcNow);
        Log("party_invite", account, request.CharacterId, result, txnId,
            "target=" + request.TargetCharacterId);
        return Task.FromResult(ToPartyResponse(result, txnId));
    }

    public override Task<PartyResponse> RespondPartyInvite(
        PartyRespondRequest request, ServerCallContext context)
    {
        // Q7：缺 party.json 时运行时为 null，七个队伍 RPC 一律回 party_unavailable，
        // 宗门 / 洞府 / 任务那批老 RPC 一条不受影响。
        if (parties is null)
        {
            return Task.FromResult(PartyFail("party_unavailable", request.TxnId ?? ""));
        }

        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0 || txnId.Length == 0
            || request.FromCharacterId == 0)
        {
            return Task.FromResult(PartyFail("request_missing", txnId));
        }

        var result = parties.Respond(
            request.CharacterId, request.FromCharacterId, request.Accept, DateTimeOffset.UtcNow);
        Log("party_respond", account, request.CharacterId, result, txnId,
            "from=" + request.FromCharacterId + " accept=" + (request.Accept ? "true" : "false"));
        return Task.FromResult(ToPartyResponse(result, txnId));
    }

    public override Task<PartyResponse> StartPartyReadyCheck(
        PartyRequest request, ServerCallContext context)
    {
        // Q7：缺 party.json 时运行时为 null，七个队伍 RPC 一律回 party_unavailable，
        // 宗门 / 洞府 / 任务那批老 RPC 一条不受影响。
        if (parties is null)
        {
            return Task.FromResult(PartyFail("party_unavailable", request.TxnId ?? ""));
        }

        if (!PartyRequestOk(request.Account, request.CharacterId, request.TxnId, out var account))
        {
            return Task.FromResult(PartyFail("request_missing", request.TxnId ?? ""));
        }

        var result = parties.StartReadyCheck(request.CharacterId, DateTimeOffset.UtcNow);
        Log("party_ready_start", account, request.CharacterId, result, request.TxnId ?? "", "");
        return Task.FromResult(ToPartyResponse(result, request.TxnId ?? ""));
    }

    public override Task<PartyResponse> SetPartyReady(
        PartyReadyRequest request, ServerCallContext context)
    {
        // Q7：缺 party.json 时运行时为 null，七个队伍 RPC 一律回 party_unavailable，
        // 宗门 / 洞府 / 任务那批老 RPC 一条不受影响。
        if (parties is null)
        {
            return Task.FromResult(PartyFail("party_unavailable", request.TxnId ?? ""));
        }

        if (!PartyRequestOk(request.Account, request.CharacterId, request.TxnId, out var account))
        {
            return Task.FromResult(PartyFail("request_missing", request.TxnId ?? ""));
        }

        var result = parties.SetReady(
            request.CharacterId, request.Ready, DateTimeOffset.UtcNow);
        Log("party_ready", account, request.CharacterId, result, request.TxnId ?? "",
            "ready=" + (request.Ready ? "true" : "false"));
        return Task.FromResult(ToPartyResponse(result, request.TxnId ?? ""));
    }

    public override Task<PartyResponse> LeaveParty(
        PartyRequest request, ServerCallContext context)
    {
        // Q7：缺 party.json 时运行时为 null，七个队伍 RPC 一律回 party_unavailable，
        // 宗门 / 洞府 / 任务那批老 RPC 一条不受影响。
        if (parties is null)
        {
            return Task.FromResult(PartyFail("party_unavailable", request.TxnId ?? ""));
        }

        if (!PartyRequestOk(request.Account, request.CharacterId, request.TxnId, out var account))
        {
            return Task.FromResult(PartyFail("request_missing", request.TxnId ?? ""));
        }

        var result = parties.Leave(request.CharacterId, DateTimeOffset.UtcNow);
        Log("party_leave", account, request.CharacterId, result, request.TxnId ?? "", "");
        return Task.FromResult(ToPartyResponse(result, request.TxnId ?? ""));
    }

    public override Task<PartyResponse> DisbandParty(
        PartyRequest request, ServerCallContext context)
    {
        // Q7：缺 party.json 时运行时为 null，七个队伍 RPC 一律回 party_unavailable，
        // 宗门 / 洞府 / 任务那批老 RPC 一条不受影响。
        if (parties is null)
        {
            return Task.FromResult(PartyFail("party_unavailable", request.TxnId ?? ""));
        }

        if (!PartyRequestOk(request.Account, request.CharacterId, request.TxnId, out var account))
        {
            return Task.FromResult(PartyFail("request_missing", request.TxnId ?? ""));
        }

        var result = parties.Disband(request.CharacterId, DateTimeOffset.UtcNow);
        Log("party_disband", account, request.CharacterId, result, request.TxnId ?? "", "");
        return Task.FromResult(ToPartyResponse(result, request.TxnId ?? ""));
    }

    /// <summary>
    /// 只读。角色服在进场与结算前调它取「此刻的队伍事实」；这里不校验 account，
    /// 因为服间直连没有账号，判定只看角色号在不在队伍里。不在队伍时 in_party=false。
    /// </summary>
    public override Task<PartyResponse> DescribeParty(
        PartyRequest request, ServerCallContext context)
    {
        // Q7：缺 party.json 时运行时为 null，七个队伍 RPC 一律回 party_unavailable，
        // 宗门 / 洞府 / 任务那批老 RPC 一条不受影响。
        if (parties is null)
        {
            return Task.FromResult(PartyFail("party_unavailable", request.TxnId ?? ""));
        }

        if (request.CharacterId == 0)
        {
            return Task.FromResult(PartyFail("request_missing", request.TxnId ?? ""));
        }

        var snapshot = parties.Describe(request.CharacterId, DateTimeOffset.UtcNow);
        var response = PartySnapshotResponse(snapshot, request.TxnId ?? "");
        Console.WriteLine("party_describe character=" + request.CharacterId
            + " in_party=" + (response.InParty ? "true" : "false")
            + " party=" + response.PartyId
            + " size=" + response.Size
            + " leader=" + response.LeaderCharacterId
            + " all_ready=" + (response.AllReady ? "true" : "false"));
        return Task.FromResult(response);
    }

    static bool PartyRequestOk(string? account, ulong characterId, string? txnId, out string trimmed)
    {
        trimmed = (account ?? "").Trim();
        return trimmed.Length > 0 && characterId != 0 && (txnId ?? "").Trim().Length > 0;
    }

    static PartyResponse PartyFail(string reason, string txnId) => new()
    {
        Ok = false,
        Reason = reason,
        TxnId = txnId ?? "",
        InParty = false,
    };

    static PartyResponse ToPartyResponse(PartyRuntime.Result result, string txnId)
    {
        var response = PartySnapshotResponse(result.Party, txnId);
        response.Ok = result.Ok;
        response.Reason = result.Reason;
        return response;
    }

    static PartyResponse PartySnapshotResponse(PartyRuntime.Snapshot? snapshot, string txnId)
    {
        var response = new PartyResponse
        {
            Ok = snapshot is not null,
            Reason = snapshot is null ? "not_in_party" : "",
            TxnId = txnId ?? "",
            InParty = snapshot is not null,
        };

        if (snapshot is null)
        {
            return response;
        }

        response.PartyId = snapshot.PartyId;
        response.LeaderCharacterId = snapshot.LeaderCharacterId;
        response.Size = snapshot.Size;
        response.MaxSize = snapshot.MaxSize;
        response.AllReady = snapshot.AllReady;
        response.ReadyDeadlineMs = snapshot.ReadyDeadlineMs;
        foreach (var m in snapshot.Members)
        {
            response.Members.Add(new PartyMemberFact
            {
                CharacterId = m.CharacterId,
                IsLeader = m.IsLeader,
                Ready = m.Ready,
                JoinedSeq = m.JoinedSeq,
            });
        }

        return response;
    }

    static void Log(string what, string account, ulong characterId,
        PartyRuntime.Result result, string txnId, string extra)
    {
        Console.WriteLine(what + " account=" + account
            + " id=" + characterId
            + (extra.Length == 0 ? "" : " " + extra)
            + " txn=" + txnId
            + " ok=" + (result.Ok ? "true" : "false")
            + " reason=" + result.Reason
            + " party=" + (result.Party?.PartyId ?? 0)
            + " size=" + (result.Party?.Size ?? 0)
            + " leader=" + (result.Party?.LeaderCharacterId ?? 0)
            + " all_ready=" + (result.Party?.AllReady == true ? "true" : "false"));
    }
}
