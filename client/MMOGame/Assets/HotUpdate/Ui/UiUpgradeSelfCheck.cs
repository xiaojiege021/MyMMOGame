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

        /// <summary>U20：开始制作的请求体（核对里面只有角色号与配方编号）。</summary>
        static string _craftStartBody = string.Empty;

        /// <summary>U20：收取发了几次。第一次回成功带掷点，第二次回还没到点。</summary>
        static int _craftFinishCalls;

        /// <summary>U21：三条写口的请求体（觉醒 / 骑上 / 下坐骑）。</summary>
        static string _awakenBody = string.Empty;

        static string _mountBody = string.Empty;

        static string _dismountBody = string.Empty;

        /// <summary>U21：只读口的回包可以在用例里替换（觉醒状态 / 坐骑栏 / 骑乘结果）。</summary>
        static string _awakenStateJson = AwakenStateJson;

        static string _awakenActJson = AwakenOkJson;

        static string _mountsJson = MountsJson;

        static string _mountActJson = MountUpOkJson;

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
            _craftStartBody = string.Empty;
            _craftFinishCalls = 0;
            _awakenBody = string.Empty;
            _mountBody = string.Empty;
            _dismountBody = string.Empty;
            _awakenStateJson = AwakenStateJson;
            _awakenActJson = AwakenOkJson;
            _mountsJson = MountsJson;
            _mountActJson = MountUpOkJson;

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

                // U20 制作：四条口。开始口要排在只读口之前；收取第二次回「还没好」。
                if (path.IndexOf(CharacterCraft.StartPath, StringComparison.Ordinal) >= 0)
                {
                    _craftStartBody = body;
                    done(200, CraftStartJson);
                    return;
                }

                if (path.IndexOf(CharacterCraft.FinishPath, StringComparison.Ordinal) >= 0)
                {
                    _craftFinishCalls++;
                    done(200, _craftFinishCalls == 1 ? CraftFinishJson : CraftFinishNotReadyJson);
                    return;
                }

                if (path.IndexOf(CharacterCraft.StatePath, StringComparison.Ordinal) >= 0)
                {
                    done(200, CraftStateJson);
                    return;
                }

                if (path.IndexOf(CharacterCraft.RecipesPath, StringComparison.Ordinal) >= 0)
                {
                    done(200, CraftRecipesJson);
                    return;
                }

                // U21：坐骑栏要排在 /character/mount 前面（前者是后者的子串）；
                // 觉醒的 GET 与 POST 同路径，按有没有请求体分。
                if (path.IndexOf(Companion.MountsPath, StringComparison.Ordinal) >= 0)
                {
                    done(200, _mountsJson);
                    return;
                }

                if (path.IndexOf(Companion.DismountPath, StringComparison.Ordinal) >= 0)
                {
                    _dismountBody = body;
                    done(200, _mountActJson);
                    return;
                }

                if (path.IndexOf(Companion.MountUpPath, StringComparison.Ordinal) >= 0)
                {
                    _mountBody = body;
                    done(200, _mountActJson);
                    return;
                }

                if (path.IndexOf(Companion.AwakenPath, StringComparison.Ordinal) >= 0)
                {
                    if (body.Length > 0)
                    {
                        _awakenBody = body;
                        done(200, _awakenActJson);
                        return;
                    }

                    done(200, _awakenStateJson);
                    return;
                }

                if (path.IndexOf(Companion.SpiritPath, StringComparison.Ordinal) >= 0)
                {
                    done(200, BeastsJson);
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

            // U20：制作掷点跟着背包那一行走。第一格带 38 + 两条附加，第二格没有；
            // 两格都读到了才说明「一格嵌对象数组不会把后面的格截掉」。
            Report("bag_roll", flow.HasBag && flow.Bag.Slots.Count == 2
                && flow.Bag.Slots[0].CraftAttr == 38 && flow.Bag.Slots[0].CraftExtras.Count == 2
                && flow.Bag.Slots[0].CraftExtras[1].Value == 4 && !flow.Bag.Slots[1].HasRoll
                && flow.Bag.Slots[1].ItemId == "stone_enhance_mid");
            Debug.Log("u5_self_bag_roll attr=" + (flow.HasBag ? flow.Bag.Slots[0].CraftAttr : 0)
                + " extras=" + (flow.HasBag ? flow.Bag.Slots[0].CraftExtras.Count : 0)
                + " second=" + (flow.HasBag ? flow.Bag.Slots[1].ItemId : ""));

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
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T024, (s, f) => UiCraftScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T025, (s, f) => UiAwakenScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T026, (s, f) => UiMountScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiUpgradeSelfCheck.T027, (s, f) => UiSpiritBeastScreen.Build(s, f));
            Report("screens", built == 12);
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

            // U20 制作：配方目录 / 开始 / 状态 / 收取。材料、成功率、倒计时都是回包里的值；
            // 收取带回这一手的掷点（主属性 38 + 两条附加），屏上照抄。
            flow.LoadRecipes();
            var rc = flow.LastRecipes;
            Report("craft_recipes", rc.Ok && rc.Recipes.Count == 2 && rc.Level == 12
                && rc.Recipes[0].Unlocked && rc.Recipes[0].SuccessPct == 60
                && rc.Recipes[0].OutputItemId == "ring_trial" && rc.Recipes[1].UnlockLevel == 20);
            Debug.Log("u5_self_craft_recipes rows=" + rc.Recipes.Count + " level=" + rc.Level);

            flow.CraftRecipeId = "ring_trial";
            flow.StartCraft(flow.CraftRecipeId);
            var cs = flow.LastCraftStart;
            Report("craft_start", cs.Ok && cs.RecipeId == "ring_trial"
                && _craftStartBody == "{\"character_id\":7,\"recipe_id\":\"ring_trial\"}");
            Debug.Log("u5_self_craft_start remaining=" + cs.RemainingS + " body=" + _craftStartBody);

            flow.LoadCraftState();
            var stt = flow.LastCraftState;
            Report("craft_state", stt.Ok && stt.Active && stt.RecipeId == "ring_trial" && stt.RemainingS == 30);
            Debug.Log("u5_self_craft_state active=" + stt.Active + " remaining=" + stt.RemainingS);

            flow.FinishCraft();
            var cf = flow.LastCraftFinish;
            Report("craft_finish", cf.Ok && cf.Success && cf.CraftAttr == 38 && cf.CraftExtras.Count == 2
                && cf.CraftExtras[1].Value == 4 && flow.HasBag);
            Debug.Log("u5_self_craft_finish attr=" + cf.CraftAttr + " extras=" + cf.CraftExtras.Count);

            // 没到点：被拒，产出与掷点都不该变；界面照拒绝码写。
            flow.FinishCraft();
            var nr = flow.LastCraftFinish;
            Report("craft_not_ready", !nr.Ok && nr.Reason == "craft_not_ready");
            Debug.Log("u5_self_craft_not_ready reason=" + nr.Reason);

            // ------------------------------------------------ 十四、U21 觉醒屏（B6 欠项）
            //     状态、等级、技能编号与时刻全照抄；没觉醒写「未觉醒」，请求体只有角色号与分支号。
            _awakenStateJson = AwakenStateJson;
            _awakenActJson = AwakenOkJson;
            flow.LoadAwaken();
            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T025, s => UiAwakenScreen.Build(s, flow));
            var awaken = Text(shell);
            Report("u21_awaken_state", flow.HasAwaken && flow.LastAwaken.Branch == "pojun"
                && flow.LastAwaken.SkillId == 0 && flow.LastAwaken.Level == 100
                && flow.LastAwaken.AwakenAtMs == 0
                && awaken.Contains("pojun") && awaken.Contains("100")
                && awaken.Contains(UiStrings.UiAwakenScreen.T009));
            Debug.Log("u21_self_awaken_state branch=" + flow.LastAwaken.Branch
                + " skill=" + flow.LastAwaken.SkillId + " level=" + flow.LastAwaken.Level);

            // 觉醒这一笔：请求体逐字；成功后按只读口重画（回包换成已觉醒的那一份），
            // 屏上出现技能编号与「已觉醒」是回包给的，不是本地翻的状态。
            _awakenStateJson = AwakenedStateJson;
            flow.Awaken("pojun");
            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T025, s => UiAwakenScreen.Build(s, flow));
            var awakened = Text(shell);
            Report("u21_awaken_ok", flow.HasAwakenAct && flow.LastAwakenAct.Ok
                && flow.LastAwakenAct.SkillId == 1004 && flow.LastAwakenAct.Level == 100
                && flow.HasAwaken && flow.LastAwaken.SkillId == 1004
                && awakened.Contains(UiStrings.UiAwakenScreen.T024 + "1004")
                && awakened.Contains(UiStrings.UiAwakenScreen.T010));
            Report("u21_awaken_body", _awakenBody == "{\"character_id\":7,\"branch_id\":\"pojun\"}");
            Debug.Log("u21_self_awaken_body=" + _awakenBody);

            // 不够级：原因原样上屏，状态也回到还没觉醒的那一份（进屏先读一次，再点觉醒）。
            _awakenStateJson = AwakenStateJson;
            _awakenActJson = AwakenRejectJson;
            flow.LoadAwaken();
            flow.Awaken("pojun");
            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T025, s => UiAwakenScreen.Build(s, flow));
            Report("u21_awaken_reject", flow.HasAwakenAct && !flow.LastAwakenAct.Ok
                && Text(shell).Contains("not_enough_level")
                && flow.HasAwaken && flow.LastAwaken.SkillId == 0);

            // 十五、U21 坐骑栏（B4 欠项）：当前骑的、速度、清单全照抄；
            //     骑上只发角色号与格号，成功后坐骑栏按回包重画。
            _mountsJson = MountsJson;
            _mountActJson = MountUpOkJson;
            flow.LoadMounts();
            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T026, s => UiMountScreen.Build(s, flow));
            var mount = Text(shell);
            Report("u21_mounts", flow.HasMounts && flow.LastMounts.Owned.Count == 2
                && flow.LastMounts.EquippedItemId == "mount_wind"
                && flow.LastMounts.EquippedSpeedBps == 2500
                && mount.Contains("mount_wind") && mount.Contains("2500")
                && mount.Contains(UiStrings.UiMountScreen.T011)
                && mount.Contains(UiStrings.UiMountScreen.T012));

            _mountsJson = MountsAfterJson;
            flow.MountUp(2);
            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T026, s => UiMountScreen.Build(s, flow));
            Report("u21_mount_ok", flow.HasMountAct && flow.LastMountAct.Ok
                && flow.LastMountAct.ItemId == "mount_cloud"
                && flow.HasMounts && flow.LastMounts.EquippedItemId == "mount_cloud"
                && flow.LastMounts.Owned[1].Mounted && !flow.LastMounts.Owned[0].Mounted);
            Report("u21_mount_body", _mountBody == "{\"character_id\":7,\"slot\":2}");
            Debug.Log("u21_self_mount_body=" + _mountBody);

            _mountsJson = MountsJson;
            _mountActJson = DismountOkJson;
            flow.Dismount();
            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T026, s => UiMountScreen.Build(s, flow));
            Report("u21_dismount", flow.HasMountAct && flow.LastMountAct.Ok
                && Text(shell).Contains(UiStrings.UiMountScreen.T027));
            Report("u21_dismount_body", _dismountBody == "{\"character_id\":7}");
            Debug.Log("u21_self_dismount_body=" + _dismountBody);

            // 空格子：原因原样上屏，不假装「没骑上」是本地的事。
            _mountActJson = MountRejectJson;
            flow.MountUp(9);
            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T026, s => UiMountScreen.Build(s, flow));
            Report("u21_mount_reject", flow.HasMountAct && !flow.LastMountAct.Ok
                && Text(shell).Contains("slot_empty"));

            // 十六、U21 灵兽图鉴（B4 欠项）：收集度、条数、万分比与生活协助照抄，
            //     已收集与未收集两档都上屏；这一屏没有写口。
            flow.LoadSpiritBeasts();
            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T027, s => UiSpiritBeastScreen.Build(s, flow));
            var beasts = Text(shell);
            Report("u21_beasts", flow.HasBeasts && flow.LastBeasts.CollectedCount == 2
                && flow.LastBeasts.EntryCount == 3 && flow.LastBeasts.Entries.Count == 3
                && flow.LastBeasts.CollectionPctBps == 6666
                && flow.LastBeasts.TotalLifeAssistPctBps == 40
                && beasts.Contains("6666") && beasts.Contains("life_assist")
                && beasts.Contains(UiStrings.UiSpiritBeastScreen.T011)
                && beasts.Contains(UiStrings.UiSpiritBeastScreen.T012));
            Debug.Log("u21_self_beasts collected=" + flow.LastBeasts.CollectedCount
                + " rows=" + flow.LastBeasts.Entries.Count + " bps=" + flow.LastBeasts.CollectionPctBps);

            // 十七、三屏切皮肤只换颜色：布局与文案一字不差。
            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T025, s => UiAwakenScreen.Build(s, flow));
            var awakenLayout = Layout(shell);
            var awakenText = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("u21_awaken_skin_layout", awakenLayout == Layout(shell));
            Report("u21_awaken_skin_copy", awakenText == Text(shell));
            shell.SetSkin(UiSkin.Light, false);

            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T026, s => UiMountScreen.Build(s, flow));
            var mountLayout = Layout(shell);
            var mountText = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("u21_mount_skin_layout", mountLayout == Layout(shell));
            Report("u21_mount_skin_copy", mountText == Text(shell));
            shell.SetSkin(UiSkin.Light, false);

            shell.ShowPage(UiStrings.UiUpgradeSelfCheck.T027, s => UiSpiritBeastScreen.Build(s, flow));
            var beastLayout = Layout(shell);
            var beastText = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("u21_beast_skin_layout", beastLayout == Layout(shell));
            Report("u21_beast_skin_copy", beastText == Text(shell));
            shell.SetSkin(UiSkin.Light, false);

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

        // U20 制作夹具：两条配方（一条已解锁一条差等级）、开始、状态、收取（带掷点）、没到点。
        const string CraftRecipesJson =
            "{\"ok\":true,\"reason\":\"\",\"table_version\":4,\"pending\":true,\"level\":12," +
            "\"professions\":[{\"id\":\"tailor\",\"name\":\"tailor\"},{\"id\":\"smith\",\"name\":\"smith\"}]," +
            "\"recipes\":[" +
            "{\"id\":\"ring_trial\",\"profession\":\"tailor\",\"unlock_level\":5,\"unlocked\":true," +
            "\"success_pct\":60,\"craft_time_s\":30,\"inputs\":[{\"item_id\":\"ore_iron\",\"count\":2}]," +
            "\"fee\":[{\"kind\":\"gold\",\"amount\":50}]," +
            "\"output\":{\"item_id\":\"ring_trial\",\"count\":1},\"note\":\"\"}," +
            "{\"id\":\"sword_trial\",\"profession\":\"smith\",\"unlock_level\":20,\"unlocked\":false," +
            "\"success_pct\":45,\"craft_time_s\":60,\"inputs\":[{\"item_id\":\"ore_iron\",\"count\":4}]," +
            "\"fee\":[{\"kind\":\"gold\",\"amount\":100}]," +
            "\"output\":{\"item_id\":\"sword_trial\",\"count\":1},\"note\":\"\"}]}";

        const string CraftStartJson =
            "{\"ok\":true,\"reason\":\"\",\"duplicate\":false,\"recipe_id\":\"ring_trial\"," +
            "\"profession\":\"tailor\",\"craft_time_s\":30,\"started_at_unix\":1700000000," +
            "\"ready_at_unix\":1700000030,\"remaining_s\":30}";

        const string CraftStateJson =
            "{\"ok\":true,\"reason\":\"\",\"active\":true,\"recipe_id\":\"ring_trial\"," +
            "\"profession\":\"tailor\",\"craft_time_s\":30,\"started_at_unix\":1700000000," +
            "\"ready_at_unix\":1700000030,\"remaining_s\":30}";

        const string CraftFinishJson =
            "{\"ok\":true,\"reason\":\"\",\"duplicate\":false,\"recipe_id\":\"ring_trial\"," +
            "\"profession\":\"tailor\",\"success\":true,\"output_item_id\":\"ring_trial\",\"output_count\":1," +
            "\"success_pct\":60,\"craft_time_s\":30,\"ready_at_unix\":1700000030,\"craft_attr\":38," +
            "\"craft_extras\":[{\"attr\":\"attack\",\"value\":6},{\"attr\":\"attack\",\"value\":4}]}";

        const string CraftFinishNotReadyJson =
            "{\"ok\":false,\"reason\":\"craft_not_ready\",\"duplicate\":false,\"recipe_id\":\"ring_trial\"," +
            "\"profession\":\"tailor\",\"success\":false,\"output_item_id\":\"\",\"output_count\":0," +
            "\"success_pct\":60,\"craft_time_s\":30,\"ready_at_unix\":0,\"craft_attr\":0,\"craft_extras\":[]}";

        // U20：第一格是制作产出的装备（带主属性与两条附加），第二格不是。
        // 这段还守着解析坑：一格嵌了 craft_extras 对象数组，后面的格不能被截断。
        const string BagJson =
            "{\"slots\":[{\"slot\":1,\"item_id\":\"sword_trial\",\"count\":1,\"bound\":true," +
            "\"equipped_slot\":\"weapon\",\"craft_attr\":38," +
            "\"craft_extras\":[{\"attr\":\"attack\",\"value\":6},{\"attr\":\"attack\",\"value\":4}]}," +
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

        // U21：觉醒状态两份（未觉醒 / 已觉醒）、觉醒这一笔成功与被拒、坐骑栏骑上前后两份、
        // 骑上 / 下坐骑 / 空格子、灵兽图鉴。名字、档位与分支用 ASCII，免得断言里再进一份中文。
        const string AwakenStateJson =
            "{\"found\":true,\"branch\":\"pojun\",\"skill_id\":0,\"level\":100,\"awaken_at_ms\":0}";

        const string AwakenedStateJson =
            "{\"found\":true,\"branch\":\"pojun\",\"skill_id\":1004,\"level\":100," +
            "\"awaken_at_ms\":1760000000000}";

        const string AwakenOkJson =
            "{\"ok\":true,\"reason\":\"\",\"branch\":\"pojun\",\"skill_id\":1004," +
            "\"level\":100,\"duplicate\":false}";

        /// <summary>不到 100 级：等级停在 99，状态仍是没觉醒。</summary>
        const string AwakenRejectJson =
            "{\"ok\":false,\"reason\":\"not_enough_level\",\"branch\":\"pojun\",\"skill_id\":0," +
            "\"level\":99,\"duplicate\":false}";

        /// <summary>骑着一只、背包里还有一只；mounted 只有第一只是 true。</summary>
        const string MountsJson =
            "{\"ok\":true,\"character_id\":7,\"equipped_item_id\":\"mount_wind\"," +
            "\"equipped_speed_bps\":2500,\"owned\":[" +
            "{\"item_id\":\"mount_wind\",\"name\":\"WindSteed\",\"tier\":\"normal\"," +
            "\"speed_bps\":2500,\"bag_slot\":1,\"mounted\":true}," +
            "{\"item_id\":\"mount_cloud\",\"name\":\"CloudSteed\",\"tier\":\"advanced\"," +
            "\"speed_bps\":4500,\"bag_slot\":2,\"mounted\":false}]}";

        /// <summary>骑上第二只之后再读一次的坐骑栏：mounted 翻到第二只，第一只变 false。</summary>
        const string MountsAfterJson =
            "{\"ok\":true,\"character_id\":7,\"equipped_item_id\":\"mount_cloud\"," +
            "\"equipped_speed_bps\":4500,\"owned\":[" +
            "{\"item_id\":\"mount_wind\",\"name\":\"WindSteed\",\"tier\":\"normal\"," +
            "\"speed_bps\":2500,\"bag_slot\":1,\"mounted\":false}," +
            "{\"item_id\":\"mount_cloud\",\"name\":\"CloudSteed\",\"tier\":\"advanced\"," +
            "\"speed_bps\":4500,\"bag_slot\":2,\"mounted\":true}]}";

        const string MountUpOkJson =
            "{\"ok\":true,\"reason\":\"\",\"item_id\":\"mount_cloud\",\"tier\":\"advanced\"," +
            "\"speed_bps\":4500,\"bag_slot\":2}";

        const string DismountOkJson =
            "{\"ok\":true,\"reason\":\"\",\"item_id\":\"mount_wind\",\"tier\":\"normal\"," +
            "\"speed_bps\":2500,\"bag_slot\":1}";

        /// <summary>空格子：回包里物品与档位是空串、速度 0，界面照抄不补。</summary>
        const string MountRejectJson =
            "{\"ok\":false,\"reason\":\"slot_empty\",\"item_id\":\"\",\"tier\":\"\"," +
            "\"speed_bps\":0,\"bag_slot\":0}";

        /// <summary>图鉴三只：两只已收集、一只没有；收集度 2/3 由服务端给 6666 万分比。</summary>
        const string BeastsJson =
            "{\"ok\":true,\"character_id\":7,\"collected_count\":2,\"entry_count\":3," +
            "\"collection_pct_bps\":6666,\"total_life_assist_pct_bps\":40," +
            "\"passive_kind\":\"life_assist\",\"entries\":[" +
            "{\"item_id\":\"beast_one\",\"name\":\"BeastOne\",\"tier\":\"normal\"," +
            "\"collected\":true,\"life_assist_pct_bps\":4}," +
            "{\"item_id\":\"beast_two\",\"name\":\"BeastTwo\",\"tier\":\"advanced\"," +
            "\"collected\":true,\"life_assist_pct_bps\":6}," +
            "{\"item_id\":\"beast_three\",\"name\":\"BeastThree\",\"tier\":\"divine\"," +
            "\"collected\":false,\"life_assist_pct_bps\":10}]}";
    }
}
#endif
