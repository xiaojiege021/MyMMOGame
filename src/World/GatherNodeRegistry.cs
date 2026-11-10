namespace World;

/// <summary>
/// 采集节点的占用与刷新。按技术架构第 6 节 / 开发架构第 6 节，采集占用是可以在线的状态，
/// 只活在世界服进程内存，重进场景后重建，不写角色库，也不拿它当发奖依据。
/// 世界服占住节点之后才让角色服放产物；角色服那笔失败就立刻释放，不留下空占。
/// </summary>
public sealed class GatherNodeRegistry
{
    readonly object gate = new();
    readonly Dictionary<string, DateTimeOffset> busyUntil = new(StringComparer.Ordinal);

    /// <summary>抢节点。刷新时间没到就 false；时间从配置里的 respawn_s 读。</summary>
    public bool TryClaim(string nodeId, int respawnSeconds, DateTimeOffset now)
    {
        lock (gate)
        {
            if (busyUntil.TryGetValue(nodeId, out var until) && until > now)
            {
                return false;
            }

            busyUntil[nodeId] = now.AddSeconds(respawnSeconds);
            return true;
        }
    }

    /// <summary>上游发奖失败时把占用还回去，玩家可以马上重试。</summary>
    public void Release(string nodeId)
    {
        lock (gate)
        {
            busyUntil.Remove(nodeId);
        }
    }
}
