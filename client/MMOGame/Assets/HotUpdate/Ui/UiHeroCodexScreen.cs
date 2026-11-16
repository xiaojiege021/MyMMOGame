using System;
using Mmo.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 英灵图鉴（原型 F05）。左边九格是 `/hero-codex` 回包给的条目，右边是回包事实与口径。
    ///
    /// 名字、收没收到、每位与合计的被动万分比全部照抄角色服；没有回包就写「尚未请求」，
    /// 不给格子、不按主线场次自己推、不补编一位回包里没有的人。
    /// </summary>
    public static class UiHeroCodexScreen
    {
        public const string ReadyLog = "u7_codex_screen=ready";

        public const string WiredLog = "u11_codex_wired=ready";

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

            DrawGrid(shell, flow, pad, top + m.Px(56f), leftW);

            // ---------------------------------------------------------- 右：回包事实

            var factsTop = top + m.Px(56f);
            var factsH = m.Px(300f);
            var facts = shell.Panel("图鉴回包", m.Band(rightX, factsTop, rightW, factsH), shell.Size(30f));
            DrawFacts(shell, flow, facts, rightW, factsH);

            // ---------------------------------------------------------- 右：口径

            var ruleTop = factsTop + factsH + gap;
            var rule = shell.Panel("口径", m.Band(rightX, ruleTop, rightW, m.Px(240f)), shell.Size(30f));
            var rules = new[]
            {
                UiSocialCodex.CodexNote,
                "收集度由主线进度在服务端推，界面不按场次自己算",
                "被动只作生活协助，不进伤害公式；灵兽同理",
            };
            for (var i = 0; i < rules.Length; i++)
            {
                shell.LabelIn(rule.Root,
                    new Rect(m.Px(28f), m.Px(150f) - i * m.Px(52f), rightW - m.Px(56f), m.Px(46f)),
                    rules[i], UiTextRole.Body, shell.Size(20f), TextAnchor.MiddleLeft, true);
            }

            // ---------------------------------------------------------- 右：外观收藏

            var wardrobeTop = ruleTop + m.Px(240f) + gap;
            var wardrobe = shell.Panel("外观收藏", m.Band(rightX, wardrobeTop, rightW, m.Px(200f)),
                shell.Size(30f));
            shell.LabelIn(wardrobe.Root, new Rect(m.Px(28f), m.Px(28f), rightW - m.Px(56f), m.Px(120f)),
                UiSocialCodex.WardrobeRule, UiTextRole.Muted, shell.Size(22f), TextAnchor.UpperLeft, true);

            Debug.Log(ReadyLog
                + " entries=" + (flow.HasCodex ? flow.LastCodex.Entries.Count : 0)
                + " requests=" + flow.Requests);
            Debug.Log(WiredLog + " has_codex=" + flow.HasCodex
                + " entry_count=" + (flow.HasCodex ? flow.LastCodex.EntryCount : 0)
                + " collected=" + (flow.HasCodex ? flow.LastCodex.CollectedCount : 0)
                + " collection_pct_bps=" + (flow.HasCodex ? flow.LastCodex.CollectionPctBps : 0)
                + " total_passive_pct_bps=" + (flow.HasCodex ? flow.LastCodex.TotalPassivePctBps : 0));
        }

        /// <summary>
        /// 左：九格。格数与名字只按回包给的那几条画；没回包时不画格子，
        /// 服务端没给条目时也不拿原型里的名字顶上。
        /// </summary>
        static void DrawGrid(UiShell shell, UiSocialFlow flow, float x, float top, float width)
        {
            var m = shell.Metrics;
            var cols = 3;
            var rows = 3;
            var cellW = (width - m.Px(56f) - m.Px(20f) * (cols - 1)) / cols;
            var cellH = m.Px(150f);
            var gridH = m.Px(100f) + rows * cellH + m.Px(20f) * (rows - 1) + m.Px(36f);

            var grid = shell.Panel("已收录到的名字", m.Band(x, top, width, gridH), shell.Size(30f));

            if (!flow.HasCodex)
            {
                var tried = flow.CodexTried;
                shell.LabelIn(grid.Root,
                    new Rect(m.Px(28f), m.Px(260f), width - m.Px(56f), m.Px(90f)),
                    tried
                        ? flow.Error
                        : "尚未请求 · 点「读取图鉴」走 /hero-codex",
                    tried ? UiTextRole.Warm : UiTextRole.Muted,
                    shell.Size(23f), TextAnchor.UpperLeft, true);
                shell.ButtonIn(grid.Root,
                    new Rect(m.Px(28f), m.Px(150f), m.Px(240f), m.Px(72f)),
                    tried ? "再读一次" : "读取图鉴",
                    () => flow.LoadHeroCodex(0, () => shell.RefreshPage()),
                    tried ? UiButtonKind.Quiet : UiButtonKind.Primary);
                return;
            }

            var codex = flow.LastCodex;
            var count = codex.Entries != null ? codex.Entries.Count : 0;

            shell.LabelIn(grid.Root,
                new Rect(m.Px(28f), m.Px(20f), width - m.Px(56f), m.Px(40f)),
                count == 0
                    ? "回包里没有条目 · 不补编一位"
                    : ("回包 " + count + " 条 · 未定的格子留空，不补编条目"),
                UiTextRole.Faint, shell.Size(20f), TextAnchor.MiddleLeft, false);

            if (count == 0)
            {
                shell.LabelIn(grid.Root,
                    new Rect(m.Px(28f), m.Px(260f), width - m.Px(56f), m.Px(100f)),
                    codex.Ok ? "服务端这次没给条目（entry_count=" + codex.EntryCount + "）"
                             : ("服务端：" + codex.Reason),
                    UiTextRole.Warm, shell.Size(22f), TextAnchor.UpperLeft, true);
                shell.ButtonIn(grid.Root,
                    new Rect(m.Px(28f), m.Px(150f), m.Px(240f), m.Px(72f)),
                    "再读一次",
                    () => flow.LoadHeroCodex(0, () => shell.RefreshPage()),
                    UiButtonKind.Quiet);
                return;
            }

            for (var i = 0; i < count; i++)
            {
                var entry = HeroCodexEntry.EntryAt(codex, i);
                if (entry == null)
                {
                    continue;
                }

                var col = i % cols;
                var row = i / cols;
                var cellX = m.Px(28f) + col * (cellW + m.Px(20f));
                var cellY = gridH - m.Px(100f) - (row + 1) * cellH - row * m.Px(20f) + m.Px(24f);

                shell.LabelIn(grid.Root, new Rect(cellX, cellY + m.Px(46f), cellW, m.Px(46f)),
                    entry.Value.Name, UiTextRole.Body, shell.Size(25f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(grid.Root, new Rect(cellX, cellY + m.Px(14f), cellW, m.Px(34f)),
                    HeroCodexEntry.EntryLine(entry.Value), UiTextRole.Faint, shell.Size(19f),
                    TextAnchor.MiddleLeft, false);
                shell.Divider(grid.Root, new Rect(cellX, cellY, cellW, m.Px(2f)));
            }

            shell.ButtonIn(grid.Root,
                new Rect(width - m.Px(300f), m.Px(12f), m.Px(272f), m.Px(56f)),
                "再读一次",
                () => flow.LoadHeroCodex(0, () => shell.RefreshPage()),
                UiButtonKind.Quiet);
        }

        /// <summary>
        /// 右：回包事实。收集多少、万分比多少、被动合计多少、主线场次多少，全是回包里的数，
        /// 这里不重算一遍，也不把万分比换算成别的说法。
        /// </summary>
        static void DrawFacts(UiShell shell, UiSocialFlow flow, UiPanel panel, float width, float height)
        {
            var m = shell.Metrics;

            if (!flow.HasCodex)
            {
                var tried = flow.CodexTried;
                shell.LabelIn(panel.Root,
                    new Rect(m.Px(28f), height - m.Px(140f), width - m.Px(56f), m.Px(90f)),
                    tried
                        ? flow.Error
                        : "还没有回包：收集度、被动合计都不在这里给。",
                    tried ? UiTextRole.Warm : UiTextRole.Muted,
                    shell.Size(22f), TextAnchor.UpperLeft, true);
                shell.Chip(panel.Root,
                    new Rect(m.Px(28f), m.Px(48f), m.Px(240f), m.Px(52f)),
                    tried ? "没读到" : "尚未请求",
                    tried ? UiChipKind.Error : UiChipKind.Mute);
                return;
            }

            var codex = flow.LastCodex;
            shell.LabelIn(panel.Root,
                new Rect(m.Px(28f), height - m.Px(120f), width - m.Px(56f), m.Px(84f)),
                HeroCodexEntry.SummaryLine(codex),
                codex.Ok ? UiTextRole.Body : UiTextRole.Warm,
                shell.Size(22f), TextAnchor.UpperLeft, true);

            shell.LabelIn(panel.Root,
                new Rect(m.Px(28f), m.Px(150f), width - m.Px(56f), m.Px(40f)),
                "主线场次 main_stage = " + codex.MainStage
                + " · 收集由服务端按场次推，界面不算",
                UiTextRole.Faint, shell.Size(19f), TextAnchor.MiddleLeft, false);

            shell.LabelIn(panel.Root,
                new Rect(m.Px(28f), m.Px(110f), width - m.Px(56f), m.Px(40f)),
                "被动种类 passive_kind = "
                + (codex.PassiveKind.Length == 0 ? "（回包没带）" : codex.PassiveKind)
                + " · 不进伤害公式",
                UiTextRole.Faint, shell.Size(19f), TextAnchor.MiddleLeft, false);

            shell.Chip(panel.Root,
                new Rect(m.Px(28f), m.Px(32f), m.Px(280f), m.Px(52f)),
                codex.Ok ? "已接 /hero-codex" : ("ok=false · " + codex.Reason),
                codex.Ok ? UiChipKind.Success : UiChipKind.Error);
        }
    }
}
