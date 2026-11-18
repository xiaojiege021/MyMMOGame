using System.Security.Cryptography;

namespace Battle;

/// <summary>
/// 这一场按哪套口径分掉落（Q8/H2）。事实值来自角色服的 DescribeLootScope，
/// 最终来自社交服按队伍配置给的规则——**战斗服不读队伍配置**，
/// 也不信客户端在 URL 上自报的任何分配参数（那会把 personal 报成 need_greed
/// 之类的口径骗出更多份）。
/// </summary>
public sealed record LootScope(
    string Mode, string Roll, int RollMin, int RollMax, IReadOnlyList<ulong> MemberIds)
{
    /// <summary>
    /// 单人 / 社交服不可达时的口径：personal（默认，每人各发整份）。
    /// 区间给 0——personal 不掷点；数字只在队伍配置里，代码里不写死。
    /// </summary>
    public static LootScope Personal(IReadOnlyList<ulong> members) =>
        new(PartyLoot.Personal, PartyLoot.RollNone, 0, 0, members);
}

/// <summary>一行掉落的归属。Winner=0 表示这一行没人要（全员放弃 / 没有参与者）。</summary>
public sealed record LootLineResult(string ItemId, int Count, ulong Winner);

/// <summary>
/// 一次分配的结果：每人应得的那几行（交给 SettleParty 逐人入包）+ 逐行公告（need_greed 才广播）。
/// personal 时 ByMember 里每个人都是整份、公告为空。
/// </summary>
public sealed record LootAllocation(
    string Mode,
    IReadOnlyDictionary<ulong, IReadOnlyList<DropLineDef>> ByMember,
    IReadOnlyList<LootLineResult> Lines);

/// <summary>
/// 队伍掉落分配（Q8/H2）。纯函数：没有网络、没有配置读取、没有时钟，
/// 输入是服务端事实（成员、掉落行、玩家表态）与一个可替换的掷点源，输出是每人应得的那几行。
///
/// 口径（方案只写定「默认个人份额 / 掷点只在服务端 / 同一件不双发 / 队长不能私吞」）：
///   · personal：每人各发整份，不掷点。老路径一个数字都不变。
///   · need_greed：全队只出这一份。每行先看有没有人 need；有人 need 就只有 need 的人参与，
///     否则 greed 与「没表态的」参与（没表态按 greed 算，避免没点界面的人白丢件）。
///     参与的人各掷一次 [RollMin, RollMax]，最高者得整行，平手取角色编号小的；
///     全员 pass 的那一行不发放（公告 Winner=0），不会转手给任何人。
///   · 同一件不双发：每行只写进一个赢家的背包，行与行之间互不影响。
///   · 队长不能私吞：这一层没有队长入参——结构上就做不到「队长优先 / 队长指定赢家」。
/// </summary>
public static class LootAllocator
{
    /// <summary>掷点源：给定闭区间回一个点数。默认用服务端密码学随机。</summary>
    public static LootAllocation Allocate(
        LootScope scope,
        IReadOnlyList<ulong> members,
        IReadOnlyList<DropLineDef> lines,
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>> intents,
        Func<int, int, int>? roll = null)
    {
        var participants = Normalize(members);
        var byMember = new Dictionary<ulong, List<DropLineDef>>();
        foreach (var id in participants)
        {
            byMember[id] = [];
        }

        if (lines.Count == 0)
        {
            return new LootAllocation(
                PartyLoot.Personal,
                byMember.ToDictionary(x => x.Key, x => (IReadOnlyList<DropLineDef>)x.Value),
                []);
        }

        // 不是 need_greed 一律按 personal：赔率上更宽的那一档只能由配置显式打开。
        if (!string.Equals(scope.Mode, PartyLoot.NeedGreed, StringComparison.Ordinal)
            || participants.Count == 0)
        {
            foreach (var id in participants)
            {
                byMember[id].AddRange(lines);
            }

            return new LootAllocation(
                PartyLoot.Personal,
                byMember.ToDictionary(x => x.Key, x => (IReadOnlyList<DropLineDef>)x.Value),
                []);
        }

        roll ??= DefaultRoll;
        var min = scope.RollMin;
        var max = scope.RollMax;
        var results = new List<LootLineResult>(lines.Count);

        foreach (var line in lines)
        {
            var needed = new List<ulong>();
            var greedy = new List<ulong>();
            foreach (var id in participants)
            {
                switch (Decision(intents, id, line.ItemId))
                {
                    case PartyLoot.IntentNeed:
                        needed.Add(id);
                        break;
                    case PartyLoot.IntentPass:
                        break;
                    default:
                        greedy.Add(id);
                        break;
                }
            }

            var pool = needed.Count > 0 ? needed : greedy;
            if (pool.Count == 0)
            {
                results.Add(new LootLineResult(line.ItemId, line.Count, 0));
                continue;
            }

            var winner = 0UL;
            var best = int.MinValue;
            foreach (var id in pool)
            {
                var value = roll(min, max);
                // 平手取角色编号小的：同一场重复结算不会因为顺序不同换人。
                if (value > best || (value == best && id < winner))
                {
                    best = value;
                    winner = id;
                }
            }

            byMember[winner].Add(line);
            results.Add(new LootLineResult(line.ItemId, line.Count, winner));
        }

        return new LootAllocation(
            PartyLoot.NeedGreed,
            byMember.ToDictionary(x => x.Key, x => (IReadOnlyList<DropLineDef>)x.Value),
            results);
    }

    /// <summary>本场参与分配的人：去重、剔掉 0、保持上报顺序。</summary>
    public static List<ulong> Normalize(IReadOnlyList<ulong> members)
    {
        var seen = new HashSet<ulong>();
        var list = new List<ulong>(members.Count);
        foreach (var id in members)
        {
            if (id != 0 && seen.Add(id))
            {
                list.Add(id);
            }
        }

        return list;
    }

    /// <summary>一个表态词是否合法。非法的整条忽略（不记进意图表）。</summary>
    public static bool IsDecision(string? decision) =>
        decision is PartyLoot.IntentNeed or PartyLoot.IntentGreed or PartyLoot.IntentPass;

    static string Decision(
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>> intents,
        ulong characterId, string itemId)
    {
        // 没表态按贪婪算：看不到界面 / 没来得及点的人也能参与，不会白丢。
        if (intents.TryGetValue(characterId, out var mine)
            && mine.TryGetValue(itemId, out var decision))
        {
            return decision;
        }

        return PartyLoot.IntentGreed;
    }

    static int DefaultRoll(int min, int max) =>
        max <= min ? min : RandomNumberGenerator.GetInt32(min, max + 1);
}
