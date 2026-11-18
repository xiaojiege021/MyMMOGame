namespace Social;

/// <summary>
/// 队伍状态机的自检（Q7/H1 的 <c>--party-state</c>）。不连库、不起服，用注入的
/// 时钟把「邀请 / 接受 / 满员 / 准备 / 队长继任 / 解散 / 邀请超时 / 准备超时」
/// 一步步跑完，每步打一行 <c>party_step=</c>，核对脚本按行断言。
///
/// 用它的原因：队伍只在内存，真服段能验的只是同一条状态机；把状态机本身做成
/// 可重复的无库自检，异常分支（超时、拒绝码）不必每次都起三个服才能看到。
/// </summary>
public static class PartyProbe
{
    public static int Run(PartyTable table, DateTimeOffset start, Action<string> write)
    {
        var runtime = new PartyRuntime(table);
        var now = start;
        var failures = 0;
        var steps = 0;

        void Step(string name, bool ok, PartyRuntime.Result? result, string extra = "")
        {
            steps++;
            if (!ok)
            {
                failures++;
            }

            var p = result?.Party;
            write("party_step=" + name
                + " ok=" + (ok ? "true" : "false")
                + " reason=" + (result?.Reason ?? "")
                + " in_party=" + (p is not null ? "true" : "false")
                + " size=" + (p?.Size ?? 0)
                + " leader=" + (p?.LeaderCharacterId ?? 0)
                + " all_ready=" + (p?.AllReady == true ? "true" : "false")
                + (p is null ? "" : " ready=" + string.Join(",", p.Members.Select(
                    m => m.CharacterId + ":" + (m.Ready ? "1" : "0"))))
                + (extra.Length == 0 ? "" : " " + extra));
        }

        // 1. 邀请即建队：发起者成为队长，队里只有他一个人。
        var r = runtime.Invite(101, 102, now);
        Step("invite_creates_party", r.Ok && r.Party?.Size == 1 && r.Party?.LeaderCharacterId == 101, r);

        // 2. 同一个目标重复邀请是刷新超时，不是第二条邀请；换个人再邀也可以。
        var again = runtime.Invite(101, 102, now);
        var other = runtime.Invite(101, 103, now);
        Step("invite_refresh", again.Ok && other.Ok, other,
            "invites=" + runtime.Counts().Invites);

        // 3. 自己邀自己 / 目标已在队里 / 非队长邀请，三种都拒。
        var self = runtime.Invite(101, 101, now);
        Step("invite_self_rejected", !self.Ok && self.Reason == "target_invalid", self);

        // 4. 被邀请的人接受后进队，人数按事实增长。
        var joined = runtime.Respond(102, 101, true, now);
        Step("respond_accept", joined.Ok && joined.Party?.Size == 2, joined);

        var inParty = runtime.Invite(102, 104, now);
        Step("invite_not_leader", !inParty.Ok && inParty.Reason == "not_leader", inParty);

        // 队员去邀请队长：必须是 not_leader，不是 target_in_party——判顺序错了就会报成
        // 「目标已在队伍里」，客户端会提示成完全不相干的事（真库段抓到过）。
        var toLeader = runtime.Invite(102, 101, now);
        Step("invite_to_leader", !toLeader.Ok && toLeader.Reason == "not_leader", toLeader);

        var dupJoin = runtime.Invite(101, 102, now);
        Step("invite_target_in_party", !dupJoin.Ok && dupJoin.Reason == "target_in_party", dupJoin);

        // 5. 一直加满：上限只从配置读，多出来的那个人回 party_full。
        runtime.Respond(103, 101, true, now);
        runtime.Invite(101, 104, now);
        var full = runtime.Respond(104, 101, true, now);
        var overflow = runtime.Invite(101, 105, now);
        Step("party_full", full.Party?.Size == table.MaxSize
            && overflow.Reason == "party_full", overflow,
            "max=" + table.MaxSize + " size=" + full.Party?.Size);

        // 6. 没有发起准备检查时，确认无效。
        var noCheck = runtime.SetReady(102, true, now);
        Step("ready_check_missing", !noCheck.Ok && noCheck.Reason == "ready_check_missing", noCheck);

        // 7. 队长发起检查：所有人先清零，deadline 由配置的准备超时算。
        var started = runtime.StartReadyCheck(101, now);
        Step("ready_started", started.Ok && started.Party?.AllReady == false
            && started.Party?.ReadyDeadlineMs > 0, started,
            "deadline_ms=" + (started.Party?.ReadyDeadlineMs ?? 0));

        // 8. 差一个人都不算「队伍一致」。
        runtime.SetReady(101, true, now);
        runtime.SetReady(102, true, now);
        runtime.SetReady(103, true, now);
        var partial = runtime.SetReady(104, true, now);
        Step("ready_all", partial.Party?.AllReady == true, partial);

        // 9. 队长离队：按 earliest_joined 转给 102（第二早加入的），不是按角色号。
        var heir = runtime.Leave(101, now);
        Step("leader_succession", heir.Ok && heir.Party?.LeaderCharacterId == 102
            && heir.Party?.Size == table.MaxSize - 1, heir,
            "leader=" + (heir.Party?.LeaderCharacterId ?? 0));

        var leaveAfter = runtime.Leave(103, now);
        var leaveOne = runtime.Leave(104, now);
        var lastDisband = runtime.Disband(102, now);
        var gone = runtime.Describe(102, now);
        Step("disband", leaveAfter.Ok && leaveOne.Ok && lastDisband.Ok && gone is null, lastDisband,
            "size_after=" + (leaveOne.Party?.Size ?? 0));

        // 10. 邀请超时：过了配置的邀请超时再接受，回 invite_expired。
        runtime.Invite(101, 105, now);
        var late = runtime.Respond(105, 101, true, now.AddSeconds(table.InviteTimeoutSeconds + 1));
        Step("invite_expired", !late.Ok && late.Reason == "invite_expired", late,
            "timeout_s=" + table.InviteTimeoutSeconds);

        // 11. 准备超时：检查过了时就拒确认，队长要重新发起。
        runtime.Invite(101, 106, now);
        runtime.Respond(106, 101, true, now);
        runtime.StartReadyCheck(101, now);
        var expiredReady = runtime.SetReady(106, true,
            now.AddSeconds(table.ReadyCheckTimeoutSeconds + 1));
        Step("ready_expired", !expiredReady.Ok && expiredReady.Reason == "ready_check_expired",
            expiredReady, "timeout_s=" + table.ReadyCheckTimeoutSeconds);

        // 12. 拒绝邀请：邀请消失，队伍不动。
        runtime.Invite(101, 107, now);
        var declined = runtime.Respond(107, 101, false, now);
        var afterDecline = runtime.Respond(107, 101, true, now);
        Step("respond_decline", declined.Ok && declined.Reason == "declined"
            && afterDecline.Reason == "invite_not_found", declined,
            "size=" + (declined.Party?.Size ?? 0));

        write("party_state=" + (failures == 0 ? "ok" : "failed")
            + " steps=" + steps + " failures=" + failures
            + " parties=" + runtime.Counts().Parties
            + " members=" + runtime.Counts().Members);
        return failures == 0 ? 0 : 1;
    }
}
