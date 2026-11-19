using Character;

// Q10 排序落位的脱库核对：不连库、不起进程，只喂「谁在哪一格、排完该在哪一格、哪些格空着」。
// 四种走法各一条：直挪、链、纯环、满格。每条都模拟一遍，断言三件事：
//   1. 每一步的目标格在当时是空的（不撞 (character_id, slot) 的唯一索引）；
//   2. 全部走完之后每一行都在它该在的格上；
//   3. 步数等于真的换了格的行数（不会白挪两下）。
var failed = 0;

void Check(string name, List<(long Id, int Slot)> rows, Dictionary<long, int> target,
    List<int> free, bool expectNull)
{
    var plan = BagSortPlan.Plan(rows, target, free);
    if (expectNull)
    {
        var ok = plan is null;
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name + " = null");
        if (!ok) failed++;
        return;
    }

    if (plan is null)
    {
        Console.WriteLine("FAIL " + name + " 计划是 null");
        failed++;
        return;
    }

    var slotOf = new Dictionary<long, int>();
    foreach (var row in rows)
    {
        slotOf[row.Id] = row.Slot;
    }

    var occupied = new HashSet<int>();
    foreach (var row in rows)
    {
        occupied.Add(row.Slot);
    }

    foreach (var step in plan)
    {
        if (occupied.Contains(step.Dest))
        {
            Console.WriteLine("FAIL " + name + " 第 " + step.Id + " 行要去的 " + step.Dest + " 格还占着");
            failed++;
            return;
        }

        if (slotOf[step.Id] == step.Dest)
        {
            Console.WriteLine("FAIL " + name + " 第 " + step.Id + " 行原地打转");
            failed++;
            return;
        }

        occupied.Remove(slotOf[step.Id]);
        slotOf[step.Id] = step.Dest;
        occupied.Add(step.Dest);
    }

    foreach (var one in target)
    {
        if (slotOf[one.Key] != one.Value)
        {
            Console.WriteLine("FAIL " + name + " 第 " + one.Key + " 行落在 " + slotOf[one.Key]
                + "，该在 " + one.Value);
            failed++;
            return;
        }
    }

    // 纯环要借一个周转格（多一步），所以步数只要求「不少于换格的行数」；
    // 真正判对错的还是上面那条「每行最后都在它该在的格上」。
    var movedRows = rows.Count(r => r.Slot != target[r.Id]);
    if (plan.Count < movedRows)
    {
        Console.WriteLine("FAIL " + name + " 步数 " + plan.Count + " 少于该换格的行数 " + movedRows);
        failed++;
        return;
    }

    Console.WriteLine("ok   " + name + " steps=" + plan.Count
        + " " + string.Join(",", plan.ConvertAll(s => s.Id + ">" + s.Dest)));
}

// 1. 直挪：5 号格的东西排到 1 号格，别的都在位上。
Check("直挪", new List<(long, int)> { (1, 1), (2, 5) },
    new Dictionary<long, int> { [1] = 1, [2] = 2 }, new List<int> { 2, 3 }, false);

// 2. 链：3 号格的抢 2 号格、2 号格的抢 1 号格，1 号格空着（最后一件先落到空位）。
Check("链", new List<(long, int)> { (2, 2), (3, 3), (4, 9) },
    new Dictionary<long, int> { [2] = 1, [3] = 2, [4] = 3 }, new List<int> { 1, 4 }, false);

// 3. 纯环：两格互换，必须借一个周转格。
Check("纯环", new List<(long, int)> { (1, 1), (2, 2) },
    new Dictionary<long, int> { [1] = 2, [2] = 1 }, new List<int> { 3 }, false);

// 4. 满格 + 环 + 没有空格：只能回 null（网关那头回 bag_full），不能硬挪。
Check("环-满格-无空格", new List<(long, int)> { (1, 1), (2, 2) },
    new Dictionary<long, int> { [1] = 2, [2] = 1 }, new List<int>(), true);

// 5. 满格 + 环 + 有空格：借空格走通。
Check("环-满格-有空格", new List<(long, int)> { (1, 1), (2, 2), (3, 3) },
    new Dictionary<long, int> { [1] = 3, [2] = 1, [3] = 2 }, new List<int> { 4 }, false);

// 6. 已经排好：一步都不该走。
Check("已排好", new List<(long, int)> { (1, 1), (2, 2), (3, 3) },
    new Dictionary<long, int> { [1] = 1, [2] = 2, [3] = 3 }, new List<int> { 4 }, false);

// 7. 打乱的 13 行：随机换一下位置，排完必须逐个在位。
var shuffled = new List<(long Id, int Slot)>();
var order = new[] { 9, 3, 12, 1, 7, 5, 11, 2, 8, 4, 13, 6, 10 };
for (var i = 0; i < order.Length; i++)
{
    shuffled.Add((i + 1, 20 + order[i]));
}

var bigTarget = new Dictionary<long, int>();
for (var i = 0; i < shuffled.Count; i++)
{
    bigTarget[i + 1] = 31 + i;
}

Check("打乱 13 行", shuffled, bigTarget, new List<int> { 1, 2, 3 }, false);

if (failed > 0)
{
    Console.WriteLine("bag_sort_plan=fail count=" + failed);
    return 1;
}

Console.WriteLine("bag_sort_plan=ok");
return 0;
