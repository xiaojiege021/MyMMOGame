using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 英灵图鉴（原型 F05）。左边九格名字，右边是收集口径与现状。
    ///
    /// 没有收集度与图鉴接口，所以每格只写名字、不写百分比、不写被动数值，
    /// 也不点开假详情：收集度留白，标明尚未接服务端。
    /// </summary>
    public static class UiHeroCodexScreen
    {
        public const string ReadyLog = "u7_codex_screen=ready";

        public static void Build(UiShell shell, UiSocialFlow flow)
        {
            UiUpgradeViews.Note(shell, UiSocialCodex.CodexRule);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(16f);
            var leftW = (m.Width - pad * 2f - m.Px(40f)) * 0.54f;
            var rightX = pad + leftW + m.Px(40f);
            var rightW = m.Width - pad - rightX;

            // ---------------------------------------------------------- 左：九格

            var cols = 3;
            var rows = (UiSocialCodex.CodexNames.Length + cols - 1) / cols;
            var cellW = (leftW - m.Px(56f) - m.Px(20f) * (cols - 1)) / cols;
            var cellH = m.Px(150f);
            var gridH = m.Px(100f) + rows * cellH + m.Px(20f) * (rows - 1) + m.Px(36f);

            var grid = shell.Panel("已收录到的名字", m.Band(pad, top + m.Px(56f), leftW, gridH),
                shell.Size(30f));
            for (var i = 0; i < UiSocialCodex.CodexNames.Length; i++)
            {
                var col = i % cols;
                var row = i / cols;
                var x = m.Px(28f) + col * (cellW + m.Px(20f));
                var y = gridH - m.Px(100f) - (row + 1) * cellH - row * m.Px(20f) + m.Px(24f);
                shell.LabelIn(grid.Root, new Rect(x, y + m.Px(46f), cellW, m.Px(46f)),
                    UiSocialCodex.CodexNames[i], UiTextRole.Body, shell.Size(25f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(grid.Root, new Rect(x, y + m.Px(14f), cellW, m.Px(34f)),
                    "收集度 —", UiTextRole.Faint, shell.Size(20f), TextAnchor.MiddleLeft, false);
                shell.Divider(grid.Root, new Rect(x, y, cellW, m.Px(2f)));
            }

            shell.LabelIn(grid.Root, new Rect(m.Px(28f), m.Px(20f), leftW - m.Px(56f), m.Px(40f)),
                "九格是原型里已经出现的名字 · 未定的格子留空，不补编条目", UiTextRole.Faint,
                shell.Size(20f), TextAnchor.MiddleLeft, false);

            // ---------------------------------------------------------- 右：口径与现状

            var gapTop = top + m.Px(56f);
            var missing = shell.Panel("尚未接服务端", m.Band(rightX, gapTop, rightW, m.Px(280f)),
                shell.Size(30f));
            shell.LabelIn(missing.Root, new Rect(m.Px(28f), m.Px(28f), rightW - m.Px(56f), m.Px(120f)),
                UiSocialCodex.CodexGap, UiTextRole.Warm, shell.Size(23f), TextAnchor.UpperLeft, true);
            shell.LabelIn(missing.Root, new Rect(m.Px(28f), m.Px(120f), rightW - m.Px(56f), m.Px(72f)),
                "收集度、被动数值、灵兽属性都不在这里给：没有回包就没有数字。",
                UiTextRole.Faint, shell.Size(21f), TextAnchor.UpperLeft, true);
            shell.Chip(missing.Root, new Rect(m.Px(28f), m.Px(200f), m.Px(240f), m.Px(52f)),
                "无接口", UiChipKind.Mute);

            var ruleTop = gapTop + m.Px(280f) + gap;
            var rule = shell.Panel("口径", m.Band(rightX, ruleTop, rightW, m.Px(240f)), shell.Size(30f));
            var rules = new[]
            {
                "收集度与被动只做收集、外观与生活协助",
                "不进入伤害公式；灵兽同理",
                "外观收藏可共享，装备与境界不共享",
            };
            for (var i = 0; i < rules.Length; i++)
            {
                shell.LabelIn(rule.Root,
                    new Rect(m.Px(28f), m.Px(140f) - i * m.Px(56f), rightW - m.Px(56f), m.Px(48f)),
                    rules[i], UiTextRole.Body, shell.Size(22f), TextAnchor.MiddleLeft, false);
            }

            var wardrobeTop = ruleTop + m.Px(240f) + gap;
            var wardrobe = shell.Panel("外观收藏", m.Band(rightX, wardrobeTop, rightW, m.Px(200f)),
                shell.Size(30f));
            shell.LabelIn(wardrobe.Root, new Rect(m.Px(28f), m.Px(28f), rightW - m.Px(56f), m.Px(120f)),
                UiSocialCodex.WardrobeRule, UiTextRole.Muted, shell.Size(22f), TextAnchor.UpperLeft, true);

            Debug.Log(ReadyLog + " names=" + UiSocialCodex.CodexNames.Length
                + " cols=" + cols
                + " requests=" + flow.Requests);
        }
    }
}
