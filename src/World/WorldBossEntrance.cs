namespace World;

/// <summary>
/// 地表入口的在线状态：这一刻开没开、这一场是不是已经打掉、上一场把名字记在
/// 入口上的是哪个宗门。全是进程内存，不落库——技术架构第 6 节把在线状态放在进程
/// 内存里，归属只记名字、不是发奖依据，用它算不出任何属性、税收或矿脉。
///
/// 「开门」有两个条件：这一刻落在某个场次窗口里，且这一场还没被打掉（方案 10.4
/// 「被击杀或时间到，入口就关」）。时间到由窗口自己算出来，被击杀由战斗服报回来，
/// 两条都只有服务端事实值，客户端带不进「我想让入口开着」。
///
/// 同一场只记一次名字：场次已经记过就不再改——否则最后一下谁打得快谁就能把名字改掉。
/// 但「没打掉」可以升级成「打掉了」：一场里前一批人散场没打死、后一批人进来打死了，
/// 入口必须关。反过来永远不行。
/// </summary>
public sealed class WorldBossEntrance(WorldBossTable? table)
{
    /// <summary>只留最近这么多个场次的事实值，避免进程长跑攒垃圾。</summary>
    const int MaxWindows = 32;

    readonly object _gate = new();
    readonly List<string> _windowOrder = [];
    readonly Dictionary<string, WindowRecord> _windows = new(StringComparer.Ordinal);

    OwnerRecord _owner = new(0, "", "", 0);
    int _recordCount;
    int _killCount;

    public bool HasTable => table is not null;

    /// <summary>这一刻的入口事实值。表没配就恒为「没有入口」。</summary>
    public EntranceState Describe(DateTimeOffset now)
    {
        if (table is null)
        {
            return new EntranceState(
                false, "table_missing", "", "", "", "", 0, 0, 0, 0, false,
                new OwnerRecord(0, "", "", 0));
        }

        var window = table.WindowOf(now);
        lock (_gate)
        {
            var killed = window.Open && _windows.TryGetValue(window.Key, out var rec) && rec.Killed;
            var visible = window.Open && !killed;
            var reason = visible
                ? ""
                : window.Open ? "fought" : "not_open";

            return new EntranceState(
                visible, reason,
                table.EntranceId, table.EntranceName, table.RegionId,
                visible ? window.Key : "",
                visible ? window.StartsAtMs : 0,
                visible ? window.EndsAtMs : 0,
                visible ? 0 : window.NextStartsAtMs,
                table.Cap,
                killed,
                _owner);
        }
    }

    /// <summary>
    /// 战斗服交回的这一场事实值。打掉就把入口关掉，有宗门就记名字。
    /// 打掉只认第一次，名字只认第一个有名字的宗门；不写库、不改任何数值。
    /// </summary>
    public RecordOutcome Record(RecordFact fact)
    {
        if (table is null)
        {
            return new RecordOutcome(false, "table_missing", false, fact.WindowKey, _owner, false);
        }

        if (fact.EntranceId.Length == 0 || fact.WindowKey.Length == 0 || fact.TxnId.Length == 0)
        {
            return new RecordOutcome(false, "request_missing", false, fact.WindowKey, _owner, false);
        }

        if (!string.Equals(fact.EntranceId, table.EntranceId, StringComparison.Ordinal))
        {
            return new RecordOutcome(false, "wrong_entrance", false, fact.WindowKey, _owner, false);
        }

        if (fact.WinnerSectId != 0 && fact.WinnerSectName.Length == 0)
        {
            // 只记名字：给了宗门编号却没给名字，就不记，避免入口上出现一个空名。
            return new RecordOutcome(false, "sect_name_missing", false, fact.WindowKey, _owner, false);
        }

        lock (_gate)
        {
            _windows.TryGetValue(fact.WindowKey, out var current);
            current ??= new WindowRecord(false, 0, "");

            // 已经打掉的场次不再接受第二条，谁先报谁算。
            if (current.Killed)
            {
                return new RecordOutcome(true, "duplicate", true, fact.WindowKey, _owner, true);
            }

            if (!fact.Killed)
            {
                // 散场没打掉：这一场第一次报就记下来（含当时的名字），后面重复报不改。
                if (current.Recorded)
                {
                    return new RecordOutcome(true, "duplicate", true, fact.WindowKey, _owner, false);
                }

                var kept = Keep(current, fact);
                Remember(fact.WindowKey, kept);
                _recordCount++;
                return new RecordOutcome(true, "", false, fact.WindowKey, _owner, false);
            }

            var upgraded = Keep(current, fact) with { Killed = true };
            Remember(fact.WindowKey, upgraded);
            _recordCount++;
            _killCount++;
            return new RecordOutcome(true, "", false, fact.WindowKey, _owner, true);
        }
    }

