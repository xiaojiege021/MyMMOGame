using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U5 七屏共用的小排版件：顶部一行说明、左栏卡片、清单行、结果行。
    /// 只摆位置与文字，不判任何玩法；颜色一律交给骨架的色板。
    /// </summary>
    public static class UiUpgradeViews
    {
        /// <summary>屏顶那一行说明。文案来自 <see cref="UiUpgradeCodex"/>。</summary>
        public static void Note(UiShell shell, string text)
        {
            var m = shell.Metrics;
            var pad = m.Px(120f);
            shell.Label(new Rect(pad, m.Height - m.Px(160f) - m.Px(40f), m.Width - pad * 2f, m.Px(40f)),
                text, UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);
        }

        /// <summary>左栏卡片：从顶往下排若干行「标签 · 值」。</summary>
        public static void Rows(UiShell shell, UiPanel panel, string[] labels, string[] values,
            float rowHeight, float pad)
        {
            var h = panel.Root.sizeDelta.y;
            for (var i = 0; i < labels.Length; i++)
            {
                var y = h - pad - (i + 1) * rowHeight;
                shell.LabelIn(panel.Root, new Rect(pad, y, panel.Root.sizeDelta.x * 0.38f, rowHeight),
                    labels[i], UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(panel.Root,
                    new Rect(pad + panel.Root.sizeDelta.x * 0.4f, y,
                        panel.Root.sizeDelta.x * 0.56f, rowHeight),
                    i < values.Length ? values[i] : "", UiTextRole.Body, shell.Size(24f),
                    TextAnchor.MiddleLeft, false);
            }
        }

        /// <summary>一行结果。值优先用回包里的，没回包就写「尚未请求」。</summary>
        public static void Result(UiShell shell, UiPanel panel, float y, string label, string value)
        {
            var pad = shell.Metrics.Px(28f);
            var w = panel.Root.sizeDelta.x;
            shell.LabelIn(panel.Root, new Rect(pad, y, w * 0.34f, shell.Metrics.Px(40f)),
                label, UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);
            shell.LabelIn(panel.Root, new Rect(pad + w * 0.34f, y, w * 0.6f, shell.Metrics.Px(40f)),
                value, UiTextRole.Body, shell.Size(24f), TextAnchor.MiddleLeft, false);
        }
    }
}
