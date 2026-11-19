using System;
using System.Collections.Generic;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 背包（原型 D02 / Q10 / B9）。左边只列 `/inventory` 回包里的格号、物品编号、数量与绑定态；
    /// 右边五组操作：移动、丢弃、排序、批量使用、穿 / 脱。
    ///
    /// 这一层一条数值都不算：合不合并、交不交换、丢几个、排序后落在哪一格、
    /// 这一格能不能连用，全看 `/inventory/move`、`/inventory/discard`、`/inventory/sort`、
    /// `/inventory/use-batch` 的回包。选中的格号与数量存在流程里（刷新屏不丢），
    /// 结果只写回包给的字段。
    /// </summary>
    public static class UiBagScreen
    {
        public const string ReadyLog = "u5_bag_screen=ready";

        public static int Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell,
                "背包 · 格号 / 数量 / 绑定态来自 /inventory 回包；移动、丢弃、排序、批量使用只显示服务端回包");

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.62f;
            var cardH = m.Px(760f);
            var list = shell.Panel("背包", m.Band(pad, top + m.Px(56f), leftW, cardH), shell.Size(36f));

            var rows = flow.HasBag ? UiUpgradeCodex.FirstSlots(flow.Bag, 9) : null;
            if (rows == null || rows.Count == 0)
            {
                shell.LabelIn(list.Root, new Rect(m.Px(32f), cardH - m.Px(120f), leftW - m.Px(64f), m.Px(48f)),
                    flow.HasBag ? "背包是空的" : "尚未读背包", UiTextRole.Faint, shell.Size(26f),
                    TextAnchor.MiddleLeft, false);
            }
            else
            {
                var labels = new string[rows.Count];
                var values = new string[rows.Count];
                for (var i = 0; i < rows.Count; i++)
                {
                    labels[i] = "第 " + rows[i].Slot + " 格";
                    values[i] = rows[i].ItemId + " × " + rows[i].Count + (rows[i].Bound ? " · 绑定" : "");
                }

                UiUpgradeViews.Rows(shell, list, labels, values, m.Px(64f), m.Px(32f));
            }

            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;
            var side = shell.Panel("容量", m.Band(rightX, top + m.Px(56f), rightW, m.Px(280f)), shell.Size(32f));
            UiUpgradeViews.Rows(shell, side,
                new[] { "容量", "已用", "来源" },
                new[]
                {
                    flow.HasBag ? flow.Bag.BagSlots.ToString() : "—",
                    flow.HasBag && flow.Bag.Slots != null ? flow.Bag.Slots.Count.ToString() : "—",
                    "GET /inventory",
                },
                m.Px(64f), m.Px(32f));

            var opsTop = top + m.Px(56f) + m.Px(280f) + m.Px(24f);
            var opsH = m.Px(760f);
            var ops = shell.Panel("操作", m.Band(rightX, opsTop, rightW, opsH), shell.Size(32f));
            BuildOps(shell, flow, ops, rightW, opsH);

            Debug.Log(ReadyLog + " rows=" + (rows != null ? rows.Count : 0)
                + " total=" + (flow.HasBag && flow.Bag.Slots != null ? flow.Bag.Slots.Count : 0)
                + " from=" + flow.BagFromSlot
                + " to=" + flow.BagToSlot
                + " count=" + flow.BagCount);
            return rows != null ? rows.Count : 0;
        }

        /// <summary>右栏五组操作。格号与数量只在 1..容量之间调，取值范围也来自回包的事实值。</summary>
        static void BuildOps(UiShell shell, UiUpgradeFlow flow, UiPanel ops, float width, float height)
        {
            var m = shell.Metrics;
            var pad = m.Px(28f);
            var bagSlots = flow.HasBag && flow.Bag.BagSlots > 0 ? (uint)flow.Bag.BagSlots : 1u;
            flow.BagFromSlot = Clamp(flow.BagFromSlot == 0 ? 1u : flow.BagFromSlot, 1u, bagSlots);
            flow.BagToSlot = Clamp(flow.BagToSlot == 0 ? 1u : flow.BagToSlot, 1u, bagSlots);

            // 数量只按「这一格现在有几个」夹住：界面上不许出现服务端一定会拒的数。
            var held = HeldCount(flow, flow.BagFromSlot);
            flow.BagCount = flow.BagCount < 1 ? 1 : flow.BagCount;
            if (held > 0 && flow.BagCount > held)
            {
                flow.BagCount = held;
            }

            UiUpgradeViews.Result(shell, ops, height - m.Px(100f), "来源 / 目标",
                "第 " + flow.BagFromSlot + " 格 → 第 " + flow.BagToSlot + " 格");
            UiUpgradeViews.Result(shell, ops, height - m.Px(150f), "这一格 / 数量",
                (held > 0 ? held + " 个" : "空格") + " / " + flow.BagCount);
            UiUpgradeViews.Result(shell, ops, height - m.Px(200f), "这一格穿着",
                WornText(flow, flow.BagFromSlot));
            UiUpgradeViews.Result(shell, ops, height - m.Px(250f), "装备攻击", EquipAttackText(flow));
            UiUpgradeViews.Result(shell, ops, height - m.Px(300f), "上次结果", LastText(flow));

            var gap = m.Px(16f);
            var btnW = (width - pad * 2f - gap * 3f) * 0.25f;
            var btnH = m.Px(72f);
            var row1 = height - m.Px(400f);
            var row2 = height - m.Px(490f);
            var row3 = height - m.Px(580f);
            var row4 = height - m.Px(670f);

            shell.ButtonIn(ops.Root, new Rect(pad, row1, btnW, btnH), "来源 −",
                () =>
                {
                    flow.BagFromSlot = Clamp(flow.BagFromSlot - 1, 1u, bagSlots);
                    shell.RefreshPage();
                }, UiButtonKind.Normal);
            shell.ButtonIn(ops.Root, new Rect(pad + btnW + gap, row1, btnW, btnH), "来源 +",
                () =>
                {
                    flow.BagFromSlot = Clamp(flow.BagFromSlot + 1, 1u, bagSlots);
                    shell.RefreshPage();
                }, UiButtonKind.Normal);
            shell.ButtonIn(ops.Root, new Rect(pad + (btnW + gap) * 2f, row1, btnW, btnH), "目标 −",
                () =>
                {
                    flow.BagToSlot = Clamp(flow.BagToSlot - 1, 1u, bagSlots);
                    shell.RefreshPage();
                }, UiButtonKind.Normal);
            shell.ButtonIn(ops.Root, new Rect(pad + (btnW + gap) * 3f, row1, btnW, btnH), "目标 +",
                () =>
                {
                    flow.BagToSlot = Clamp(flow.BagToSlot + 1, 1u, bagSlots);
                    shell.RefreshPage();
                }, UiButtonKind.Normal);

            var wideW = (width - pad * 2f - gap) * 0.5f;
            shell.ButtonIn(ops.Root, new Rect(pad, row2, wideW, btnH), "移到目标格",
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.MoveItem(flow.BagFromSlot, flow.BagToSlot,
                        () => AfterOp(shell, flow, flow.LastMove.Ok));
                }, UiButtonKind.Primary);
            shell.ButtonIn(ops.Root, new Rect(pad + wideW + gap, row2, wideW, btnH), "排序",
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.BagFromSlot = 1;
                    flow.BagToSlot = 1;
                    flow.SortBag(shell.RefreshPage);
                }, UiButtonKind.Normal);

            // 第三行：数量 −/+ 各一格宽，剩下两半给丢弃与批量使用。
            var tailX = pad + (btnW + gap) * 2f;
            var tailW = (width - tailX - pad - gap) * 0.5f;
            shell.ButtonIn(ops.Root, new Rect(pad, row3, btnW, btnH), "数量 −",
                () =>
                {
                    flow.BagCount = Math.Max(1, flow.BagCount - 1);
                    shell.RefreshPage();
                }, UiButtonKind.Normal);
            shell.ButtonIn(ops.Root, new Rect(pad + btnW + gap, row3, btnW, btnH), "数量 +",
                () =>
                {
                    flow.BagCount = flow.BagCount + 1;
                    shell.RefreshPage();
                }, UiButtonKind.Normal);

            shell.ButtonIn(ops.Root, new Rect(tailX, row3, tailW, btnH), "丢弃 " + flow.BagCount + " 个",
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    // 二次确认：点这个按钮只弹窗，弹窗里再点一次才发请求。
                    shell.ShowConfirm("丢弃物品",
                        "第 " + flow.BagFromSlot + " 格的 " + flow.BagCount
                        + " 个会被销毁，丢掉的东西找不回来。",
                        "确认丢弃",
                        () => flow.DiscardItem(flow.BagFromSlot, flow.BagCount,
                            () => AfterOp(shell, flow, flow.LastDiscard.Ok)));
                }, UiButtonKind.Normal);
            shell.ButtonIn(ops.Root, new Rect(tailX + tailW + gap, row3, tailW, btnH),
                "用 " + flow.BagCount + " 个",
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.UseItemBatch(flow.BagFromSlot, flow.BagCount,
                        () => AfterOp(shell, flow, flow.LastUseBatch.Ok));
                }, UiButtonKind.Primary);

            // B9：穿 / 脱。一件装备当前穿在身上就写「脱下」，否则写「穿上」——
            // 标签跟着 /inventory 的 equipped_slot 走，界面不另记一份穿戴状态。
            var worn = WornSlot(flow, flow.BagFromSlot);
            var equipLabel = worn.Length > 0
                ? "脱下 " + CharacterBag.SlotName(worn)
                : "穿上这一件";
            shell.ButtonIn(ops.Root, new Rect(pad, row4, width - pad * 2f, btnH), equipLabel,
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    if (worn.Length > 0)
                    {
                        flow.UnequipItem(worn, () =>
                        {
                            // 脱下会改回包的 equipped_slot，成功就重读背包。
                            AfterOp(shell, flow, flow.LastUnequip.Ok);
                        });
                        return;
                    }

                    flow.EquipItem(flow.BagFromSlot,
                        () => AfterOp(shell, flow, flow.LastEquip.Ok));
                }, UiButtonKind.Primary);
        }

        /// <summary>B9：这一格正穿在哪个部位；没穿返回空串。只认回包里的字段。</summary>
        static string WornSlot(UiUpgradeFlow flow, uint slot)
        {
            if (!flow.HasBag || flow.Bag.Slots == null)
            {
                return string.Empty;
            }

            var slots = flow.Bag.Slots;
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i].Slot == slot)
                {
                    return slots[i].EquippedSlot ?? string.Empty;
                }
            }

            return string.Empty;
        }

        /// <summary>这一格穿着什么：部位叫法来自回包，没穿就写「没穿」。</summary>
        static string WornText(UiUpgradeFlow flow, uint slot)
        {
            var worn = WornSlot(flow, slot);
            return worn.Length > 0 ? CharacterBag.SlotName(worn) : "没穿";
        }

        /// <summary>装备固定攻击之和：只有服务端回包里给过才有数，没请求过写「尚未请求」。</summary>
        static string EquipAttackText(UiUpgradeFlow flow)
        {
            if (flow.HasEquip)
            {
                return flow.LastEquip.EquipAttack.ToString();
            }

            return flow.HasUnequip ? flow.LastUnequip.EquipAttack.ToString() : "尚未请求";
        }

        /// <summary>成功才重读背包（格号变了）；被拒时只重画结果行，不多发一次只读请求。</summary>
        static void AfterOp(UiShell shell, UiUpgradeFlow flow, bool ok)
        {
            if (ok)
            {
                flow.LoadBag(shell.RefreshPage);
                return;
            }

            shell.RefreshPage();
        }

        /// <summary>这一格现在有几个：只认回包里的数量，没这一格就是 0。</summary>
        static int HeldCount(UiUpgradeFlow flow, uint slot)
        {
            if (!flow.HasBag || flow.Bag.Slots == null)
            {
                return 0;
            }

            var slots = flow.Bag.Slots;
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i].Slot == slot)
                {
                    return slots[i].Count;
                }
            }

            return 0;
        }

        /// <summary>最近一条回包说明。一次都没发过就写「尚未请求」。</summary>
        static string LastText(UiUpgradeFlow flow)
        {
            return string.IsNullOrEmpty(flow.LastOpText) ? "尚未请求" : flow.LastOpText;
        }

        static uint Clamp(uint value, uint low, uint high)
        {
            if (value < low)
            {
                return low;
            }

            return value > high ? high : value;
        }
    }
}
