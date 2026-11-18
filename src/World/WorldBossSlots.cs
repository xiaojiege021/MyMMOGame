using Mmo.Shared.Domain;

namespace World;

/// <summary>
/// 第一层容量：这个人此刻站在哪条分线上。世界 Boss 只认「入口所在地表的那条分线」，
/// 客户端带不进分线号与人数；这里只读分线注册表，不另存一份在线状态。
/// </summary>
public interface IEntranceLine
{
    LineStanding Where(ulong characterId);
}

/// <summary>一个人站在哪条线、这条线现在几个人、上限几个。人数与上限由分线注册表给。</summary>
public sealed record LineStanding(bool OnEntranceRegion, string LineKey, int Occupancy, int Capacity);

/// <summary>
/// 世界 Boss 一场的名额（Q11）。名额由这里一串发，战斗服的连接数只当第二道保险：
/// 抢名额发生在连上战斗服之前，所以不会出现「一百个人同时连进来再判满」。
///
/// 三层事实值都在进程内存（技术架构第 6 节）：
///   1. 窗口：这一刻是哪一场，由同一份表 + 同一份日历表算出；
///   2. 分线：人必须在入口所在地表的一条分线上，这条线的站立人数不超上限；
///   3. 场次：有效占位数不超 cap。
///
/// 只记「谁还占着这一场的位置」：没有任何奖励、属性、税率、矿脉、宗门加成字段。
/// 拿到名额后 hold_s 内没进战斗服就回收；进场后按 renew_s 续，lost_s 没续判失联。
/// </summary>
public sealed class WorldBossSlots(
    WorldBossTable? table, CalendarTable calendar, WorldBossEntrance entrance, IEntranceLine lines)
{
    readonly object _gate = new();
    readonly Dictionary<ulong, Slot> _slots = new();

    int _granted;
    int _renewed;
    int _released;
    int _expired;
    int _rejectedFull;
    int _rejectedLine;

    /// <summary>
    /// 要一个名额。窗口关着、这一场已经打掉、人不在入口那条分线、这条线满了、
    /// 这一场名额发完了，五种情况都回 ok=false 加一个 reason，一个位置都不占。
    /// 同一个角色在同一场重复要名额是幂等的，只把到期时刻往后推。
    /// </summary>
    public SlotOutcome Hold(ulong characterId, DateTimeOffset now)
    {
        if (table is null)
        {
            return Reject("table_missing", "");
        }

        if (characterId == 0)
        {
            return Reject("request_missing", "");
        }

        var window = table.WindowOf(now, calendar);
        if (!window.Open)
        {
            return Reject("worldboss_closed", "");
        }

        lock (_gate)
        {
            Sweep(now, window.Key);

            if (entrance.WindowOf(window.Key).Killed)
            {
                return Reject("worldboss_fought", window.Key);
            }

            if (_slots.TryGetValue(characterId, out var mine)
                && string.Equals(mine.WindowKey, window.Key, StringComparison.Ordinal))
            {
                var kept = mine with { ExpiresAtMs = Expiry(now, mine.Entered) };
                _slots[characterId] = kept;
                return Accept(kept, duplicate: true, lines.Where(characterId));
            }

            var standing = lines.Where(characterId);
            if (!standing.OnEntranceRegion)
            {
                // 不在入口所在地表的分线上：绕开表世界直接连战斗服也进不来。
                return Reject("worldboss_not_on_entrance_line", window.Key);
            }

            if (standing.Occupancy > standing.Capacity)
            {
                _rejectedLine++;
                return Reject("worldboss_line_full", window.Key, standing);
            }

            if (CountLocked(window.Key) >= table.Cap)
            {
                _rejectedFull++;
                return Reject("worldboss_full", window.Key, standing);
            }

            var slot = new Slot(window.Key, standing.LineKey, Expiry(now, entered: false), false);
            _slots[characterId] = slot;
            _granted++;
            return Accept(slot, duplicate: false, standing);
        }
    }

    /// <summary>
    /// 续约。entered=true 表示这条连接已经在战斗服里了，之后按 renew_s 续；
    /// 还没进去时按 hold_s 续（客户端重试连战斗服不会因为慢慢重试就丢名额）。
    /// 名额已经过期或被回收就回 slot_missing，战斗服据此把人挡在门外。
    /// </summary>
    public SlotOutcome Renew(ulong characterId, bool entered, DateTimeOffset now)
    {
        if (table is null)
        {
            return Reject("table_missing", "");
        }

        var window = table.WindowOf(now, calendar);
        if (!window.Open)
        {
            return Reject("worldboss_closed", "");
        }

        lock (_gate)
        {
            Sweep(now, window.Key);

            if (!_slots.TryGetValue(characterId, out var slot))
            {
                return Reject("slot_missing", window.Key);
            }

            if (!string.Equals(slot.WindowKey, window.Key, StringComparison.Ordinal))
            {
                return Reject("window_changed", window.Key);
            }

            var renewed = slot with
            {
                Entered = slot.Entered || entered,
                ExpiresAtMs = Expiry(now, slot.Entered || entered),
            };
            _slots[characterId] = renewed;
            _renewed++;
            return Accept(renewed, duplicate: true);
        }
    }

    /// <summary>离场 / 散场 / 掉线超时：把名额还回去。没占过也回 ok=false，但不动别人的。</summary>
    public SlotOutcome Release(ulong characterId, string reason, DateTimeOffset now)
    {
        if (table is null)
        {
            return Reject("table_missing", "");
        }

        var window = table.WindowOf(now, calendar);
        lock (_gate)
        {
            if (!_slots.Remove(characterId, out var slot))
            {
                return Reject("slot_missing", window.Key);
            }

            _released++;
            return new SlotOutcome(
                true, reason.Length == 0 ? "" : reason, false, slot.WindowKey,
                slot.ExpiresAtMs, CountLocked(slot.WindowKey), table.Cap,
                slot.LineKey, 0, 0);
        }
    }

    /// <summary>这一刻的名额事实值。只给 DescribeWorldBoss 与只读入口看。</summary>
    public SlotFacts Describe(DateTimeOffset now)
    {
        if (table is null)
        {
            return new SlotFacts(0, 0, 0, 0, 0, 0, "");
        }

        var window = table.WindowOf(now, calendar);
        lock (_gate)
        {
            Sweep(now, window.Key);
            var current = window.Open ? CountLocked(window.Key) : 0;
            return new SlotFacts(
                current, Math.Max(0, table.Cap - current), table.Cap,
                table.Hold.HoldSeconds, table.Hold.RenewSeconds, table.Hold.LostSeconds,
                window.Open ? window.Key : "");
        }
    }

    /// <summary>只给只读入口与核对看：发过多少、续过多少、回收过多少、挡过多少。</summary>
    public SlotCounters Counters
    {
        get
        {
            lock (_gate)
            {
                return new SlotCounters(_granted, _renewed, _released, _expired, _rejectedFull, _rejectedLine);
            }
        }
    }

    /// <summary>清掉某一场的全部名额：散场、打掉、整场结束都走这里。</summary>
    public int ClearWindow(string windowKey)
    {
        lock (_gate)
        {
            var gone = _slots.Where(p => string.Equals(p.Value.WindowKey, windowKey, StringComparison.Ordinal))
                .Select(p => p.Key).ToArray();
            foreach (var key in gone)
            {
                _slots.Remove(key);
            }

            _released += gone.Length;
            return gone.Length;
        }
    }

    long Expiry(DateTimeOffset now, bool entered) => now.ToUnixTimeMilliseconds()
        + (entered ? table!.Hold.RenewSeconds : table!.Hold.HoldSeconds) * 1000L;

    int CountLocked(string windowKey) =>
        _slots.Count(p => string.Equals(p.Value.WindowKey, windowKey, StringComparison.Ordinal));

    /// <summary>
    /// 惰性回收：过期名额在这里掉，不在别处偷偷留着。同时把不在当前场次的名额清掉——
    /// 窗口一换，上一场的名额就没有意义了，留着只会让下一场少一个位置。
    /// </summary>
    void Sweep(DateTimeOffset now, string windowKey)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        List<ulong>? drop = null;
        foreach (var (characterId, slot) in _slots)
        {
            var stale = slot.ExpiresAtMs <= nowMs
                || (windowKey.Length > 0 && !string.Equals(slot.WindowKey, windowKey, StringComparison.Ordinal));
            if (!stale)
            {
                continue;
            }

            (drop ??= []).Add(characterId);
        }

        if (drop is null)
        {
            return;
        }

        foreach (var characterId in drop)
        {
            _slots.Remove(characterId);
            _expired++;
        }
    }

    SlotOutcome Reject(string reason, string windowKey, LineStanding? standing = null) =>
        new(false, reason, false, windowKey, 0,
            windowKey.Length == 0 ? 0 : CountLocked(windowKey), table?.Cap ?? 0,
            standing?.LineKey ?? "", standing?.Occupancy ?? 0, standing?.Capacity ?? 0);

    SlotOutcome Accept(
        Slot slot, bool duplicate, LineStanding? standing = null) =>
        new(true, "", duplicate, slot.WindowKey, slot.ExpiresAtMs, CountLocked(slot.WindowKey), table!.Cap,
            slot.LineKey, standing?.Occupancy ?? 0, standing?.Capacity ?? 0);

    sealed record Slot(string WindowKey, string LineKey, long ExpiresAtMs, bool Entered);
}

/// <summary>一次占位 / 续约 / 释放的结果。拒绝时 current 是这一刻已占的名额数。</summary>
public sealed record SlotOutcome(
    bool Ok, string Reason, bool Duplicate, string WindowKey, long ExpiresAtMs,
    int Current, int Cap, string LineKey, int LineOccupancy, int LineCapacity);

/// <summary>这一刻的名额事实值。</summary>
public sealed record SlotFacts(
    int Current, int Remaining, int Cap, int HoldSeconds, int RenewSeconds, int LostSeconds, string WindowKey);

/// <summary>占位表的累计计数，只给日志与核对看。</summary>
public sealed record SlotCounters(
    int Granted, int Renewed, int Released, int Expired, int RejectedFull, int RejectedLine);
