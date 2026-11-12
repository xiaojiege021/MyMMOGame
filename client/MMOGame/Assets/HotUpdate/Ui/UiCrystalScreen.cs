using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 能量结晶（原型 D05）。选装备格、选背包里的结晶格、选镶嵌位，
    /// 装上 / 拆下都只发请求；类别、部位、位数量与结果全看回包。
    /// 装上与拆下都不改强化、金币与绑定——这三个值也从回包里读，界面不自己推。
    /// </summary>
    public static class UiCrystalScreen
    {
        public const string ReadyLog = "u5_crystal_screen=ready";

        public static void Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell, UiUpgradeCodex.CrystalNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var colW = (m.Width - pad * 2f - gap) * 0.5f;

            var equipSlot = 0u;
            var crystalSlot = 0u;
            var socket = 1u;
            var rows = flow.HasBag ? UiUpgradeCodex.FirstSlots(flow.Bag, 4) : new List<Mmo.Net.BagSlotView>();
            if (rows.Count > 0)
            {
                equipSlot = rows[0].Slot;
                crystalSlot = rows.Count > 1 ? rows[1].Slot : rows[0].Slot;
            }

            var equip = shell.Panel("装备格", m.Band(pad, top + m.Px(56f), colW, m.Px(360f)), shell.Size(32f));
            var crystal = shell.Panel("结晶格", m.Band(pad + colW + gap, top + m.Px(56f), colW, m.Px(360f)),
                shell.Size(32f));
            BuildPicker(shell, equip, rows, colW, 0, slot => equipSlot = slot, "equip");
            BuildPicker(shell, crystal, rows, colW, rows.Count > 1 ? 1 : 0, slot => crystalSlot = slot, "crystal");

            var sockets = shell.Panel("镶嵌位", m.Band(pad, top + m.Px(56f) + m.Px(400f), m.Width - pad * 2f, m.Px(180f)),
                shell.Size(32f));
            for (var i = 0; i < 3; i++)
            {
                var index = i;
                var opt = new UiOption(
                    sockets.Root,
                    new Rect(m.Px(24f) + i * m.Px(240f), m.Px(60f), m.Px(210f), m.Px(72f)),
                    (i + 1).ToString(), "第 " + (i + 1) + " 位", "",
                    UiOptionKind.Chip, shell.Size(26f), shell.Size(20f));
                opt.Selected = i == 0;
                opt.OnPick = picked =>
                {
                    socket = (uint)(index + 1);
                    Debug.Log("u5_crystal_socket=" + socket);
                };
                shell.Register(opt);
            }

            shell.Button(
                new Rect(m.Width * 0.5f - m.Px(360f), m.Px(300f), m.Px(320f), m.Px(72f)),
                "装上结晶",
                () =>
                {
                    if (flow.Busy || equipSlot == 0 || crystalSlot == 0)
                    {
                        return;
                    }

                    flow.InstallCrystal(equipSlot, crystalSlot, socket, shell.RefreshPage);
                }, UiButtonKind.Primary);

            shell.Button(
                new Rect(m.Width * 0.5f + m.Px(40f), m.Px(300f), m.Px(320f), m.Px(72f)),
                "拆下结晶",
                () =>
                {
                    if (flow.Busy || equipSlot == 0)
                    {
                        return;
                    }

                    flow.RemoveCrystal(equipSlot, socket, shell.RefreshPage);
                }, UiButtonKind.Normal);

            var result = shell.Panel("回包", m.Band(pad, m.Px(620f), m.Width - pad * 2f, m.Px(420f)),
                shell.Size(32f));
            var c = flow.LastCrystal;
            var has = flow.HasCrystal;
            UiUpgradeViews.Result(shell, result, m.Px(310f), "结果",
                has ? (c.Ok ? (c.Installed ? "已装上" : "已拆下") : "被拒：" + c.Reason) : "尚未请求");
            UiUpgradeViews.Result(shell, result, m.Px(258f), "类别 / 部位",
                has ? c.Category + " / " + c.Part : "—");
            UiUpgradeViews.Result(shell, result, m.Px(206f), "镶嵌位",
                has ? c.Socket + " / " + c.SocketCount + "（已填 " + c.Filled + "）" : "—");
            UiUpgradeViews.Result(shell, result, m.Px(154f), "背包结晶",
                has ? c.CrystalItem + " · 剩 " + c.CrystalLeft : "—");
            UiUpgradeViews.Result(shell, result, m.Px(102f), "强化 / 金币 / 绑定",
                has ? c.EnhanceLevel + " / " + c.Gold + " / " + (c.Bound ? "true" : "false") : "—");

            Debug.Log(ReadyLog + " equip=" + equipSlot + " crystal=" + crystalSlot
                + " socket=" + socket + " rows=" + rows.Count);
        }

        static void BuildPicker(UiShell shell, UiPanel panel, List<Mmo.Net.BagSlotView> rows,
            float width, int selected, System.Action<uint> onPick, string tag)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var opt = new UiOption(
                    panel.Root,
                    new Rect(shell.Metrics.Px(24f), shell.Metrics.Px(56f) + i * shell.Metrics.Px(72f),
                        width - shell.Metrics.Px(48f), shell.Metrics.Px(62f)),
                    row.Slot.ToString(), "第 " + row.Slot + " 格",
                    row.ItemId + " × " + row.Count,
                    UiOptionKind.Card, shell.Size(26f), shell.Size(20f));
                opt.Selected = i == selected;
                var slot = row.Slot;
                opt.OnPick = picked =>
                {
                    onPick(slot);
                    Debug.Log("u5_crystal_pick " + tag + "=" + slot);
                };
                shell.Register(opt);
            }
        }
    }
}
