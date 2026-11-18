namespace Social;

/// <summary>
/// 队伍状态机（Q7/H1）。**只在社交服进程内存**：技术架构第 3 节把在线关系类
/// 临时态放在进程内存，本类不碰任何 schema、不发奖、不结算技能，社交服重启或
/// 全员掉线就散——队伍不是发奖依据，进本与结算只把它当「此刻的事实」。
///
/// 人数上限、邀请超时、准备超时、队长继任方式只从 <see cref="PartyTable"/> 读，
/// 代码里没有第二套数字。超时用**惰性过期**：每次读写先按传入的服务端时钟清理，
/// 不启后台 tick（可测，也不引入第二套时钟）。
///
/// 一条队伍只有一个队长；邀请权在队长。加入即 ready=false，队长发起准备检查后
/// 每个人各自确认，全员确认且未超时才算「队伍一致」——进本时角色服来读这个事实。
/// </summary>
public sealed class PartyRuntime(PartyTable table)
{
    readonly object _gate = new();

    /// <summary>一条队伍的成员快照。joined_seq 是加入顺序，队长继任用最小值。</summary>
    public sealed record MemberFact(ulong CharacterId, bool IsLeader, bool Ready, long JoinedSeq);

    /// <summary>队伍事实值。Size / AllReady / ReadyDeadline 都由服务端算，回包里带不进来。</summary>
    public sealed record Snapshot(
        ulong PartyId, ulong LeaderCharacterId, int Size, int MaxSize,
        bool AllReady, long ReadyDeadlineMs, IReadOnlyList<MemberFact> Members)
    {
        public ulong[] MemberIds()
        {
            var ids = new ulong[Members.Count];
            for (var i = 0; i < Members.Count; i++)
            {
                ids[i] = Members[i].CharacterId;
            }

            return ids;
        }
    }

    /// <summary>一次操作的结论。业务拒绝走 ok=false + reason，不是异常。</summary>
    public sealed record Result(bool Ok, string Reason, Snapshot? Party)
    {
        public static Result Fail(string reason) => new(false, reason, null);
    }

    sealed class Member(ulong characterId, long joinedSeq)
    {
        public ulong CharacterId { get; } = characterId;
        public long JoinedSeq { get; } = joinedSeq;
        public bool Ready { get; set; }
    }

    sealed class Party(ulong id, ulong leader)
    {
        public ulong Id { get; } = id;
        public ulong Leader { get; set; } = leader;
        public long ReadyDeadlineUnix { get; set; }

        /// <summary>上一次准备检查是不是已经超时。超时后确认不接受，要队长重新发起。</summary>
        public bool ReadyExpired { get; set; }
        public List<Member> Members { get; } = [new Member(leader, 1)];
    }

    sealed class PendingInvite(ulong partyId, ulong from, ulong to, DateTimeOffset expiresAt)
    {
        public ulong PartyId { get; } = partyId;
        public ulong From { get; } = from;
        public ulong To { get; } = to;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
    }

    readonly Dictionary<ulong, Party> _parties = [];
    readonly Dictionary<ulong, ulong> _partyOf = [];
    readonly List<PendingInvite> _invites = [];
    ulong _nextPartyId = 1;
    long _nextJoinedSeq = 2;

    public PartyTable Table => table;

    /// <summary>队长邀请一个人。发起者还没有队伍时，这一手同时把他建成队长。</summary>
    public Result Invite(ulong from, ulong target, DateTimeOffset now)
    {
        if (from == 0 || target == 0)
        {
            return Result.Fail("request_missing");
        }

        if (from == target)
        {
            return Result.Fail("target_invalid");
        }

        lock (_gate)
        {
            Prune(now);

            // 先判发起者：不在队伍就自己当队长，在队伍里但不是队长就 not_leader。
            // 顺序不能反：先判 target_in_party 会让「队员去邀请队长」报成 target_in_party
            // （Q7 真库段抓到过：B 邀请队长 CH 回的是 target_in_party）。
            Party party;
            if (_partyOf.TryGetValue(from, out var partyId))
            {
                party = _parties[partyId];
                if (party.Leader != from)
                {
                    return Result.Fail("not_leader");
                }
            }
            else
            {
                // 只有自己一个人也组不成队：上限 1 时不建空队、直接拒绝。
                if (table.MaxSize < 2)
                {
                    return Result.Fail("party_full");
                }

                party = NewParty(from);
            }

            if (_partyOf.ContainsKey(target))
            {
                return Result.Fail("target_in_party");
            }

            // 超时邀请先清掉：不然一条过期邀请会一直占着目标，别人再邀就撞 invite_pending。
            _invites.RemoveAll(i => i.ExpiresAt <= now);

            if (party.Members.Count >= table.MaxSize)
            {
                return Result.Fail("party_full");
            }

            // 同一个人重复邀请同一个目标：刷新超时，不叠第二条。
            var existing = FindInvite(from, target);
            if (existing is not null)
            {
                _invites.Remove(existing);
            }
            else if (_invites.Exists(i => i.To == target))
            {
                return Result.Fail("invite_pending");
            }

            _invites.Add(new PendingInvite(party.Id, from, target,
                now.AddSeconds(table.InviteTimeoutSeconds)));

            return new Result(true, "", DescribeLocked(from));
        }
    }

