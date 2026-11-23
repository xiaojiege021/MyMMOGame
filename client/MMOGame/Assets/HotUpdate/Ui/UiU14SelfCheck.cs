#if UNITY_EDITOR
using System;
using System.Text;
using Mmo.Net;
using Mmo.Proto.Battle;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U14–U17 四屏自检：组队屏只认 `GET /party` 回包、掷点屏只公布 `LootAssigned` 的归属、
    /// 重连屏只按 `deserter_remaining_s` 走针、世界 Boss 入口屏只读 `/world/worldboss`，
    /// 四屏切皮肤只换颜色。
    /// </summary>
    public static class UiU14SelfCheck
    {
        static bool _failed;

        public static bool Run()
        {
            _failed = false;
            UiSkinStore.Reset();
            var shell = UiShell.Ensure();

            var party = UiPartyFlow.Ensure(shell);
            var social = UiSocialFlow.Ensure(shell);
            var loot = UiLootFlow.Ensure();
            var reconnect = UiReconnectFlow.Ensure();

            party.SetSender((path, body, token, done) => done(200, PartyJson));
            party.Bind("selftest-token", 9);
            social.SetSender((path, body, token, done) => done(200, BossJson));
            social.Bind("selftest-token", 9);

            // ---------------------------------------------------------- U14 组队

            // 一条请求都没发过：写「队伍名单还没到」，不拿自己凑一队。
            shell.ShowPage("组队", s => UiPartyRoomScreen.Build(s, party));
            var pending = Text(shell);
            Report("party_pending", pending.Contains(UiPartyCodex.EmptyList)
                && pending.IndexOf("角色 9", StringComparison.Ordinal) < 0);
            Debug.Log("u14_self_pending=" + (pending.Contains(UiPartyCodex.EmptyList) ? "empty" : "missing"));

            party.Load(shell.RefreshPage);
            shell.ShowPage("组队", s => UiPartyRoomScreen.Build(s, party));
            var list = Text(shell);
            Report("party_facts",
                party.Last.Size == 2 && party.Last.MaxSize == 4 && party.Last.InParty
                && party.Last.Members.Count == 2
                && list.Contains("角色 5") && list.Contains("角色 9")
                && list.Contains("队长") && list.Contains("已准备")
                && list.Contains("2 / 4 人"));
            Debug.Log("u14_self_facts size=" + party.Last.Size + "/" + party.Last.MaxSize
                + " members=" + party.Last.Members.Count);

            // 分配口径照抄回包：need_greed + 服务端掷点 + 区间；personal 走另一句。
            Report("party_loot_mode",
                list.Contains("全队一份") && list.Contains("服务端掷点") && list.Contains("1–100")
                && CharacterParty.LootLine(new PartyOutcome
                {
                    Ok = true, InParty = true, LootMode = "personal", LootRoll = "none",
                }).Contains("每人各发整份"));

            // ---------------------------------------------------------- U15 掷点

            var assigned = new LootAssigned { Mode = "need_greed" };
            assigned.Lines.Add(new LootAssignment { ItemId = "eq_1", Count = 1, WinnerCharacterId = 0 });
            assigned.Lines.Add(new LootAssignment { ItemId = "eq_2", Count = 2, WinnerCharacterId = 9 });
            loot.Apply(assigned);

            shell.ShowPage("掷点", s => UiLootScreen.Build(s, loot));
            var lootText = Text(shell);
            Report("loot_rows",
                loot.HasRows && loot.Rows.Count == 2 && loot.CountNobody() == 1
                && lootText.Contains(UiLootCodex.Nobody) && lootText.Contains("归角色 9")
                && lootText.Contains("eq_2 × 2")
                && lootText.Contains(UiLootCodex.Word(UiLootCodex.Need))
                && lootText.Contains(UiLootCodex.Word(UiLootCodex.Greed))
                && lootText.Contains(UiLootCodex.Word(UiLootCodex.Pass)));
            Debug.Log("u15_self_rows=" + loot.Rows.Count + " nobody=" + loot.CountNobody());

            // 第四个词发不出去：界面只有 need / greed / pass 三个词。
            Report("loot_three_words",
                !loot.Decide("eq_1", "roll") && !loot.Decide("eq_1", string.Empty)
                && UiLootCodex.Word(UiLootCodex.Need) == "需求");
            Debug.Log("u15_self_intents=" + loot.Intents);

            // ---------------------------------------------------------- U16 重连

            reconnect.SetFromRefusal(new EnterOutcome
            {
                Kind = EnterKind.Refused,
                Reason = "deserter_penalty",
                DeserterRemainingS = 42,
            });
            shell.ShowPage("重连", s => UiReconnectScreen.Build(s, reconnect));
            var countdown = Text(shell);
            Report("reconnect_fact", countdown.Contains("还剩 42 秒"));
            Debug.Log("u16_self_remaining=" + reconnect.Remaining);

            reconnect.Advance(41f);
            Report("reconnect_tick", reconnect.Remaining == 1
                && Text(shell).Contains("还剩 1 秒"));
            reconnect.Advance(1f);
            Report("reconnect_cleared", reconnect.Remaining == 0
                && UiReconnectCodex.PenaltyLine(reconnect.HasFact, reconnect.Remaining)
                    == UiReconnectCodex.Cleared);

            reconnect.Reset();
            Report("reconnect_no_fact",
                UiReconnectCodex.PenaltyLine(reconnect.HasFact, reconnect.Remaining)
                    == UiReconnectCodex.NoFact);

            // ---------------------------------------------------------- U17 世界 Boss

            social.LoadBoss(shell.RefreshPage);
            shell.ShowPage("世界 Boss", s => UiWorldBossScreen.Build(s, social));
            var boss = Text(shell);
            Report("boss_facts",
                social.HasBoss && social.LastBoss.Visible && !social.LastBoss.Killed
                && boss.Contains("荒原祭坛") && boss.Contains("已进 3 / 20")
                && boss.Contains("还能进 17") && boss.Contains("无人记名"));
            Debug.Log("u17_self_current=" + social.LastBoss.Current + "/" + social.LastBoss.Cap
                + " remaining=" + social.LastBoss.Remaining);

            social.SetSender((path, body, token, done) => done(200, BossKilledJson));
            social.LoadBoss(shell.RefreshPage);
            shell.ShowPage("世界 Boss", s => UiWorldBossScreen.Build(s, social));
            Report("boss_killed", Text(shell).Contains("这一场已打掉"));

            // ---------------------------------------------------------- 四屏切皮肤

            shell.ShowPage("组队", s => UiPartyRoomScreen.Build(s, party));
            var layout = Layout(shell);
            var copy = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", layout == Layout(shell));
            Report("skin_copy", copy == Text(shell));
            shell.SetSkin(UiSkin.Light, false);

            shell.ClosePage();
            UiSkinStore.Reset();
            UnityEngine.Object.DestroyImmediate(loot.gameObject);
            UnityEngine.Object.DestroyImmediate(reconnect.gameObject);
            UnityEngine.Object.DestroyImmediate(party.gameObject);
            UnityEngine.Object.DestroyImmediate(social.gameObject);
            UnityEngine.Object.DestroyImmediate(shell.gameObject);

            Debug.Log("u14_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("u14_self_" + key + "=" + (ok ? "ok" : "fail"));
            if (!ok)
            {
                _failed = true;
            }
        }

        static string Layout(UiShell shell)
        {
            var builder = new StringBuilder();
            var rects = shell.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                var rt = rects[i];
                builder.Append(rt.name).Append(':')
                    .Append(rt.anchoredPosition.x.ToString("0.##")).Append(',')
                    .Append(rt.anchoredPosition.y.ToString("0.##")).Append(',')
                    .Append(rt.sizeDelta.x.ToString("0.##")).Append(',')
                    .Append(rt.sizeDelta.y.ToString("0.##")).Append(';');
            }

            return builder.ToString();
        }

        static string Text(UiShell shell)
        {
            var builder = new StringBuilder();
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                builder.Append(labels[i].name).Append(':').Append(labels[i].text).Append(';');
            }

            return builder.ToString();
        }

        const string PartyJson =
            "{\"ok\":true,\"reason\":\"\",\"in_party\":true,\"party_id\":12," +
            "\"leader_character_id\":5,\"size\":2,\"max_size\":4,\"all_ready\":false," +
            "\"ready_deadline_ms\":0," +
            "\"members\":[{\"character_id\":5,\"is_leader\":true,\"ready\":true,\"joined_seq\":1}," +
            "{\"character_id\":9,\"is_leader\":false,\"ready\":false,\"joined_seq\":2}]," +
            "\"loot_mode\":\"need_greed\",\"loot_roll\":\"server\",\"roll_min\":1,\"roll_max\":100}";

        const string BossJson =
            "{\"visible\":true,\"reason\":\"\",\"entrance_id\":\"wb_1\",\"entrance_name\":\"荒原祭坛\"," +
            "\"window_key\":\"w1\",\"cap\":20,\"current\":3,\"remaining\":17," +
            "\"hold_s\":600,\"renew_s\":300,\"lost_s\":120,\"killed\":false," +
            "\"owner_sect_id\":0,\"owner_sect_name\":\"\",\"starts_at_ms\":0," +
            "\"ends_at_ms\":0,\"next_starts_at_ms\":0}";

        const string BossKilledJson =
            "{\"visible\":true,\"reason\":\"\",\"entrance_id\":\"wb_1\",\"entrance_name\":\"荒原祭坛\"," +
            "\"window_key\":\"w1\",\"cap\":20,\"current\":20,\"remaining\":0," +
            "\"hold_s\":600,\"renew_s\":300,\"lost_s\":120,\"killed\":true," +
            "\"owner_sect_id\":7,\"owner_sect_name\":\"云锦\",\"starts_at_ms\":0," +
            "\"ends_at_ms\":0,\"next_starts_at_ms\":0}";
    }
}
#endif
