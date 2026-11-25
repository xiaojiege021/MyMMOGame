using System.Globalization;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 竞技场赛季排行榜屏（B11 的界面欠项）。只读 `GET /arena/leaderboard`：
    /// 行顺序、名次、名字、职业、分数、档名全部照抄回包。
    ///
    /// 客户端不排序、不按分数重排名次、不自己截人数（前多少名由角色服按配置截断，
    /// 请求里不带 limit），也不读段位配置表。
    /// </summary>
    public static class UiArenaBoardScreen
    {
        public const string ReadyLog = "u19_board_screen=ready";

        public static void Build(UiShell shell, UiSocialFlow flow)
        {
            UiUpgradeViews.Note(shell, UiSocialCodex.BoardNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var width = m.Width - pad * 2f;

            var board = flow.LastBoard;
            var shown = flow.HasBoard;
            var notYet = UiStrings.UiArenaBoardScreen.T009;
            var dash = UiStrings.UiArenaBoardScreen.T011;

            var rowH = m.Px(56f);
            var rows = shown ? board.Rows.Count : 0;
            var panelH = m.Px(200f) + Mathf.Max(rows, 1) * rowH;
            var panel = shell.Panel(UiStrings.UiArenaBoardScreen.T001,
                m.Band(pad, top + m.Px(56f), width, panelH), shell.Size(34f));

            // 季与条数：都是回包字段（limit 是角色服截断后的实际条数，不是客户端要的）。
            shell.LabelIn(panel.Root, new Rect(m.Px(28f), panelH - m.Px(84f), width * 0.6f, m.Px(36f)),
                UiStrings.UiArenaBoardScreen.T002
                + (shown ? board.SeasonId.ToString(CultureInfo.InvariantCulture) : notYet)
                + UiStrings.UiArenaBoardScreen.T003
                + (shown ? board.Limit.ToString(CultureInfo.InvariantCulture) : notYet),
                UiTextRole.Muted, shell.Size(22f), TextAnchor.MiddleLeft, false);

            // 表头
            var headY = panelH - m.Px(140f);
            Head(shell, panel.Root, width, headY, UiStrings.UiArenaBoardScreen.T004,
                UiStrings.UiArenaBoardScreen.T005, UiStrings.UiArenaBoardScreen.T006,
                UiStrings.UiArenaBoardScreen.T007, UiStrings.UiArenaBoardScreen.T008);

            if (!shown)
            {
                shell.LabelIn(panel.Root, new Rect(m.Px(28f), headY - m.Px(80f), width - m.Px(56f), m.Px(40f)),
                    flow.BoardTried ? flow.Error : notYet,
                    flow.BoardTried ? UiTextRole.Warm : UiTextRole.Muted,
                    shell.Size(24f), TextAnchor.MiddleLeft, false);
            }

            for (var i = 0; i < rows; i++)
            {
                var row = board.Rows[i];
                var y = headY - m.Px(48f) - (i + 1) * rowH + m.Px(16f);

                Cell(shell, panel.Root, width, y, 0.00f, 0.10f,
                    row.Rank.ToString(CultureInfo.InvariantCulture), UiTextRole.Body);
                Cell(shell, panel.Root, width, y, 0.12f, 0.30f,
                    string.IsNullOrEmpty(row.Name)
                        ? row.CharacterId.ToString(CultureInfo.InvariantCulture)
                        : row.Name,
                    UiTextRole.Body);
                Cell(shell, panel.Root, width, y, 0.44f, 0.14f,
                    row.ClassId.ToString(CultureInfo.InvariantCulture), UiTextRole.Muted);
                Cell(shell, panel.Root, width, y, 0.60f, 0.14f,
                    row.Score.ToString(CultureInfo.InvariantCulture), UiTextRole.Body);
                Cell(shell, panel.Root, width, y, 0.76f, 0.22f,
                    ArenaRank.TierLabel(row.TierKey, row.TierName), UiTextRole.Muted);
            }

            // 按钮放标题带右侧：行多时不会压到最后一行的名次。
            shell.ButtonIn(panel.Root,
                new Rect(width - m.Px(28f) - m.Px(260f), panelH - m.Px(96f), m.Px(260f), m.Px(76f)),
                UiStrings.UiArenaBoardScreen.T010,
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.LoadLeaderboard(shell.RefreshPage);
                }, UiButtonKind.Primary);

            Debug.Log(ReadyLog + " shown=" + (shown ? "true" : "false")
                + " season=" + board.SeasonId
                + " limit=" + board.Limit
                + " rows=" + rows
                + " tried=" + (flow.BoardTried ? "true" : "false"));
        }

        /// <summary>五列的表头。</summary>
        static void Head(UiShell shell, Transform parent, float width, float y,
            string rank, string name, string job, string score, string tier)
        {
            var m = shell.Metrics;
            Cell(shell, parent, width, y, 0.00f, 0.10f, rank, UiTextRole.Faint);
            Cell(shell, parent, width, y, 0.12f, 0.30f, name, UiTextRole.Faint);
            Cell(shell, parent, width, y, 0.44f, 0.14f, job, UiTextRole.Faint);
            Cell(shell, parent, width, y, 0.60f, 0.14f, score, UiTextRole.Faint);
            Cell(shell, parent, width, y, 0.76f, 0.22f, tier, UiTextRole.Faint);
            shell.Divider(parent, new Rect(m.Px(28f), y - m.Px(10f), width - m.Px(56f), m.Px(2f)));
        }

        static void Cell(UiShell shell, Transform parent, float width, float y,
            float leftRatio, float widthRatio, string text, UiTextRole role)
        {
            var m = shell.Metrics;
            var x = m.Px(28f) + width * leftRatio;
            shell.LabelIn(parent, new Rect(x, y, width * widthRatio, m.Px(34f)),
                text, role, shell.Size(22f), TextAnchor.MiddleLeft, false);
        }
    }
}
