using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 强化转移（原型 D04）。选来源格与目标格，发一次 `/character/transfer`。
    /// 等级高低、费用、绑定变化全看回包；被拒时未生效的等级与数值按回包原样显示。
    /// </summary>
    public static class UiTransferScreen
    {
        public const string ReadyLog = "u5_transfer_screen=ready";

        public static void Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell, UiUpgradeCodex.TransferNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var colW = (m.Width - pad * 2f - gap) * 0.5f;

            var fromSlot = 0u;
            var toSlot = 0u;

            var from = shell.Panel("来源", m.Band(pad, top + m.Px(56f), colW, m.Px(420f)), shell.Size(32f));
            var to = shell.Panel("目标", m.Band(pad + colW + gap, top + m.Px(56f), colW, m.Px(420f)), shell.Size(32f));
            var rows = flow.HasBag ? UiUpgradeCodex.FirstSlots(flow.Bag, 4) : new List<Mmo.Net.BagSlotView>();
            if (rows.Count > 0)
            {
                fromSlot = rows[0].Slot;
                toSlot = rows.Count > 1 ? rows[1].Slot : rows[0].Slot;
            }

            BuildPicker(shell, from, rows, colW, 0, slot => fromSlot = slot, "来源");
            BuildPicker(shell, to, rows, colW, rows.Count > 1 ? 1 : 0, slot => toSlot = slot, "目标");

            var hint = shell.Label(
                new Rect(pad, m.Px(300f), m.Width - pad * 2f, m.Px(40f)),
                flow.HasBag ? "来源第 " + fromSlot + " 格 → 目标第 " + toSlot + " 格" : "尚未读背包",
                UiTextRole.Faint, shell.Size(24f), TextAnchor.MiddleLeft, false);

            shell.Button(
                new Rect(m.Width * 0.5f - m.Px(160f), m.Px(236f), m.Px(320f), m.Px(76f)),
                "确认转移",
                () =>
                {
                    if (flow.Busy || fromSlot == 0 || toSlot == 0)
                    {
                        return;
                    }

                    hint.Label.text = "已提交，等回包";
                    flow.Transfer(fromSlot, toSlot, shell.RefreshPage);
                }, UiButtonKind.Primary);

            var result = shell.Panel("回包", m.Band(pad, m.Px(560f), m.Width - pad * 2f, m.Px(300f)),
                shell.Size(32f));
            var t = flow.LastTransfer;
            var has = flow.HasTransfer;
            UiUpgradeViews.Result(shell, result, m.Px(200f), "来源 / 目标",
                has ? t.FromLevelBefore + " → " + t.ToLevelBefore : "尚未请求");
            UiUpgradeViews.Result(shell, result, m.Px(150f), "结果",
                has ? (t.Ok ? "已转移：" + t.FromLevel + " / " + t.ToLevel : "被拒：" + t.Reason) : "尚未请求");
            UiUpgradeViews.Result(shell, result, m.Px(100f), "金币 / 转移石",
                has ? t.GoldCost + " / " + t.StoneCost + "（剩 " + t.StoneLeft + "）" : "—");
            UiUpgradeViews.Result(shell, result, m.Px(50f), "绑定",
                has ? (t.TargetBoundBefore ? "true" : "false") + " → " + (t.TargetBound ? "true" : "false") : "—");

            Debug.Log(ReadyLog + " from=" + fromSlot + " to=" + toSlot + " rows=" + rows.Count);
        }

        static void BuildPicker(UiShell shell, UiPanel panel, List<Mmo.Net.BagSlotView> rows,
            float width, int selected, System.Action<uint> onPick, string tag)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var opt = new UiOption(
                    panel.Root,
                    new Rect(shell.Metrics.Px(24f), shell.Metrics.Px(64f) + i * shell.Metrics.Px(80f),
                        width - shell.Metrics.Px(48f), shell.Metrics.Px(68f)),
                    row.Slot.ToString(), "第 " + row.Slot + " 格",
                    row.ItemId + " × " + row.Count,
                    UiOptionKind.Card, shell.Size(26f), shell.Size(20f));
                opt.Selected = i == selected;
                var slot = row.Slot;
                opt.OnPick = picked =>
                {
                    onPick(slot);
                    Debug.Log("u5_transfer_pick " + tag + "=" + slot);
                };
                shell.Register(opt);
            }
        }
    }
}