    /// <summary>被邀请的人接受或拒绝。拒绝只删邀请，队伍不动。</summary>
    public Result Respond(ulong target, ulong from, bool accept, DateTimeOffset now)
    {
        if (target == 0 || from == 0)
        {
            return Result.Fail("request_missing");
        }

        lock (_gate)
        {
            Prune(now);

            var invite = FindInvite(from, target);
            if (invite is null)
            {
                return Result.Fail("invite_not_found");
            }

            // 过期的邀请可能还挂在别人屏幕上：分开报，且这一条就地丢掉。
            if (invite.ExpiresAt <= now)
            {
                _invites.Remove(invite);
                return Result.Fail("invite_expired");
            }

            _invites.Remove(invite);

            if (!accept)
            {
                return new Result(true, "declined", DescribeLocked(target));
            }

            if (_partyOf.ContainsKey(target))
            {
                return Result.Fail("already_in_party");
            }

            if (!_parties.TryGetValue(invite.PartyId, out var party))
            {
                return Result.Fail("party_not_found");
            }

            if (party.Members.Count >= table.MaxSize)
            {
                return Result.Fail("party_full");
            }

            var member = new Member(target, _nextJoinedSeq++);
            party.Members.Add(member);
            _partyOf[target] = party.Id;
            // 新成员进来，之前那次「全员已准备」作废：准备是这一队人的事。
            ClearReadyLocked(party);

            return new Result(true, "", DescribeLocked(target));
        }
    }

    /// <summary>
    /// 离队。队长离队按配置的 leader_succession 处理：转给最早加入的成员，或整队解散；
    /// 最后一个人离队就是解散。
    /// </summary>
    public Result Leave(ulong characterId, DateTimeOffset now)
    {
        if (characterId == 0)
        {
            return Result.Fail("request_missing");
        }

        lock (_gate)
        {
            Prune(now);

            if (!_partyOf.TryGetValue(characterId, out var partyId))
            {
                return Result.Fail("not_in_party");
            }

            var party = _parties[partyId];
            var leaving = party.Members.Find(m => m.CharacterId == characterId)!;
            party.Members.Remove(leaving);
            _partyOf.Remove(characterId);

            if (party.Members.Count == 0)
            {
                DropPartyLocked(party);
                return new Result(true, "disbanded", null);
            }

            if (party.Leader == characterId)
            {
                if (table.LeaderSuccession == "disband")
                {
                    DropPartyLocked(party);
                    return new Result(true, "disbanded", null);
                }

                // earliest_joined：按加入顺序接手，不按角色号、不按客户端指定。
                var heir = party.Members[0];
                foreach (var m in party.Members)
                {
                    if (m.JoinedSeq < heir.JoinedSeq)
                    {
                        heir = m;
                    }
                }

                party.Leader = heir.CharacterId;
            }

            ClearReadyLocked(party);
            return new Result(true, "", DescribeLocked(party.Leader));
        }
    }

    /// <summary>解散。只有队长能解散；队长自己走用 Leave。</summary>
    public Result Disband(ulong characterId, DateTimeOffset now)
    {
        if (characterId == 0)
        {
            return Result.Fail("request_missing");
        }

        lock (_gate)
        {
            Prune(now);

            if (!_partyOf.TryGetValue(characterId, out var partyId))
            {
                return Result.Fail("not_in_party");
            }

            var party = _parties[partyId];
            if (party.Leader != characterId)
            {
                return Result.Fail("not_leader");
            }

            DropPartyLocked(party);
            return new Result(true, "disbanded", null);
        }
    }

