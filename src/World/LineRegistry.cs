namespace World;

public sealed record EnterResult(
    bool Entered,
    string Reason,
    string LineId,
    string RegionId,
    int LineNo,
    int LineSize,
    int Capacity,
    bool Duplicate);

public sealed record LeaveResult(bool Left, string LineId, int LineSize);

public sealed record LineSnapshot(string LineId, string RegionId, int LineNo, int LineSize, int Capacity);

/// <summary>
/// 一条分线里某个人的当前坐标、朝向、上报序号，以及名字牌文本。
/// 名字由网关复核归属后从角色服取，服间传入，客户端帧里带不进。
/// </summary>
public sealed record AvatarState(
    ulong CharacterId, float X, float Y, float Z, float Facing, ulong Seq, string DisplayName);

public sealed record PositionReportResult(bool Accepted, string Reason, AvatarState? Corrected);

public sealed record LineMembers(string LineId, string RegionId, IReadOnlyList<AvatarState> Members);

/// <summary>
/// 分线与位置的唯一写入方。在线状态按技术架构第 3 / 开发架构第 2 节放进程内存，
/// 不写角色库。同一账号的同一角色重复进同一区域回同一条线，不另占名额；
/// 一条线满了才开下一条，最多几条线由配置定。离开只释放这个人的名额与位置。
/// 上报的位移在这里做服务端校验，不通过就保留上一次的合法位置。
/// </summary>
public sealed class LineRegistry(WorldConfig config, WorldCatalog catalog)
{
    readonly object gate = new();
    readonly Dictionary<string, Line> lines = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> seats = new(StringComparer.Ordinal);

    public EnterResult Enter(string account, ulong characterId, string regionId, string displayName)
    {
        var region = catalog.Find(regionId);
        if (region is null)
        {
            return Rejected("unknown_region");
        }

        if (!region.Open)
        {
            // 旧天城区与焦原第 11 场才开（方案 2.5）。这里只认配置，不读剧情进度。
            return Rejected("region_locked");
        }

        lock (gate)
        {
            var key = SeatKey(account, characterId);
            seats.TryGetValue(key, out var oldLineId);

            if (oldLineId is not null && lines[oldLineId].RegionId == regionId)
            {
                var same = lines[oldLineId];
                return Accepted(same, regionId, duplicate: true);
            }

            // 先找有位子的线，不够才新开；新开失败时不动这个人原来所在的线。
            var candidate = lines.Values
                .Where(l => l.RegionId == regionId && l.Members.Count < config.LineCapacity)
                .OrderBy(l => l.LineNo)
                .FirstOrDefault();

            if (candidate is null)
            {
                var existing = lines.Values.Where(l => l.RegionId == regionId).ToArray();
                if (existing.Length >= config.MaxLinesPerRegion)
                {
                    return Rejected("lines_full");
                }

                var no = existing.Length == 0 ? 1 : existing.Max(l => l.LineNo) + 1;
                candidate = new Line(regionId + "#" + no, regionId, no);
                lines.Add(candidate.Id, candidate);
            }

            if (oldLineId is not null)
            {
                lines[oldLineId].Members.Remove(key);
            }

            candidate.Members.Add(key, new Member(
                Spawn(characterId, (displayName ?? "").Trim()), DateTimeOffset.UtcNow));
            seats[key] = candidate.Id;
            return Accepted(candidate, regionId, duplicate: false);
        }
    }

    public LeaveResult Leave(string account, ulong characterId)
    {
        lock (gate)
        {
            var key = SeatKey(account, characterId);
            if (!seats.Remove(key, out var lineId))
            {
                return new LeaveResult(false, "", 0);
            }

            var line = lines[lineId];
            line.Members.Remove(key);
            return new LeaveResult(true, line.Id, line.Members.Count);
        }
    }

    public IReadOnlyList<LineSnapshot> Snapshot()
    {
        lock (gate)
        {
            return lines.Values
                .OrderBy(l => l.RegionId, StringComparer.Ordinal)
                .ThenBy(l => l.LineNo)
                .Select(l => new LineSnapshot(l.Id, l.RegionId, l.LineNo, l.Members.Count, config.LineCapacity))
                .ToArray();
        }
    }

