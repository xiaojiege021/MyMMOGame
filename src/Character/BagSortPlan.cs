namespace Character;

/// <summary>
/// 排序的落位计划：先把「哪一行挪到哪一格」算成一条安全序列，再交给仓储一条条改库。
///
/// 抽成纯函数是为了能脱库核对：链条、纯环、满格这三种走法只读代码看不出来，
/// 而它们决定「会不会撞 (character_id, slot) 的唯一索引」。
/// 计划保证每一步的目标格在那一刻都是空的；有环又没有空格时回 null（调用方回 bag_full）。
/// </summary>
public static class BagSortPlan
{
    /// <summary>一步：把 id 这一行挪到 dest 格。</summary>
    public readonly record struct Step(long Id, int Dest);

    /// <summary>
    /// 算落位顺序。rows 是当前占用的 (行 id, 格号)，targetOf 是每个行 id 排完后该在的格号，
    /// freeSlots 是可当周转位的空格（升序）。
    /// </summary>
    public static List<Step>? Plan(
        IReadOnlyList<(long Id, int Slot)> rows,
        IReadOnlyDictionary<long, int> targetOf,
        IReadOnlyList<int> freeSlots)
    {
        var steps = new List<Step>(rows.Count);
        var slotOf = new Dictionary<long, int>(rows.Count);
        var idAtSlot = new Dictionary<int, long>(rows.Count);
        foreach (var row in rows)
        {
            slotOf[row.Id] = row.Slot;
            idAtSlot[row.Slot] = row.Id;
        }

        var tempSlot = 0;

        void Put(long id, int dest)
        {
            steps.Add(new Step(id, dest));
            idAtSlot.Remove(slotOf[id]);
            slotOf[id] = dest;
            idAtSlot[dest] = id;
        }

        // 按行 id 升序走，结果与输入顺序无关，核对好写。
        var ordered = new List<long>(slotOf.Keys);
        ordered.Sort();

        foreach (var start in ordered)
        {
            if (slotOf[start] == targetOf[start])
            {
                continue;
            }

            // 顺着「谁的格子被我要去」一路找：走到底要么是一个空格，要么回到起点（纯环）。
            var chain = new List<long>();
            var cursor = start;
            var cycle = false;
            while (true)
            {
                chain.Add(cursor);
                var dest = targetOf[cursor];
                if (!idAtSlot.TryGetValue(dest, out var occupant))
                {
                    break;
                }

                if (occupant == start)
                {
                    cycle = true;
                    break;
                }

                cursor = occupant;
            }

            if (!cycle)
            {
                // 倒着落位：最后一件先进它那个空格，前面的一件接一件补进刚腾出来的格。
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    Put(chain[i], targetOf[chain[i]]);
                }

                continue;
            }

            if (tempSlot == 0)
            {
                foreach (var free in freeSlots)
                {
                    if (!idAtSlot.ContainsKey(free))
                    {
                        tempSlot = free;
                        break;
                    }
                }

                if (tempSlot == 0)
                {
                    return null;
                }
            }

            Put(chain[0], tempSlot);
            for (var i = chain.Count - 1; i >= 1; i--)
            {
                Put(chain[i], targetOf[chain[i]]);
            }

            Put(chain[0], targetOf[chain[0]]);
        }

        return steps;
    }
}
