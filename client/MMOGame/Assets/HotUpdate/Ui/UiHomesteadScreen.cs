using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 家园洞府（原型 F04）。左边四段（生活技能、城镇 NPC、家园、时间结构），
    /// 右边是这一屏的现状与边界。
    ///
    /// 没有家园 / 种植的接口，所以屏上不编家园币、产量、摆件数值，也不给
    /// 假的「收取」按钮：只把方案写定的口径摆出来，并标明尚未接服务端。
    /// </summary>
    public static class UiHomesteadScreen
    {
        public const string ReadyLog = "u7_homestead_screen=ready";

        public static void Build(UiShell shell, UiSocialFlow flow)
        {
            UiUpgradeViews.Note(shell, UiSocialCodex.HomesteadRule);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(16f);
            var leftW = (m.Width - pad * 2f - m.Px(40f)) * 0.5f;
            var rightX = pad + leftW + m.Px(40f);
            var rightW = m.Width - pad - rightX;

            // ---------------------------------------------------------- 左：四段

            var cardH = m.Px(196f);
            for (var i = 0; i < UiSocialCodex.Homestead.Length; i++)
            {
                var row = UiSocialCodex.Homestead[i];
                var y = top + m.Px(56f) + (UiSocialCodex.Homestead.Length - 1 - i) * (cardH + gap);
                var card = shell.Panel(row[0], m.Band(pad, y, leftW, cardH), shell.Size(30f));
                shell.LabelIn(card.Root, new Rect(m.Px(28f), m.Px(28f), leftW - m.Px(56f), m.Px(80f)),
                    row[1], UiTextRole.Muted, shell.Size(22f), TextAnchor.UpperLeft, true);
            }

            // ---------------------------------------------------------- 右：现状

            var gapTop = top + m.Px(56f);
            var missing = shell.Panel("尚未接服务端", m.Band(rightX, gapTop, rightW, m.Px(300f)),
                shell.Size(30f));
            shell.LabelIn(missing.Root, new Rect(m.Px(28f), m.Px(28f), rightW - m.Px(56f), m.Px(120f)),
                UiSocialCodex.HomesteadGap, UiTextRole.Warm, shell.Size(23f), TextAnchor.UpperLeft, true);
            shell.LabelIn(missing.Root, new Rect(m.Px(28f), m.Px(120f), rightW - m.Px(56f), m.Px(80f)),
                "家园币、产量、摆件加成一律留白：没有回包就没有数字，界面上不出现占位数字。",
                UiTextRole.Faint, shell.Size(21f), TextAnchor.UpperLeft, true);
            shell.Chip(missing.Root, new Rect(m.Px(28f), m.Px(212f), m.Px(240f), m.Px(52f)),
                "无接口", UiChipKind.Mute);

            var ruleTop = gapTop + m.Px(300f) + gap;
            var rule = shell.Panel("口径", m.Band(rightX, ruleTop, rightW, m.Px(240f)), shell.Size(30f));
            var rules = new[]
            {
                "家园币只换外观与便捷，不换金币、材料、副本次数",
                "摆件与装饰只做展示，不进伤害公式",
                "外观收藏可与第二角色栏共享，装备与境界不共享",
            };
            for (var i = 0; i < rules.Length; i++)
            {
                shell.LabelIn(rule.Root,
                    new Rect(m.Px(28f), m.Px(140f) - i * m.Px(56f), rightW - m.Px(56f), m.Px(48f)),
                    rules[i], UiTextRole.Body, shell.Size(22f), TextAnchor.MiddleLeft, false);
            }

            var timeTop = ruleTop + m.Px(240f) + gap;
            var time = shell.Panel("时间结构", m.Band(rightX, timeTop, rightW, m.Px(200f)), shell.Size(30f));
            shell.LabelIn(time.Root, new Rect(m.Px(28f), m.Px(28f), rightW - m.Px(56f), m.Px(120f)),
                "大世界、挖矿、采药、家园、钓鱼、外观收集都不设日限周限；\n"
                + "副本与团本才按次数算，这部分见「副本 / 团本」屏。",
                UiTextRole.Body, shell.Size(22f), TextAnchor.UpperLeft, true);

            Debug.Log(ReadyLog + " cards=" + UiSocialCodex.Homestead.Length
                + " requests=" + flow.Requests);
        }
    }
}
