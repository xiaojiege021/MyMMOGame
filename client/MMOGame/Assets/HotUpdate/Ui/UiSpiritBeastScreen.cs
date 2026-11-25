using System.Globalization;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 灵兽图鉴（U21，B4 的界面欠项）。收集度、图鉴条数、万分比与生活协助合计
    /// 全照抄 `GET /character/spirit-beasts`；这一屏只有一条只读口，没有写动作。
    ///
    /// 灵兽只做收集 / 外观 / 生活协助，不进伤害公式：客户端不补条目、不删条目、
    /// 不按档位重排、不把收集度折算成百分比，也不读灵兽表。
    /// </summary>
    public static class UiSpiritBeastScreen
    {
        public const string ReadyLog = "u21_beast_screen=ready";

        /// <summary>一屏最多摆几行图鉴：多的不画，仍说明回包一共几条（排版事实）。</summary>
        const int MaxRows = 8;

        public static void Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell, UiUpgradeCodex.BeastNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var leftW = (m.Width - pad * 2f - m.Px(40f)) * 0.34f;
            var rightX = pad + leftW + m.Px(40f);
            var rightW = m.Width - pad - rightX;

            var board = flow.LastBeasts;
            var shown = flow.HasBeasts;
            var notYet = UiStrings.UiSpiritBeastScreen.T001;

            // ---------------------------------------------------------- 左：收集度与被动

            var leftH = m.Px(600f);
            var facts = shell.Panel(UiStrings.UiSpiritBeastScreen.T002,
                m.Band(pad, top + m.Px(56f), leftW, leftH), shell.Size(34f));

            // 已收集 / 条数、万分比、生活协助合计、被动口径：四个数都是服务端算好的，
            // 界面只拼单位，不做除法也不折成百分比。
            UiUpgradeViews.Rows(shell, facts,
                new[]
                {
                    UiStrings.UiSpiritBeastScreen.T003,
                    UiStrings.UiSpiritBeastScreen.T004,
                    UiStrings.UiSpiritBeastScreen.T005,
                    UiStrings.UiSpiritBeastScreen.T006,
                    UiStrings.UiSpiritBeastScreen.T007,
                },
                new[]
                {
                    shown ? CountText(board.CollectedCount) : notYet,
                    shown ? CountText(board.EntryCount) : notYet,
                    shown ? BpsText(board.CollectionPctBps) : notYet,
                    shown ? BpsText(board.TotalLifeAssistPctBps) : notYet,
                    shown ? board.PassiveKind : notYet,
                },
                m.Px(72f), m.Px(28f));

            shell.ButtonIn(facts.Root,
                new Rect(m.Px(28f), m.Px(36f), m.Px(220f), m.Px(76f)),
                UiStrings.UiSpiritBeastScreen.T008,
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.LoadSpiritBeasts(shell.RefreshPage);
                }, UiButtonKind.Primary);

            // ---------------------------------------------------------- 右：图鉴清单

            var entries = shown ? board.Entries : new System.Collections.Generic.List<SpiritBeastFact>();
            var rowH = m.Px(76f);
            var drawn = Mathf.Min(entries.Count, MaxRows);
            var listH = m.Px(120f) + Mathf.Max(drawn, 1) * rowH;
            var list = shell.Panel(UiStrings.UiSpiritBeastScreen.T009,
                m.Band(rightX, top + m.Px(56f), rightW, listH), shell.Size(34f));

            if (entries.Count == 0)
            {
                shell.LabelIn(list.Root,
                    new Rect(m.Px(28f), listH - m.Px(160f), rightW - m.Px(56f), m.Px(40f)),
                    shown ? UiStrings.UiSpiritBeastScreen.T010 : notYet, UiTextRole.Muted,
                    shell.Size(24f), TextAnchor.MiddleLeft, false);
            }

            for (var i = 0; i < drawn; i++)
            {
                var one = entries[i];
                var y = listH - m.Px(120f) - (i + 1) * rowH + m.Px(16f);

                shell.LabelIn(list.Root, new Rect(m.Px(28f), y + m.Px(36f), rightW * 0.42f, m.Px(32f)),
                    NameText(one), UiTextRole.Body, shell.Size(25f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(list.Root,
                    new Rect(m.Px(28f) + rightW * 0.42f, y + m.Px(36f), rightW * 0.3f, m.Px(32f)),
                    DetailText(one), UiTextRole.Muted, shell.Size(21f), TextAnchor.MiddleLeft, false);

                // 收没收集只认回包布尔；顺序就是回包顺序，不按档位或收集状态重排。
                var rect = new Rect(rightW - m.Px(28f) - m.Px(150f), y + m.Px(14f), m.Px(150f), m.Px(52f));
                shell.Chip(list.Root, rect,
                    one.Collected ? UiStrings.UiSpiritBeastScreen.T011 : UiStrings.UiSpiritBeastScreen.T012,
                    one.Collected ? UiChipKind.Success : UiChipKind.Mute);
            }

            if (entries.Count > drawn)
            {
                shell.LabelIn(list.Root, new Rect(m.Px(28f), m.Px(40f), rightW - m.Px(56f), m.Px(36f)),
                    HiddenText(entries.Count, drawn), UiTextRole.Faint, shell.Size(21f),
                    TextAnchor.MiddleLeft, false);
            }

            Debug.Log(ReadyLog + " shown=" + (shown ? "true" : "false")
                + " collected=" + board.CollectedCount
                + " entries=" + board.EntryCount
                + " rows=" + entries.Count
                + " bps=" + board.CollectionPctBps
                + " life_bps=" + board.TotalLifeAssistPctBps
                + " passive=" + board.PassiveKind);
        }

        static string NameText(SpiritBeastFact one) =>
            string.IsNullOrEmpty(one.Name) ? one.ItemId : one.Name;

        static string DetailText(SpiritBeastFact one) =>
            one.Tier + UiStrings.UiSpiritBeastScreen.T013 + BpsText(one.LifeAssistPctBps);

        static string BpsText(int bps) =>
            bps.ToString(CultureInfo.InvariantCulture) + UiStrings.UiSpiritBeastScreen.T014;

        static string CountText(int count) =>
            count.ToString(CultureInfo.InvariantCulture) + UiStrings.UiSpiritBeastScreen.T015;

        static string HiddenText(int total, int drawn) =>
            UiStrings.UiSpiritBeastScreen.T016 + drawn.ToString(CultureInfo.InvariantCulture)
            + UiStrings.UiSpiritBeastScreen.T017 + total.ToString(CultureInfo.InvariantCulture)
            + UiStrings.UiSpiritBeastScreen.T015;
    }
}
