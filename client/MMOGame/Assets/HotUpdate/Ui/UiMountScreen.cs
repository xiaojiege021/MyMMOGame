using System.Collections.Generic;
using System.Globalization;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 坐骑栏（U21，B4 的界面欠项）。当前骑的是哪一只、速度多少、背包里一共有哪几只
    /// 全照抄 `GET /character/mounts`；「骑上」只 POST `/character/mount`（角色号 + 格号），
    /// 「下坐骑」只 POST `/character/dismount`（角色号）。
    ///
    /// 客户端不判档位、不算速度加成、不把万分比折成百分比，也不读坐骑表；
    /// 骑没骑上由服务端写定，成功后按只读口重画。
    /// </summary>
    public static class UiMountScreen
    {
        public const string ReadyLog = "u21_mount_screen=ready";

        /// <summary>一屏最多摆几只：多的不画，但仍说明回包一共几只（这是排版事实，不是玩法判定）。</summary>
        const int MaxRows = 6;

        public static void Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell, UiUpgradeCodex.MountNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(16f);
            var leftW = (m.Width - pad * 2f - m.Px(40f)) * 0.36f;
            var rightX = pad + leftW + m.Px(40f);
            var rightW = m.Width - pad - rightX;

            var board = flow.LastMounts;
            var shown = flow.HasMounts;
            var notYet = UiStrings.UiMountScreen.T001;

            // ---------------------------------------------------------- 左：现在骑的

            var leftH = m.Px(600f);
            var current = shell.Panel(UiStrings.UiMountScreen.T002,
                m.Band(pad, top + m.Px(56f), leftW, leftH), shell.Size(34f));

            // 没骑时 equipped_item_id 是空串、speed 是 0——回包本来就这样，不是错误。
            var riding = shown && !string.IsNullOrEmpty(board.EquippedItemId);
            UiUpgradeViews.Rows(shell, current,
                new[]
                {
                    UiStrings.UiMountScreen.T003,
                    UiStrings.UiMountScreen.T004,
                    UiStrings.UiMountScreen.T005,
                },
                new[]
                {
                    shown ? (riding ? board.EquippedItemId : UiStrings.UiMountScreen.T006) : notYet,
                    shown
                        ? (riding ? BpsText(board.EquippedSpeedBps) : UiStrings.UiMountScreen.T006)
                        : notYet,
                    shown ? CountText(board.Owned.Count) : notYet,
                },
                m.Px(72f), m.Px(28f));

            if (riding)
            {
                shell.ButtonIn(current.Root,
                    new Rect(m.Px(28f), m.Px(36f), m.Px(220f), m.Px(76f)),
                    UiStrings.UiMountScreen.T007,
                    () =>
                    {
                        if (flow.Busy)
                        {
                            return;
                        }

                        flow.Dismount(shell.RefreshPage);
                    }, UiButtonKind.Primary);
            }

            shell.ButtonIn(current.Root,
                new Rect(m.Px(28f) + m.Px(240f), m.Px(36f), m.Px(200f), m.Px(76f)),
                UiStrings.UiMountScreen.T008,
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.LoadMounts(shell.RefreshPage);
                }, UiButtonKind.Quiet);

            // ---------------------------------------------------------- 右上：背包里的坐骑

            var owned = shown ? board.Owned : new List<MountFact>();
            var listRowH = m.Px(96f);
            var listH = m.Px(120f) + Mathf.Max(Mathf.Min(owned.Count, MaxRows), 1) * listRowH;
            var list = shell.Panel(UiStrings.UiMountScreen.T009,
                m.Band(rightX, top + m.Px(56f), rightW, listH), shell.Size(34f));

            if (owned.Count == 0)
            {
                shell.LabelIn(list.Root,
                    new Rect(m.Px(28f), listH - m.Px(160f), rightW - m.Px(56f), m.Px(40f)),
                    shown ? UiStrings.UiMountScreen.T010 : notYet, UiTextRole.Muted,
                    shell.Size(24f), TextAnchor.MiddleLeft, false);
            }

            var drawn = Mathf.Min(owned.Count, MaxRows);
            for (var i = 0; i < drawn; i++)
            {
                var one = owned[i];
                var y = listH - m.Px(120f) - (i + 1) * listRowH + m.Px(20f);

                shell.LabelIn(list.Root, new Rect(m.Px(28f), y + m.Px(46f), rightW - m.Px(56f), m.Px(34f)),
                    NameText(one), UiTextRole.Body, shell.Size(26f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(list.Root, new Rect(m.Px(28f), y + m.Px(8f), rightW * 0.62f, m.Px(32f)),
                    DetailText(one), UiTextRole.Muted, shell.Size(21f), TextAnchor.MiddleLeft, false);

                var rect = new Rect(rightW - m.Px(28f) - m.Px(160f), y + m.Px(20f), m.Px(160f), m.Px(56f));
                if (one.Mounted)
                {
                    // mounted 是回包给的布尔，客户端不拿 item_id 跟 equipped 比。
                    shell.Chip(list.Root, rect, UiStrings.UiMountScreen.T011, UiChipKind.Success);
                }
                else
                {
                    var slot = one.BagSlot;
                    shell.ButtonIn(list.Root, rect, UiStrings.UiMountScreen.T012,
                        () =>
                        {
                            if (flow.Busy)
                            {
                                return;
                            }

                            flow.MountUp(slot, shell.RefreshPage);
                        }, UiButtonKind.Normal);
                }
            }

            if (owned.Count > drawn)
            {
                shell.LabelIn(list.Root, new Rect(m.Px(28f), m.Px(40f), rightW - m.Px(56f), m.Px(36f)),
                    HiddenText(owned.Count, drawn), UiTextRole.Faint, shell.Size(21f),
                    TextAnchor.MiddleLeft, false);
            }

            // ---------------------------------------------------------- 右下：骑乘回包

            var act = flow.LastMountAct;
            var resultH = m.Px(300f);
            var result = shell.Panel(UiStrings.UiMountScreen.T013,
                m.Band(rightX, top + m.Px(56f) + listH + gap, rightW, resultH), shell.Size(34f));
            UiUpgradeViews.Result(shell, result, m.Px(206f), UiStrings.UiMountScreen.T014,
                flow.HasMountAct ? ActText(act) : UiStrings.UiMountScreen.T015);
            UiUpgradeViews.Result(shell, result, m.Px(150f), UiStrings.UiMountScreen.T016,
                flow.HasMountAct ? act.ItemId : UiStrings.UiMountScreen.T015);
            UiUpgradeViews.Result(shell, result, m.Px(94f), UiStrings.UiMountScreen.T017,
                flow.HasMountAct ? BpsText(act.SpeedBps) : UiStrings.UiMountScreen.T015);
            UiUpgradeViews.Result(shell, result, m.Px(38f), UiStrings.UiMountScreen.T018,
                flow.HasMountAct ? SlotText(act.BagSlot) : UiStrings.UiMountScreen.T015);

            Debug.Log(ReadyLog + " shown=" + (shown ? "true" : "false")
                + " equipped=" + board.EquippedItemId
                + " speed_bps=" + board.EquippedSpeedBps
                + " owned=" + owned.Count
                + " act=" + (flow.HasMountAct ? (act.Ok ? "ok" : "reject") : "none"));
        }

        static string NameText(MountFact one) =>
            string.IsNullOrEmpty(one.Name) ? one.ItemId : one.Name;

        /// <summary>「advanced · 4500 万分比 · 第 3 格」——全是回包字段，只拼串不换算。</summary>
        static string DetailText(MountFact one) =>
            one.Tier + UiStrings.UiMountScreen.T019 + BpsText(one.SpeedBps) + UiStrings.UiMountScreen.T020
            + SlotText(one.BagSlot);

        static string BpsText(int bps) =>
            bps.ToString(CultureInfo.InvariantCulture) + UiStrings.UiMountScreen.T021;

        static string SlotText(int slot) =>
            UiStrings.UiMountScreen.T022 + slot.ToString(CultureInfo.InvariantCulture) + UiStrings.UiMountScreen.T023;

        static string CountText(int count) =>
            count.ToString(CultureInfo.InvariantCulture) + UiStrings.UiMountScreen.T024;

        static string HiddenText(int total, int drawn) =>
            UiStrings.UiMountScreen.T025 + drawn.ToString(CultureInfo.InvariantCulture)
            + UiStrings.UiMountScreen.T026 + total.ToString(CultureInfo.InvariantCulture)
            + UiStrings.UiMountScreen.T024;

        static string ActText(MountActionOutcome act) =>
            act.Ok ? UiStrings.UiMountScreen.T027 : UiStrings.UiMountScreen.T028 + act.Reason;
    }
}
