#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using Mmo.Net;
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

        static int _promoteCalls;

        static string _promoteBody = string.Empty;

        static string _bagMoveBody = string.Empty;

        static string _bagDiscardBody = string.Empty;

        static string _equipBody = string.Empty;

        static string _unequipBody = string.Empty;

        /// <summary>A9：加点请求体。核对要确认里面只有角色号、属性键与点数。</summary>
        static string _allocBody = string.Empty;

        /// <summary>A9：加点发了几次。第一次回成功，第二次回被拒。</summary>
        static int _allocCalls;

        public static bool Run()
        {
            _failed = false;
            _enhanceCalls = 0;
            _allocCalls = 0;
            _promoteCalls = 0;
            _promoteBody = string.Empty;
            _bagMoveBody = string.Empty;
            _bagDiscardBody = string.Empty;
            _equipBody = string.Empty;
            _unequipBody = string.Empty;

            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            var flow = UiUpgradeFlow.Ensure(shell);
            flow.SetSender((path, body, token, done) =>
            {
                // Q10 的四条要排在 /inventory 前面：否则被背包只读口那条前缀先吃掉。
                if (path.IndexOf("/inventory/move", StringComparison.Ordinal) >= 0)
                {
                    _bagMoveBody = body;
                    done(200, MoveOkJson);
                    return;
                }

                if (path.IndexOf("/inventory/discard", StringComparison.Ordinal) >= 0)
                {
                    _bagDiscardBody = body;
                    done(200, DiscardDuplicateJson);
                    return;
                }

                if (path.IndexOf("/inventory/sort", StringComparison.Ordinal) >= 0)
                {
                    done(200, SortOkJson);
                    return;
                }

                if (path.IndexOf("/inventory/use-batch", StringComparison.Ordinal) >= 0)
                {
                    done(200, UseRejectJson);
                    return;
                }

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

                // B9：穿戴两条也要在兜底之前接住，否则会被结晶那条兜底回包吃掉。
                if (path.IndexOf("/character/equip", StringComparison.Ordinal) >= 0)
                {
                    _equipBody = body;
                    done(200, EquipOkJson);
                    return;
                }

                if (path.IndexOf("/character/unequip", StringComparison.Ordinal) >= 0)
                {
                    _unequipBody = body;
                    done(200, UnequipOkJson);
                    return;
                }

                // U13：升阶口在前，别被只读口的前缀先吃掉。
                if (path.IndexOf(CharacterRealmPhase.PromotePath, StringComparison.Ordinal) >= 0)
                {
                    _promoteCalls++;
                    _promoteBody = body;
                    done(200, _promoteCalls == 1 ? PromoteOkJson : PromoteRejectJson);
                    return;
                }

                if (path.IndexOf(CharacterRealmPhase.ReadPath, StringComparison.Ordinal) >= 0)
                {
                    done(200, RealmPhaseJson);
                    return;
                }

                // A9：加点口在前，别被只读口的前缀先吃掉。
                if (path.IndexOf(CharacterAttr.AllocatePath, StringComparison.Ordinal) >= 0)
                {
                    _allocBody = body;
                    _allocCalls++;
                    done(200, _allocCalls == 1 ? AttrAllocateOkJson : AttrAllocateRejectJson);
                    return;
                }

                if (path.IndexOf(CharacterAttr.ReadPath, StringComparison.Ordinal) >= 0)
                {
                    done(200, AttrPointsJson);
                    return;
                }

                done(200, CrystalRejectJson);
            });

            flow.Bind("selftest-token", 7);
            flow.LoadBag();
            Report("bag", flow.HasBag && flow.Bag.Slots.Count == 2
                && flow.Bag.CharacterName == UiStrings.UiUpgradeSelfCheck.T001 && flow.Bag.Realm == UiStrings.UiUpgradeSelfCheck.T002
                && flow.Bag.DisplayRealm == UiStrings.UiUpgradeSelfCheck.T002);
            Debug.Log("u5_self_bag slots=" + (flow.HasBag ? flow.Bag.Slots.Count : -1));

            // C2：第十段立魔按服务端给的展示名显示散仙，规范名仍是金仙；老回包没这一格时退回规范名。
            var demon = CharacterBag.Parse(
                UiStrings.UiUpgradeSelfCheck.T003);
            var old = CharacterBag.Parse(
                UiStrings.UiUpgradeSelfCheck.T004);
            Report("realm_display", demon.Ok && demon.Realm == UiStrings.UiUpgradeSelfCheck.T005 && demon.DisplayRealm == UiStrings.UiUpgradeSelfCheck.T006
                && old.DisplayRealm == UiStrings.UiUpgradeSelfCheck.T005);
            Debug.Log("u5_self_realm_display realm=" + demon.Realm
                + " display=" + demon.DisplayRealm + " fallback=" + old.DisplayRealm);

            // 七屏都能建起来。
            var built = 0;
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T007, (s, f) => UiCharacterScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T008, (s, f) => UiBagScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T009, (s, f) => UiEnhanceScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T010, (s, f) => UiTransferScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T011, (s, f) => UiCrystalScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T012, (s, f) => UiSkillScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T013, (s, f) => UiRealmScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T023, (s, f) => UiAttrScreen.Build(s, f));
            Report("screens", built == 8);
            Debug.Log("u5_self_screens=" + built);

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

            // U13：段内阶只读回包，升一阶只发角色号，门槛照回包写、被拒不改屏上那一阶。
            Report("realm_pending",
                CharacterRealmPhase.PhaseLine(false, default(RealmPhaseOutcome)) == UiStrings.UiUpgradeSelfCheck.T014);

            flow.LoadRealmPhase();
            var rp = flow.LastRealmPhase;
            Report("realm_phase", flow.RealmPhaseRequested && rp.Ok && rp.Realm == UiStrings.UiUpgradeSelfCheck.T002
                && rp.Phase == 0 && rp.PhaseName == UiStrings.UiUpgradeSelfCheck.T015 && rp.PassivePctBps == 0);
            Debug.Log("u5_self_realm_phase phase=" + rp.Phase + " name=" + rp.PhaseName
                + " bps=" + rp.PassivePctBps);

            flow.PromoteRealmPhase();
            var po = flow.LastPromote;
            Report("promote_ok", po.Ok && po.FromPhase == 0 && po.ToPhase == 1
                && po.PhaseName == UiStrings.UiUpgradeSelfCheck.T016 && po.PassivePctBps == 30
                && flow.LastRealmPhase.Phase == 1 && flow.LastRealmPhase.PassivePctBps == 30);
            Debug.Log("u5_self_promote_ok from=" + po.FromPhase + " to=" + po.ToPhase
                + " name=" + po.PhaseName + " bps=" + po.PassivePctBps);

            flow.PromoteRealmPhase();
            var pr = flow.LastPromote;
            Report("promote_reject", !pr.Ok && pr.Reason == "level_not_enough"
                && pr.Level == 3 && pr.LevelRequired == 11 && flow.LastRealmPhase.Phase == 1);
            Debug.Log("u5_self_promote_reject reason=" + pr.Reason + " level=" + pr.Level
                + " required=" + pr.LevelRequired + " kept=" + flow.LastRealmPhase.Phase);

            // 请求体只有角色号：事务号由网关生成，客户端带不进任何数值。
            Report("promote_body", _promoteBody == "{\"character_id\":7}"
                && _promoteBody.IndexOf("txn", StringComparison.OrdinalIgnoreCase) < 0);
            Debug.Log("u5_self_promote_body " + _promoteBody);

            // Q10：四条背包操作。回包靠替换发送实现喂进来，不连网关；
            // 屏上认的只有回包的字段与请求体里那几个参数。
            flow.MoveItem(3, 5);
            var mv = flow.LastMove;
            Report("q10_move", mv.Ok && mv.Merged && mv.Target.Count == 200
                && mv.Source.ItemId.Length == 0
                && _bagMoveBody == "{\"character_id\":7,\"from_slot\":3,\"to_slot\":5}");
            Debug.Log("u5_self_q10_move target=" + mv.Target.Slot + ":" + mv.Target.Count
                + " body=" + _bagMoveBody);

            flow.DiscardItem(5, 50);
            var dc = flow.LastDiscard;
            Report("q10_discard", dc.Duplicate && dc.CountLeft == 150 && !dc.Ok
                && _bagDiscardBody.IndexOf("\"confirm\":true", StringComparison.Ordinal) >= 0);
            Debug.Log("u5_self_q10_discard left=" + dc.CountLeft + " body=" + _bagDiscardBody);

            flow.SortBag();
            var st = flow.LastSort;
            Report("q10_sort", st.Ok && st.Moved == 2 && st.Slots.Count == 2
                && st.Slots[0].Slot == 1 && flow.Bag.Slots.Count == 2);
            Debug.Log("u5_self_q10_sort moved=" + st.Moved + " rows=" + st.Slots.Count);

            flow.UseItemBatch(1, 2);
            var ub = flow.LastUseBatch;
            Report("q10_use", !ub.Ok && ub.Reason == "effect_single_instance" && ub.Used == 0);
            Debug.Log("u5_self_q10_use reason=" + ub.Reason + " used=" + ub.Used);

            // B9：格子上的 equipped_slot 只认回包——第 1 格穿着武器，第 2 格没穿。
            Report("b9_bag_worn", flow.Bag.Slots[0].EquippedSlot == "weapon"
                && !flow.Bag.Slots[1].IsWorn);
            Debug.Log("u5_self_b9_bag_worn slot1=" + flow.Bag.Slots[0].EquippedSlot
                + " slot2=" + flow.Bag.Slots[1].EquippedSlot);

            // B9：穿 / 脱。请求体只有角色号与格号 / 部位，数值一个都没带。
            flow.EquipItem(1);
            var eq = flow.LastEquip;
            Report("b9_equip", eq.Ok && eq.EquipSlot == "weapon" && eq.EquipAttack == 20
                && eq.Level == 12 && eq.BagSlot == 1
                && _equipBody == "{\"character_id\":7,\"slot\":1}");
            Debug.Log("u5_self_b9_equip worn=" + eq.EquipSlot + " attack=" + eq.EquipAttack
                + " body=" + _equipBody);

            flow.UnequipItem("weapon");
            var uq = flow.LastUnequip;
            Report("b9_unequip", uq.Ok && uq.EquipSlot == "weapon" && uq.BagSlot == 1
                && uq.EquipAttack == 0
                && _unequipBody == "{\"character_id\":7,\"equip_slot\":\"weapon\"}");
            Debug.Log("u5_self_b9_unequip bag_slot=" + uq.BagSlot + " attack=" + uq.EquipAttack
                + " body=" + _unequipBody);

            // A9：只读口照回包给剩余点数与四格已加点；加点只发属性键与点数。
            flow.LoadAttrPoints();
            var ap = flow.AttrPoints;
            Report("a9_attr_points", flow.AttrPointsRequested && ap.Ok && ap.AttrPoints == 7
                && ap.Strength == 3 && ap.Agility == 1 && ap.Spirit == 0 && ap.Vitality == 2);
            Debug.Log("u5_self_a9_attr_points points=" + ap.AttrPoints + " strength=" + ap.Strength
                + " agility=" + ap.Agility + " spirit=" + ap.Spirit + " vitality=" + ap.Vitality);

            // 加点：请求体只有三项，成功后剩余点数与四格照回包换。
            flow.AllocateAttr("strength", 3);
            var al = flow.LastAllocate;
            Report("a9_allocate_ok", al.Ok && al.AttrPoints == 4 && al.Strength == 6
                && _allocBody == "{\"character_id\":7,\"attribute\":\"strength\",\"points\":3}");
            Debug.Log("u5_self_a9_allocate_ok points=" + al.AttrPoints + " strength=" + al.Strength
                + " body=" + _allocBody);

            // 被拒：拒绝码原样上屏，剩余点数与四格照回包，不自减也不自己加。
            flow.AllocateAttr("strength", 8);
            var ar = flow.LastAllocate;
            Report("a9_allocate_reject", !ar.Ok && ar.Reason == "not_enough"
                && ar.AttrPoints == 4 && ar.Strength == 6);
            Debug.Log("u5_self_a9_allocate_reject reason=" + ar.Reason
                + " points=" + ar.AttrPoints + " strength=" + ar.Strength);

            // 切皮肤只换颜色：布局与文案一字不差。
            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T009, s => UiEnhanceScreen.Build(s, flow));
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
            "{\"slots\":[{\"slot\":1,\"item_id\":\"sword_trial\",\"count\":1,\"bound\":true," +
            "\"equipped_slot\":\"weapon\"}," +
            "{\"slot\":2,\"item_id\":\"stone_enhance_mid\",\"count\":3,\"bound\":true," +
            "\"equipped_slot\":\"\"}]," +
            UiStrings.UiUpgradeSelfCheck.T017 +
            UiStrings.UiUpgradeSelfCheck.T018;

        // Q10：合并成功 / 同号重放的丢弃 / 排完两份 / 连用被拒。字段形状与网关回包一致。
        const string MoveOkJson =
            "{\"ok\":true,\"reason\":\"\",\"source\":{\"slot\":3,\"item_id\":\"\",\"count\":0," +
            "\"bound\":false},\"target\":{\"slot\":5,\"item_id\":\"ore_iron\",\"count\":200," +
            "\"bound\":false},\"merged\":true,\"swapped\":false}";

        const string DiscardDuplicateJson =
            "{\"ok\":false,\"reason\":\"duplicate\",\"item_id\":\"ore_iron\",\"count_left\":150," +
            "\"duplicate\":true}";

        // 排序回包也是整份背包，带 equipped_slot（网关 /inventory/sort 原样转发），
        // 排完序屏上照样认得出第 1 格穿着武器。
        const string SortOkJson =
            "{\"ok\":true,\"reason\":\"\",\"moved\":2,\"slots\":[" +
            "{\"slot\":1,\"item_id\":\"sword_trial\",\"count\":1,\"bound\":true," +
            "\"equipped_slot\":\"weapon\"}," +
            "{\"slot\":2,\"item_id\":\"stone_enhance_mid\",\"count\":3,\"bound\":true," +
            "\"equipped_slot\":\"\"}]}";

        // B9：穿上 / 脱下。部位、装备攻击之和、格号都是网关回包里的字段。
        const string EquipOkJson =
            "{\"ok\":true,\"reason\":\"\",\"item_id\":\"sword_trial\",\"equip_slot\":\"weapon\"," +
            "\"equip_attack\":20,\"level\":12,\"class_id\":\"warrior\",\"bag_slot\":1}";

        const string UnequipOkJson =
            "{\"ok\":true,\"reason\":\"\",\"item_id\":\"sword_trial\",\"equip_slot\":\"weapon\"," +
            "\"bag_slot\":1,\"equip_attack\":0}";

        const string UseRejectJson =
            "{\"ok\":false,\"reason\":\"effect_single_instance\",\"item_id\":\"exp_booster\"," +
            "\"used\":0,\"count_left\":2,\"duplicate\":false}";

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

        const string RealmPhaseJson =
            UiStrings.UiUpgradeSelfCheck.T019 +
            UiStrings.UiUpgradeSelfCheck.T020;

        const string PromoteOkJson =
            "{\"ok\":true,\"reason\":\"\",\"txn_id\":\"realm_phase:selftest-a\",\"character_id\":7," +
            UiStrings.UiUpgradeSelfCheck.T021 +
            "\"level\":6,\"level_required\":11,\"duplicate\":false}";

        const string PromoteRejectJson =
            "{\"ok\":false,\"reason\":\"level_not_enough\",\"txn_id\":\"realm_phase:selftest-b\"," +
            UiStrings.UiUpgradeSelfCheck.T022 +
            "\"passive_pct_bps\":30,\"level\":3,\"level_required\":11,\"duplicate\":false}";

        // A9：四属性是**已加点数**（角色服 Allocated 那四个值），基础值不在这里。
        const string AttrPointsJson =
            "{\"ok\":true,\"character_id\":7,\"class_id\":1,\"level\":12,\"attr_points\":7," +
            "\"strength\":3,\"agility\":1,\"spirit\":0,\"vitality\":2}";

        const string AttrAllocateOkJson =
            "{\"ok\":true,\"reason\":\"\",\"duplicate\":false,\"character_id\":7," +
            "\"attribute\":\"strength\",\"points\":3,\"class_id\":1,\"level\":12,\"attr_points\":4," +
            "\"strength\":6,\"agility\":1,\"spirit\":0,\"vitality\":2}";

        const string AttrAllocateRejectJson =
            "{\"ok\":false,\"reason\":\"not_enough\",\"duplicate\":false,\"character_id\":7," +
            "\"attribute\":\"strength\",\"points\":8,\"class_id\":1,\"level\":12,\"attr_points\":4," +
            "\"strength\":6,\"agility\":1,\"spirit\":0,\"vitality\":2}";
    }
}
#endif