    /// <summary>这个人当前在哪条分线。位置流用它决定推给谁，客户端不参与选择。</summary>
    public string? LineOf(string account, ulong characterId)
    {
        lock (gate)
        {
            return seats.TryGetValue(SeatKey(account, characterId), out var lineId) ? lineId : null;
        }
    }

    /// <summary>
    /// 这个人当前在哪块地表。采集用它核对「节点确实长在玩家所在的分线上」，
    /// 区域编号由分线注册表给，客户端请求里带不进来。
    /// </summary>
    public string? RegionOf(string account, ulong characterId)
    {
        lock (gate)
        {
            return seats.TryGetValue(SeatKey(account, characterId), out var lineId)
                   && lines.TryGetValue(lineId, out var line)
                ? line.RegionId
                : null;
        }
    }

    /// <summary>一次性取全部分线成员的位置，供广播节拍组装快照。</summary>
    public IReadOnlyList<LineMembers> MemberSnapshot()
    {
        lock (gate)
        {
            return lines.Values
                .Select(l => new LineMembers(l.Id, l.RegionId, l.Members.Values.Select(m => m.State).ToArray()))
                .ToArray();
        }
    }

    /// <summary>
    /// 客户端只上报自己的坐标与朝向。非有限值、瞬时超过配置速度上限都不通过，
    /// 回上一次合法位置，别人看到的仍是那一份；不做境界、飞行与技能判定。
    /// </summary>
    public PositionReportResult Report(
        string account, ulong characterId,
        float x, float y, float z, float facing, ulong seq, DateTimeOffset now)
    {
        lock (gate)
        {
            var key = SeatKey(account, characterId);
            if (!seats.TryGetValue(key, out var lineId) || !lines[lineId].Members.TryGetValue(key, out var member))
            {
                return new PositionReportResult(false, "not_in_line", null);
            }

            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(facing))
            {
                return new PositionReportResult(false, "invalid_position", member.State);
            }

            // 连续两帧挤在同一毫秒时，按一个广播周期给位移额度，不误伤正常上报。
            var elapsed = (now - member.UpdatedAt).TotalSeconds;
            var window = Math.Max(elapsed, 1.0 / config.SyncHz);
            var allowed = config.MaxMoveSpeed * window;
            var previous = member.State;
            var dx = x - previous.X;
            var dy = y - previous.Y;
            var dz = z - previous.Z;
            var distance = Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz);
            if (distance > allowed)
            {
                return new PositionReportResult(false, "speed_limit", previous);
            }

            member.State = new AvatarState(
                characterId, x, y, z, facing, seq, previous.DisplayName);
            member.UpdatedAt = now;
            return new PositionReportResult(true, "", member.State);
        }
    }

    AvatarState Spawn(ulong characterId, string displayName) => new(
        characterId,
        (float)config.SpawnX, (float)config.SpawnY, (float)config.SpawnZ,
        (float)config.SpawnFacing, 0, displayName);

    EnterResult Accepted(Line line, string regionId, bool duplicate) =>
        new(true, "", line.Id, regionId, line.LineNo, line.Members.Count, config.LineCapacity, duplicate);

    static EnterResult Rejected(string reason) => new(false, reason, "", "", 0, 0, 0, false);

    static string SeatKey(string account, ulong characterId) => account + ":" + characterId;

    sealed class Line(string id, string regionId, int lineNo)
    {
        public string Id { get; } = id;
        public string RegionId { get; } = regionId;
        public int LineNo { get; } = lineNo;
        public Dictionary<string, Member> Members { get; } = new(StringComparer.Ordinal);
    }

    sealed class Member(AvatarState state, DateTimeOffset updatedAt)
    {
        public AvatarState State { get; set; } = state;
        public DateTimeOffset UpdatedAt { get; set; } = updatedAt;
    }
}
