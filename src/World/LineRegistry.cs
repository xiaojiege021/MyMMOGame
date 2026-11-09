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
/// 分线的唯一写入方。在线状态按技术架构第 3 / 开发架构第 2 节放进程内存，
/// 不写角色库。同一账号的同一角色重复进同一区域回同一条线，不另占名额；
/// 一条线满了才开下一条，最多几条线由配置定。离开只释放这个人的名额。
/// </summary>
public sealed class LineRegistry(WorldConfig config, WorldCatalog catalog)
{
    readonly object gate = new();
    readonly Dictionary<string, Line> lines = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> seats = new(StringComparer.Ordinal);

    public EnterResult Enter(string account, ulong characterId, string regionId)
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

            candidate.Members.Add(key);
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

    EnterResult Accepted(Line line, string regionId, bool duplicate) =>
        new(true, "", line.Id, regionId, line.LineNo, line.Members.Count, config.LineCapacity, duplicate);

    static EnterResult Rejected(string reason) => new(false, reason, "", "", 0, 0, 0, false);

    static string SeatKey(string account, ulong characterId) => account + ":" + characterId;

    sealed class Line(string id, string regionId, int lineNo)
    {
        public string Id { get; } = id;
        public string RegionId { get; } = regionId;
        public int LineNo { get; } = lineNo;
        public HashSet<string> Members { get; } = new(StringComparer.Ordinal);
    }
}