    /// <summary>名字取第一个有名字的宗门：先记过就保留先记的，不被后来的覆盖。</summary>
    WindowRecord Keep(WindowRecord current, RecordFact fact)
    {
        if (current.Recorded && current.SectId != 0)
        {
            return current;
        }

        if (fact.WinnerSectId == 0)
        {
            return current with { Recorded = true };
        }

        _owner = new OwnerRecord(
            fact.WinnerSectId, fact.WinnerSectName, fact.WindowKey,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return new WindowRecord(current.Killed, fact.WinnerSectId, fact.WinnerSectName)
        {
            Recorded = true,
        };
    }

    void Remember(string windowKey, WindowRecord record)
    {
        if (!_windows.ContainsKey(windowKey))
        {
            _windowOrder.Add(windowKey);
            while (_windowOrder.Count > MaxWindows)
            {
                _windows.Remove(_windowOrder[0]);
                _windowOrder.RemoveAt(0);
            }
        }

        _windows[windowKey] = record;
    }

    /// <summary>已记过多少场。只给日志与核对看。</summary>
    public int RecordCount
    {
        get
        {
            lock (_gate)
            {
                return _recordCount;
            }
        }
    }

    /// <summary>已记过多少场是打掉的。只给日志与核对看。</summary>
    public int KillCount
    {
        get
        {
            lock (_gate)
            {
                return _killCount;
            }
        }
    }

    /// <summary>这一场的事实值（没报过就是默认值），只给只读入口与核对看。</summary>
    public WindowRecord WindowOf(string windowKey)
    {
        lock (_gate)
        {
            return _windows.TryGetValue(windowKey, out var rec) ? rec : new WindowRecord(false, 0, "");
        }
    }

    /// <summary>一场在内存里的事实值：打掉没打掉、名字是谁、报过没有。</summary>
    public sealed record WindowRecord(bool Killed, ulong SectId, string SectName)
    {
        /// <summary>这一场有没有被报过。没报过 = 还没有任何人交回过事实值。</summary>
        public bool Recorded { get; init; }
    }
}

/// <summary>入口这一刻的事实值。owner 是上一场（或这一场）记下的名字。</summary>
public sealed record EntranceState(
    bool Visible,
    string Reason,
    string EntranceId,
    string EntranceName,
    string RegionId,
    string WindowKey,
    long StartsAtMs,
    long EndsAtMs,
    long NextStartsAtMs,
    int Cap,
    bool Killed,
    OwnerRecord Owner);

/// <summary>记在入口上的归属：只有宗门编号、名字、是哪一场记的、什么时候记的。</summary>
public sealed record OwnerRecord(ulong SectId, string SectName, string WindowKey, long RecordedAtMs);

/// <summary>战斗服交回的事实值。没有奖励、没有加成、没有税率、没有矿脉。</summary>
public sealed record RecordFact(
    string TxnId, string EntranceId, string WindowKey, bool Killed,
    ulong WinnerSectId, string WinnerSectName, long DamageTotal);

/// <summary>世界服记完以后回给战斗服的只读事实值。</summary>
public sealed record RecordOutcome(
    bool Ok, string Reason, bool Duplicate, string WindowKey, OwnerRecord Owner, bool EntranceClosed);
