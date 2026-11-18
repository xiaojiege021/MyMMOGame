namespace Battle;

/// <summary>
/// Q8/H2 的离线自检：把分配算法的口径跑成可重复的用例，不连库、不起服、
/// 不读配置（需要确定结果的地方把掷点源换成固定值）。核对脚本按
/// <c>loot_step=&lt;名字&gt; ok=true</c> 逐条数。
///
/// 这里覆盖的是「服务端怎么分」：个人份额每人整份、需求优先贪婪、平手取小号、
/// 全放弃不发、未表态按贪婪、区间来自口径而不是代码。掷点本身用服务端随机源，
/// 断言只看事实（每行一个赢家、总入包行数等于掉落行数），不钉某次随机的结果。
/// 「队长不能私吞」不在这里验：<see cref="LootAllocator"/> 的入参里就没有 leader，
/// 结构上做不到，由静态断言钉住这一点。
/// </summary>
public static class LootSelfCheck
{
    public static int Run()
    {
        var failures = 0;
        void Step(string name, bool ok, string detail)
        {
            Console.WriteLine("loot_step=" + name + " ok=" + (ok ? "true" : "false")
                + " " + detail);
            if (!ok)
            {
                failures++;
            }
        }

        // 两行掉落。编号用中性标签：算法不看物品表，K4 也不许代码里出现真实物品编号。
        DropLineDef[] lines =
        [
            new("probe_item_a", 2),
            new("probe_item_b", 1),
        ];

        ulong[] members = [101, 102, 103];

        // ① personal：每人整份、没有公告（与 N6 的口径一字不差）。
        var personal = LootAllocator.Allocate(
            new LootScope(PartyLoot.Personal, PartyLoot.RollNone, 0, 0, members),
            members, lines, NoIntents());
        Step("personal_each_full",
            personal.Mode == PartyLoot.Personal
            && personal.Lines.Count == 0
            && members.All(id => personal.ByMember[id].Count == lines.Length),
            "mode=" + personal.Mode + " lines=" + personal.Lines.Count);

        // ② need_greed：每行只出一个赢家，总入包行数等于掉落行数（不双发）。
        var scope = new LootScope(PartyLoot.NeedGreed, PartyLoot.RollServer, 1, 100, members);
        var shared = LootAllocator.Allocate(scope, members, lines, NoIntents());
        var totalAwarded = members.Sum(id => shared.ByMember[id].Count);
        var winners = shared.Lines.Select(x => x.Winner).ToList();
        Step("need_greed_single_winner",
            shared.Mode == PartyLoot.NeedGreed
            && shared.Lines.Count == lines.Length
            && totalAwarded == lines.Length
            && winners.All(w => w != 0),
            "awarded=" + totalAwarded + " winners=" + string.Join(",", winners));

        // ③ 需求优先：只有一个人 need 时，greed 的人不参与。
        var intents = Intents((101, "probe_item_a", PartyLoot.IntentNeed));
        var needWins = LootAllocator.Allocate(scope, members, [lines[0]], intents, RollAlways(1));
        Step("need_beats_greed",
            needWins.Lines.Count == 1 && needWins.Lines[0].Winner == 101,
            "winner=" + (needWins.Lines.Count == 1 ? needWins.Lines[0].Winner : 0));

        // ④ 全员 pass：这一行不发给任何人（不会转手给队长或别人）。
        var allPass = Intents(
            (101, "probe_item_b", PartyLoot.IntentPass),
            (102, "probe_item_b", PartyLoot.IntentPass),
            (103, "probe_item_b", PartyLoot.IntentPass));
        var none = LootAllocator.Allocate(scope, members, [lines[1]], allPass);
        Step("all_pass_not_awarded",
            none.Lines.Count == 1 && none.Lines[0].Winner == 0
            && members.All(id => none.ByMember[id].Count == 0),
            "winner=" + (none.Lines.Count == 1 ? none.Lines[0].Winner : -1));

        // ⑤ 未表态按贪婪参与：没有意图表时每行仍有一个赢家（不是全员丢弃）。
        Step("untouched_means_greed",
            shared.Lines.All(x => x.Winner != 0),
            "winners=" + string.Join(",", shared.Lines.Select(x => x.Winner)));

        // ⑥ 平手取角色编号小的：点数恒定，赢家必须是 101（上报顺序打乱也一样）。
        var tie = LootAllocator.Allocate(scope, [103, 101, 102], [lines[0]], NoIntents(),
            RollAlways(50));
        Step("tie_lowest_id",
            tie.Lines.Count == 1 && tie.Lines[0].Winner == 101,
            "winner=" + (tie.Lines.Count == 1 ? tie.Lines[0].Winner : 0));

        // ⑦ 区间只从口径来：掷点源收到的上下界必须等于传入的 roll_min / roll_max。
        var seen = new List<(int Min, int Max)>();
        LootAllocator.Allocate(
            new LootScope(PartyLoot.NeedGreed, PartyLoot.RollServer, 7, 13, members),
            members, [lines[0]], NoIntents(),
            (min, max) =>
            {
                seen.Add((min, max));
                return min;
            });
        Step("range_from_scope",
            seen.Count > 0 && seen.All(x => x.Min == 7 && x.Max == 13),
            "seen=" + string.Join(",", seen.Select(x => x.Min + ".." + x.Max)));

        // ⑧ 单人 / 不在队伍：按 personal 发整份，不掷点。
        var solo = LootAllocator.Allocate(
            LootScope.Personal([101]), [101], lines, NoIntents());
        Step("solo_personal",
            solo.Mode == PartyLoot.Personal
            && solo.ByMember[101].Count == lines.Length
            && solo.Lines.Count == 0,
            "mode=" + solo.Mode + " lines=" + solo.Lines.Count);

        // ⑨ 表态词只认三个：认不出的整条忽略。
        Step("decision_words",
            LootAllocator.IsDecision(PartyLoot.IntentNeed)
            && LootAllocator.IsDecision(PartyLoot.IntentGreed)
            && LootAllocator.IsDecision(PartyLoot.IntentPass)
            && !LootAllocator.IsDecision("loot")
            && !LootAllocator.IsDecision(""),
            "need/greed/pass 之外一律不认");

        Console.WriteLine("loot_state=" + (failures == 0 ? "ok" : "fail")
            + " failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>没有人表态：全部按贪婪参与（算法内部的默认口径）。</summary>
    static Dictionary<ulong, IReadOnlyDictionary<string, string>> NoIntents() => [];

    static Dictionary<ulong, IReadOnlyDictionary<string, string>> Intents(
        params (ulong CharacterId, string ItemId, string Decision)[] entries)
    {
        var inner = new Dictionary<ulong, Dictionary<string, string>>();
        foreach (var (characterId, itemId, decision) in entries)
        {
            if (!inner.TryGetValue(characterId, out var mine))
            {
                mine = new Dictionary<string, string>(StringComparer.Ordinal);
                inner[characterId] = mine;
            }

            mine[itemId] = decision;
        }

        return inner.ToDictionary(
            x => x.Key, x => (IReadOnlyDictionary<string, string>)x.Value);
    }

    static Func<int, int, int> RollAlways(int value) => (_, _) => value;
}
