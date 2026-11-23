using Grpc.Core;
using Mmo.Proto.Social;

namespace Social;

public sealed partial class SocialGrpcService
{
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
        return Task.FromResult(ToPartyResponse(result, txnId, parties.LootRule));
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
        return Task.FromResult(ToPartyResponse(result, txnId, parties.LootRule));
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
        return Task.FromResult(ToPartyResponse(result, request.TxnId ?? "", parties.LootRule));
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
        return Task.FromResult(ToPartyResponse(result, request.TxnId ?? "", parties.LootRule));
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
        return Task.FromResult(ToPartyResponse(result, request.TxnId ?? "", parties.LootRule));
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
        return Task.FromResult(ToPartyResponse(result, request.TxnId ?? "", parties.LootRule));
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
        var response = PartySnapshotResponse(snapshot, request.TxnId ?? "", parties.LootRule);
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

    static PartyResponse ToPartyResponse(PartyRuntime.Result result, string txnId, PartyTable.Loot loot)
    {
        var response = PartySnapshotResponse(result.Party, txnId, loot);
        response.Ok = result.Ok;
        response.Reason = result.Reason;
        return response;
    }

    static PartyResponse PartySnapshotResponse(
        PartyRuntime.Snapshot? snapshot, string txnId, PartyTable.Loot loot)
    {
        var response = new PartyResponse
        {
            Ok = snapshot is not null,
            Reason = snapshot is null ? "not_in_party" : "",
            TxnId = txnId ?? "",
            InParty = snapshot is not null,
            // Q8：分配口径由社交服按 config/party.json 给事实值，客户端只用来显示；
            // 不在队伍时也照配置回（角色服据此按 personal 发），判定不在这一层。
            LootMode = loot.Mode,
            LootRoll = loot.Roll,
            RollMin = loot.RollMin,
            RollMax = loot.RollMax,
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
