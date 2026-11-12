using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 背包（原型 D02）。只列 `/inventory` 回包里的格号、物品编号、数量与绑定态；
    /// 分类筛选需要服务端给物品种类，本卡不改协议，所以先按格号顺序平铺，不本地猜种类。
    /// </summary>
    public static class UiBagScreen
    {
        public const string ReadyLog = "u5_bag_screen=ready";

        public static int Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell,
                "背包 · 格号 / 数量 / 绑定态来自 /inventory 回包；分类以服务端物品表为准，界面不本地分");

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
            var side = shell.Panel("容量", m.Band(rightX, top + m.Px(56f), rightW, m.Px(320f)), shell.Size(32f));
            UiUpgradeViews.Rows(shell, side,
                new[] { "容量", "已用", "来源" },
                new[]
                {
                    flow.HasBag ? flow.Bag.BagSlots.ToString() : "—",
                    flow.HasBag && flow.Bag.Slots != null ? flow.Bag.Slots.Count.ToString() : "—",
                    "GET /inventory",
                },
                m.Px(64f), m.Px(32f));

            Debug.Log(ReadyLog + " rows=" + (rows != null ? rows.Count : 0)
                + " total=" + (flow.HasBag && flow.Bag.Slots != null ? flow.Bag.Slots.Count : 0));
            return rows != null ? rows.Count : 0;
        }
    }
}
