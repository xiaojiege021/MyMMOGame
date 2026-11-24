namespace World;

/// <summary>
/// B5 摆摊的显示状态。按技术架构第 6 节，世界服只管在线状态：谁在哪儿摆、
/// 招牌叫什么、还挂着几件。全部放进程内存，重进场景后重建；不连角色库，
/// 也不改任何背包行。挂单、撤单与成交都在角色服，这里只收角色服回包的摘要。
///
/// 摊位不跨服：只按 region_id 分块显示，本进程就是本服，没有「去别的服查」这条路。
/// </summary>
public sealed class StallRegistry
{
    public sealed record Entry(
        string StallId,
        ulong OwnerId,
        string OwnerName,
        string RegionId,
        double PosX,
        double PosY,
        string Title,
        int LiveListings);

    readonly object gate = new();
    readonly Dictionary<string, Entry> stalls = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (gate)
            {
                return stalls.Count;
            }
        }
    }

    /// <summary>登记一个摊。同一摊位号重放就覆盖（摊主、位置以最新一次为准）。</summary>
    public void Open(Entry entry)
    {
        lock (gate)
        {
            stalls[entry.StallId] = entry;
        }
    }

    /// <summary>挂单 / 成交之后刷新在售件数。摊子不在（已关）就当没收到。</summary>
    public bool Renew(string stallId, int liveListings)
    {
        lock (gate)
        {
            if (!stalls.TryGetValue(stallId, out var entry))
            {
                return false;
            }

            stalls[stallId] = entry with { LiveListings = Math.Max(liveListings, 0) };
            return true;
        }
    }

    public bool Close(string stallId)
    {
        lock (gate)
        {
            return stalls.Remove(stallId);
        }
    }

    /// <summary>按地表列出在摊；region 为空串回本服全部。</summary>
    public List<Entry> List(string regionId)
    {
        lock (gate)
        {
            return regionId.Length == 0
                ? stalls.Values.OrderBy(e => e.StallId, StringComparer.Ordinal).ToList()
                : stalls.Values
                    .Where(e => string.Equals(e.RegionId, regionId, StringComparison.Ordinal))
                    .OrderBy(e => e.StallId, StringComparer.Ordinal)
                    .ToList();
        }
    }
}
