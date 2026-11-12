#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U5 七屏自检。Verifies：回包里的等级 / 费用 / 镶嵌状态原样上屏；被拒时未生效的数值
    /// 保持回包给的 0；七屏都能建起来；切皮肤只换颜色。
    ///
    /// 回包靠替换发送实现喂进去，不连网关。
    /// </summary>
    public static class UiUpgradeSelfCheck
    {
        static bool _failed;

        static int _enhanceCalls;

        public static bool Run()
        {
            _failed = false;
            _enhanceCalls = 0;

            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            var flow = UiUpgradeFlow.Ensure(shell);
            flow.SetSender((path, body, token, done) =>
            {
                if (path.IndexOf("/inventory", StringComparison.Ordinal) >= 0)
                {
                    done(200, BagJson);
                    return;
                }

                if (path.IndexOf("/character/enhance", StringComparison.Ordinal) >= 0)
                {
                    _enhanceCalls++;
                    done(200, _enhanceCalls == 1 ? EnhanceOkJson : EnhanceRejectJson);
                    return;
                }

                if (path.IndexOf("/character/transfer", StringComparison.Ordinal) >= 0)
                {
                    done(200, TransferRejectJson);
                    return;
                }

                done(200, CrystalRejectJson);
            });

            flow.Bind("selftest-token", 7);
            flow.LoadBag();
            Report("bag", flow.HasBag && flow.Bag.Slots.Count == 2
                && flow.Bag.CharacterName == "自检角色" && flow.Bag.Realm == "炼体");
            Debug.Log("u5_self_bag slots=" + (flow.HasBag ? flow.Bag.Slots.Count : -1));

            // 七屏都能建起来。
            var built = 0;
            built += Build(shell, flow, "角色与装备", (s, f) => UiCharacterScreen.Build(s, f));
            built += Build(shell, flow, "背包", (s, f) => UiBagScreen.Build(s, f));
            built += Build(shell, flow, "强化", (s, f) => UiEnhanceScreen.Build(s, f));
            built += Build(shell, flow, "强化转移", (s, f) => UiTransferScreen.Build(s, f));
            built += Build(shell, flow, "能量结晶", (s, f) => UiCrystalScreen.Build(s, f));
            built += Build(shell, flow, "技能与分支", (s, f) => UiSkillScreen.Build(s, f));
            built += Build(shell, flow, "境界突破", (s, f) => UiRealmScreen.Build(s, f));
            Report("seven", built == 7);
            Debug.Log("u5_self_seven=" + built);

            // 成功：等级 3→4、扣 200 金、1 块石头。
            flow.Enhance(1, "stone_enhance_mid", false);
            var ok = flow.LastEnhance;
            Report("enhance_ok", ok.Ok && ok.Success && ok.LevelBefore == 3 && ok.Level == 4
                && ok.GoldCost == 200 && ok.StoneCost == 1);
            Debug.Log("u5_self_enhance_ok level=" + ok.Level + " gold=" + ok.GoldCost);

            // 被拒：等级不动，未生效的金币与石头按回包给 0。
            flow.Enhance(1, "stone_enhance_mid", false);
            var no = flow.LastEnhance;
            Report("enhance_reject", !no.Ok && no.Reason == "at_cap"
                && no.Level == no.LevelBefore && no.GoldCost == 0 && no.StoneCost == 0);
            Debug.Log("u5_self_enhance_reject reason=" + no.Reason
                + " level=" + no.Level + " gold=" + no.GoldCost + " stone=" + no.StoneCost);

            // 转移被拒：未生效的两格等级与费用都是 0。
            flow.Transfer(1, 2);
            var t = flow.LastTransfer;
            Report("transfer_reject", !t.Ok && t.Reason == "source_not_higher"
                && t.FromLevel == 0 && t.ToLevel == 0 && t.GoldCost == 0);
            Debug.Log("u5_self_transfer reason=" + t.Reason + " gold=" + t.GoldCost);

            // 结晶被拒：已填位数保持 0，强化 / 金币 / 绑定不改。
            flow.InstallCrystal(1, 2, 1);
            var c = flow.LastCrystal;
            Report("crystal_reject", !c.Ok && c.Reason == "wrong_part"
                && c.Filled == 0 && c.EnhanceLevel == 0 && c.Gold == 0 && !c.Bound);
            Debug.Log("u5_self_crystal reason=" + c.Reason + " filled=" + c.Filled);

            // 切皮肤只换颜色：布局与文案一字不差。
            shell.ShowPage("强化", s => UiEnhanceScreen.Build(s, flow));
            var lightLayout = Layout(shell);
            var lightText = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", lightLayout == Layout(shell));
            Report("skin_copy", lightText == Text(shell));

            shell.ClosePage();
            UiSkinStore.Reset();
            UnityEngine.Object.DestroyImmediate(flow.gameObject);
            UnityEngine.Object.DestroyImmediate(shell.gameObject);

            Debug.Log("u5_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        static int Build(UiShell shell, UiUpgradeFlow flow, string title, Action<UiShell, UiUpgradeFlow> build)
        {
            try
            {
                shell.ShowPage(title, s => build(s, flow));
                return 1;
            }
            catch (Exception error)
            {
                Report("build_" + title, false);
                Debug.LogError("u5_self_build_error " + title + " " + error);
                return 0;
            }
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("u5_self_" + key + "=" + (ok ? "ok" : "fail"));
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

        const string BagJson =
            "{\"slots\":[{\"slot\":1,\"item_id\":\"sword_trial\",\"count\":1,\"bound\":true}," +
            "{\"slot\":2,\"item_id\":\"stone_enhance_mid\",\"count\":3,\"bound\":true}]," +
            "\"bag_slots\":40,\"character_name\":\"自检角色\",\"realm\":\"炼体\"}";

        const string EnhanceOkJson =
            "{\"ok\":true,\"reason\":\"\",\"level_before\":3,\"level\":4,\"max_level\":12," +
            "\"success\":true,\"tier\":\"mid\",\"required_stone\":\"stone_enhance_mid\"," +
            "\"stone_cost\":1,\"gold_cost\":200,\"gold\":800,\"stone_left\":2,\"duplicate\":false," +
            "\"protected_level\":false,\"coupon_used\":0,\"coupon_left\":0}";

        const string EnhanceRejectJson =
            "{\"ok\":false,\"reason\":\"at_cap\",\"level_before\":3,\"level\":3,\"max_level\":12," +
            "\"success\":false,\"tier\":\"\",\"required_stone\":\"\",\"stone_cost\":0,\"gold_cost\":0," +
            "\"gold\":1000,\"stone_left\":3,\"duplicate\":false,\"protected_level\":false," +
            "\"coupon_used\":0,\"coupon_left\":0}";

        const string TransferRejectJson =
            "{\"ok\":false,\"reason\":\"source_not_higher\",\"from_level_before\":0,\"to_level_before\":0," +
            "\"from_level\":0,\"to_level\":0,\"max_level\":12,\"gold_cost\":0,\"gold\":1000," +
            "\"stone_item\":\"stone_transfer\",\"stone_cost\":0,\"stone_left\":0," +
            "\"target_bound_before\":false,\"target_bound\":false,\"duplicate\":false}";

        const string CrystalRejectJson =
            "{\"ok\":false,\"reason\":\"wrong_part\",\"installed\":false,\"slot\":1,\"socket\":1," +
            "\"crystal_item\":\"\",\"category\":\"\",\"part\":\"\",\"socket_count\":0," +
            "\"filled_before\":0,\"filled\":0,\"crystal_left\":0,\"gold\":0,\"enhance_level\":0," +
            "\"bound\":false,\"duplicate\":false}";
    }
}
#endif