    /// <summary>
    /// 队长发起准备检查：把所有人的 ready 清零，deadline = 现在 + 配置的准备超时。
    /// 重新发起会重置 deadline，也重置所有人的确认。
    /// </summary>
    public Result StartReadyCheck(ulong characterId, DateTimeOffset now)
    {
        if (characterId == 0)
        {
            return Result.Fail("request_missing");
        }

        lock (_gate)
        {
            Prune(now);

            if (!_partyOf.TryGetValue(characterId, out var partyId))
            {
                return Result.Fail("not_in_party");
            }

            var party = _parties[partyId];
            if (party.Leader != characterId)
            {
                return Result.Fail("not_leader");
            }

            ClearReadyLocked(party);
            party.ReadyDeadlineUnix = now.AddSeconds(table.ReadyCheckTimeoutSeconds)
                .ToUnixTimeMilliseconds();

            return new Result(true, "", DescribeLocked(characterId));
        }
    }

    /// <summary>成员确认 / 取消准备。没有进行中的检查、或检查已超时，都不接受。</summary>
    public Result SetReady(ulong characterId, bool ready, DateTimeOffset now)
    {
        if (characterId == 0)
        {
            return Result.Fail("request_missing");
        }

        lock (_gate)
        {
            Prune(now);

            if (!_partyOf.TryGetValue(characterId, out var partyId))
            {
                return Result.Fail("not_in_party");
            }

            var party = _parties[partyId];
            if (party.ReadyDeadlineUnix == 0)
            {
                return Result.Fail("ready_check_missing");
            }

            if (party.ReadyExpired)
            {
                return Result.Fail("ready_check_expired");
            }

            var member = party.Members.Find(m => m.CharacterId == characterId)!;
            member.Ready = ready;
            return new Result(true, "", DescribeLocked(characterId));
        }
    }

    /// <summary>只读：这个人在不在队伍里、队伍是什么样的。不在队伍回 null。</summary>
    public Snapshot? Describe(ulong characterId, DateTimeOffset now)
    {
        if (characterId == 0)
        {
            return null;
        }

        lock (_gate)
        {
            Prune(now);
            return DescribeLocked(characterId);
        }
    }

    /// <summary>核对用：内存里现在有多少队伍、多少条待接受邀请。</summary>
    public (int Parties, int Members, int Invites) Counts()
    {
        lock (_gate)
        {
            return (_parties.Count, _partyOf.Count, _invites.Count);
        }
    }

    Party NewParty(ulong leader)
    {
        var party = new Party(_nextPartyId++, leader);
        _parties[party.Id] = party;
        _partyOf[leader] = party.Id;
        return party;
    }

    void DropPartyLocked(Party party)
    {
        foreach (var m in party.Members)
        {
            _partyOf.Remove(m.CharacterId);
        }

        _invites.RemoveAll(i => i.PartyId == party.Id);
        _parties.Remove(party.Id);
    }

    void ClearReadyLocked(Party party)
    {
        foreach (var m in party.Members)
        {
            m.Ready = false;
        }

        party.ReadyDeadlineUnix = 0;
    }

    PendingInvite? FindInvite(ulong from, ulong to)
    {
        foreach (var i in _invites)
        {
            if (i.From == from && i.To == to)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// 惰性过期：准备检查到点就把确认清零，并把这一次标成「已超时」——deadline 留着，
    /// 让 <see cref="SetReady"/> 能分辨「没发起过」和「发起了但过了时」。
    /// 邀请的过期在邀请 / 应答路径上就地判，不在这里删。
    /// </summary>
    void Prune(DateTimeOffset now)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        foreach (var party in _parties.Values)
        {
            if (party.ReadyDeadlineUnix != 0 && party.ReadyDeadlineUnix <= nowMs)
            {
                foreach (var m in party.Members)
                {
                    m.Ready = false;
                }

                party.ReadyExpired = true;
            }
        }
    }

    Snapshot? DescribeLocked(ulong characterId)
    {
        if (!_partyOf.TryGetValue(characterId, out var partyId)
            || !_parties.TryGetValue(partyId, out var party))
        {
            return null;
        }

        var members = new List<MemberFact>(party.Members.Count);
        var allReady = party.Members.Count > 0;
        foreach (var m in party.Members)
        {
            members.Add(new MemberFact(m.CharacterId, m.CharacterId == party.Leader,
                m.Ready, m.JoinedSeq));
            if (!m.Ready)
            {
                allReady = false;
            }
        }

        return new Snapshot(party.Id, party.Leader, party.Members.Count, table.MaxSize,
            allReady && party.ReadyDeadlineUnix != 0 && !party.ReadyExpired,
            party.ReadyDeadlineUnix, members);
    }
}
