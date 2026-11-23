using System.Diagnostics;
using Mmo.Proto.Battle;
using Mmo.Proto.Character;
using TalismanPool = Mmo.Proto.Character.TalismanPool;

namespace Battle;

// 14.8 D3c：Encounter 按域拆出的分片，纯搬代码。
public sealed partial class Encounter
{
    /// <summary>
    /// Q8/H2：本场的掉落表态（角色编号 → 物品编号 → need / greed / pass）。
    /// 只活在这一场内存里；结算时用一次就随这场丢掉，不落库、不进角色服。
    /// 离场不清：打完就断线的人投过的票仍然算。
    /// </summary>
    readonly Dictionary<ulong, Dictionary<string, string>> _lootIntents = [];

    /// <summary>
    /// need_greed 下的分配结果（结算时填一次，Server 在 Ended 之后广播）。
    /// personal 为 null：每人各发整份，没有「谁拿了哪件」可公布。
    /// </summary>
    public LootAllocation? LootResult { get; private set; }

    /// <summary>
    /// 收一条掉落表态。物品编号为空或表态词认不出就整条忽略（不改已有表态、不报错）。
    /// 同一件重复表态以后到的覆盖先到的——客户端重发不该被算成两次。
    /// 结算之后不再接受（结算完的表态改不了已经分完的件）。
    /// </summary>
    public void SetLootIntent(ulong characterId, string itemId, string decision)
    {
        var id = (itemId ?? "").Trim();
        var word = (decision ?? "").Trim();
        if (characterId == 0 || id.Length == 0 || !LootAllocator.IsDecision(word))
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (!_lootIntents.TryGetValue(characterId, out var mine))
            {
                mine = new Dictionary<string, string>(StringComparer.Ordinal);
                _lootIntents[characterId] = mine;
            }

            mine[id] = word;
        }
    }

    /// <summary>结算前复制一份表态（接收线程还在写，不能把字典直接交给分配）。</summary>
    Dictionary<ulong, IReadOnlyDictionary<string, string>> SnapshotLootIntents()
    {
        lock (_gate)
        {
            var copy = new Dictionary<ulong, IReadOnlyDictionary<string, string>>(_lootIntents.Count);
            foreach (var (characterId, mine) in _lootIntents)
            {
                copy[characterId] = new Dictionary<string, string>(mine, StringComparer.Ordinal);
            }

            return copy;
        }
    }
}
