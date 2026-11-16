using System.Globalization;
using Mmo.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 宗门（原型 F01）。左边是方案写定的规则，右边是社交服确认过的事实与这一手的请求。
    ///
    /// 名册上限、等级、人数、席位、建设、掌门全部来自 `/sect/*` 回包；没有回包时
    /// 写「尚未请求」，不拿配置顶格值假装已经确认。逐条名单只来自
    /// `GET /sect/roster`（N2）：角色号和职位按回包顺序写，不翻译职位、不补人。
    /// 没有这份回包时，名单处仍写「尚未请求」。
    /// </summary>
    public static class UiSectScreen
    {
        public const string ReadyLog = "u7_sect_screen=ready";

        /// <summary>一截放几行。只由这块面板的高度决定，不是名册上限。</summary>
        public const int RowsPerPage = 2;

        static int _rosterWindow;

        static string _rosterStamp = string.Empty;

        public static void Build(UiShell shell, UiSocialFlow flow)
        {
            UiUpgradeViews.Note(shell, UiSocialCodex.SectNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(16f);
            var leftW = (m.Width - pad * 2f - m.Px(40f)) * 0.44f;
            var rightX = pad + leftW + m.Px(40f);
            var rightW = m.Width - pad - rightX;

            // ---------------------------------------------------------- 左：写定规则

            var levelsH = m.Px(84f) + UiSocialCodex.Levels.Length * m.Px(46f) + m.Px(64f);
            var levels = shell.Panel("等级与人数", m.Band(pad, top + m.Px(56f), leftW, levelsH),
                shell.Size(30f));
            var y = levelsH - m.Px(84f) - m.Px(40f);
            for (var i = 0; i < UiSocialCodex.Levels.Length; i++)
            {
                shell.LabelIn(levels.Root, new Rect(m.Px(28f), y - i * m.Px(46f), leftW - m.Px(56f), m.Px(42f)),
                    UiSocialCodex.Levels[i], UiTextRole.Body, shell.Size(24f), TextAnchor.MiddleLeft, false);
            }

            shell.LabelIn(levels.Root, new Rect(m.Px(28f), m.Px(24f), leftW - m.Px(56f), m.Px(56f)),
                "上限是数字，只认右侧回包给的 member_cap", UiTextRole.Faint, shell.Size(21f),
                TextAnchor.MiddleLeft, false);

            var posTop = top + m.Px(56f) + levelsH + gap;
            var posH = m.Px(84f) + UiSocialCodex.Positions.Length * m.Px(46f) + m.Px(64f);
            var positions = shell.Panel("职位", m.Band(pad, posTop, leftW, posH), shell.Size(30f));
            var py = posH - m.Px(84f) - m.Px(40f);
            for (var i = 0; i < UiSocialCodex.Positions.Length; i++)
            {
                shell.LabelIn(positions.Root,
                    new Rect(m.Px(28f), py - i * m.Px(46f), leftW - m.Px(56f), m.Px(42f)),
                    UiSocialCodex.Positions[i], UiTextRole.Body, shell.Size(24f), TextAnchor.MiddleLeft, false);
            }

            shell.LabelIn(positions.Root, new Rect(m.Px(28f), m.Px(24f), leftW - m.Px(56f), m.Px(56f)),
                "席位数字由回包给，客户端不写死", UiTextRole.Faint, shell.Size(21f),
                TextAnchor.MiddleLeft, false);

            var lineTop = posTop + posH + gap;
            var lineH = m.Px(84f) + UiSocialCodex.Lines.Length * m.Px(46f) + m.Px(16f);
            var lines = shell.Panel("三条线", m.Band(pad, lineTop, leftW, lineH), shell.Size(30f));
            var ly = lineH - m.Px(84f) - m.Px(40f);
            for (var i = 0; i < UiSocialCodex.Lines.Length; i++)
            {
                shell.LabelIn(lines.Root, new Rect(m.Px(28f), ly - i * m.Px(46f), leftW - m.Px(56f), m.Px(42f)),
                    UiSocialCodex.Lines[i], UiTextRole.Body, shell.Size(23f), TextAnchor.MiddleLeft, false);
            }

            var npcTop = lineTop + lineH + gap;
            var npc = shell.Panel("宗门 NPC", m.Band(pad, npcTop, leftW, m.Px(200f)), shell.Size(30f));
            shell.LabelIn(npc.Root, new Rect(m.Px(28f), m.Px(28f), leftW - m.Px(56f), m.Px(120f)),
                UiSocialCodex.Npc, UiTextRole.Muted, shell.Size(23f), TextAnchor.UpperLeft, true);

            // ---------------------------------------------------------- 右：服务端事实

            var facts = shell.Panel("服务端事实", m.Band(rightX, top + m.Px(56f), rightW, m.Px(464f)),
                shell.Size(30f));
            var sect = flow.LastSect;
            var upgraded = flow.LastUpgrade;
            var position = flow.LastPosition;
            var leadership = flow.LastLeadership;

            UiUpgradeViews.Result(shell, facts, m.Px(360f), "宗门",
                flow.HasSect ? (sect.Ok ? sect.Name : "被拒：" + sect.Reason) : "尚未请求");
            UiUpgradeViews.Result(shell, facts, m.Px(312f), "等级",
                flow.HasSect ? "第 " + sect.Level + " 级" : "—");
            UiUpgradeViews.Result(shell, facts, m.Px(264f), "名册 / 上限",
                flow.HasSect ? sect.MemberCount + " / " + sect.MemberCap : "—");
            UiUpgradeViews.Result(shell, facts, m.Px(216f), "建设",
                flow.HasUpgrade
                    ? upgraded.ConstructionBefore + " → " + upgraded.ConstructionAfter
                        + "（扣 " + upgraded.UpgradeCost + "）"
                    : "—");
            UiUpgradeViews.Result(shell, facts, m.Px(168f), "职位席位",
                flow.HasPosition
                    ? position.Position + " · " + position.SeatCount + " / " + position.SeatCap
                        + (position.Ok ? "" : "（被拒：" + position.Reason + "）")
                    : "—");
            UiUpgradeViews.Result(shell, facts, m.Px(120f), "掌门",
                flow.HasLeadership
                    ? leadership.OldMaster + " → " + leadership.NewMaster
                        + " · 名册 " + leadership.MemberCount
                    : "—");
            UiUpgradeViews.Result(shell, facts, m.Px(72f), "最近一条", Latest(flow));

            // 两个编号是请求参数，不是事实值；用一格数组装住，点了签才算数。
            var sectChoice = new[] { UiSocialCodex.NumberChoices[0] };
            var targetChoice = new[] { UiSocialCodex.NumberChoices[0] };
            var sectId = SectIdOf(flow, sectChoice[0]);

            // ---------------------------------------------------------- 右：逐条名单

            var rosterTop = top + m.Px(56f) + m.Px(464f) + gap;
            var rosterH = m.Px(248f);
            var roster = shell.Panel("", m.Band(rightX, rosterTop, rightW, rosterH), shell.Size(30f));
            DrawRoster(shell, flow, roster, rightW, rosterH, sectChoice);

            // ---------------------------------------------------------- 右：这一手

            var actsTop = rosterTop + rosterH + gap;
            var actsH = m.Px(640f);
            var acts = shell.Panel("这一手", m.Band(rightX, actsTop, rightW, actsH), shell.Size(30f));

            var sectLine = shell.LabelIn(acts.Root,
                new Rect(m.Px(28f), m.Px(516f), rightW - m.Px(56f), m.Px(36f)),
                "宗门编号：" + sectId + "（有回包就用回包的）", UiTextRole.Muted, shell.Size(22f),
                TextAnchor.MiddleLeft, false);

            for (var i = 0; i < UiSocialCodex.NumberChoices.Length; i++)
            {
                var value = UiSocialCodex.NumberChoices[i];
                var opt = new UiOption(acts.Root,
                    new Rect(m.Px(28f) + i * m.Px(180f), m.Px(452f), m.Px(164f), m.Px(58f)),
                    value.ToString(), "宗门 " + value, "编号", UiOptionKind.Chip,
                    shell.Size(24f), shell.Size(18f));
                opt.Selected = i == 0;
                opt.OnPick = picked =>
                {
                    sectChoice[0] = value;
                    sectLine.Label.text = "宗门编号：" + SectIdOf(flow, value) + "（有回包就用回包的）";
                    Debug.Log("u7_sect_pick_sect=" + value);
                };
                shell.Register(opt);
            }

            shell.LabelIn(acts.Root, new Rect(m.Px(28f), m.Px(436f), rightW - m.Px(56f), m.Px(36f)),
                "目标角色：任命、免职、传位都用这个编号", UiTextRole.Muted, shell.Size(22f),
                TextAnchor.MiddleLeft, false);

            for (var i = 0; i < UiSocialCodex.NumberChoices.Length; i++)
            {
                var value = UiSocialCodex.NumberChoices[i];
                var opt = new UiOption(acts.Root,
                    new Rect(m.Px(28f) + i * m.Px(180f), m.Px(372f), m.Px(164f), m.Px(58f)),
                    "t" + value, "角色 " + value, "编号", UiOptionKind.Chip,
                    shell.Size(24f), shell.Size(18f));
                opt.Selected = i == 0;
                opt.OnPick = picked =>
                {
                    targetChoice[0] = value;
                    Debug.Log("u7_sect_pick_target=" + value);
                };
                shell.Register(opt);
            }

            var colW = (rightW - m.Px(56f) - m.Px(24f)) / 2f;
            var labels = new[]
            {
                "建宗", "加入", "升级", "退出宗门",
                "任命副掌门", "任命长老", "免职长老", "传位",
            };

            for (var i = 0; i < labels.Length; i++)
            {
                var index = i;
                var rect = new Rect(m.Px(28f) + (index % 2) * (colW + m.Px(24f)),
                    m.Px(288f) - (index / 2) * m.Px(88f), colW, m.Px(72f));
                var button = shell.ButtonIn(acts.Root, rect, labels[i],
                    () => Act(flow, shell, index, SectIdOf(flow, sectChoice[0]), targetChoice[0]),
                    index == 0 ? UiButtonKind.Primary : UiButtonKind.Normal);

                // 除了建宗，其余都要一个宗门编号；有回包才有编号，没有就先置灰。
                if (index != 0)
                {
                    button.Enabled = sectId != 0;
                    button.Apply(shell.Palette);
                }
            }

            Debug.Log(ReadyLog + " levels=" + UiSocialCodex.Levels.Length
                + " positions=" + UiSocialCodex.Positions.Length
                + " sect=" + sectId
                + " has_sect=" + (flow.HasSect ? "true" : "false"));
        }

        static ulong SectIdOf(UiSocialFlow flow, ulong chosen)
        {
            return flow.SectId != 0 ? flow.SectId : chosen;
        }

        static string Latest(UiSocialFlow flow)
        {
            if (flow.HasUpgrade)
            {
                return flow.LastUpgrade.DisplayText;
            }

            if (flow.HasLeadership)
            {
                return flow.LastLeadership.DisplayText;
            }

            if (flow.HasPosition)
            {
                return flow.LastPosition.DisplayText;
            }

            if (flow.HasSect)
            {
                return flow.LastSect.DisplayText;
            }

            return "尚未请求";
        }

        public static void ResetRosterWindow()
        {
            _rosterWindow = 0;
            _rosterStamp = string.Empty;
        }

        public static void StepRosterWindow(int delta, int rowCount)
        {
            var pages = Pages(rowCount);
            var next = _rosterWindow + delta;
            if (next < 0)
            {
                next = 0;
            }

            if (next >= pages)
            {
                next = pages - 1;
            }

            _rosterWindow = next;
        }

        static void DrawRoster(
            UiShell shell, UiSocialFlow flow, UiPanel roster, float width, float height, ulong[] sectChoice)
        {
            SyncWindow(flow);
            var m = shell.Metrics;
            shell.LabelIn(roster.Root,
                new Rect(m.Px(28f), height - m.Px(44f), width - m.Px(56f), m.Px(36f)),
                "逐条名单", UiTextRole.Heading, shell.Size(26f), TextAnchor.MiddleLeft, false);
            shell.LabelIn(roster.Root,
                new Rect(m.Px(28f), m.Px(168f), width - m.Px(56f), m.Px(32f)),
                RosterStatus(flow), UiTextRole.Warm, shell.Size(22f), TextAnchor.MiddleLeft, false);

            if (flow.HasRoster && flow.LastRoster.Ok && flow.LastRoster.Members != null)
            {
                var members = flow.LastRoster.Members;
                var start = _rosterWindow * RowsPerPage;
                if (start >= members.Count)
                {
                    start = 0;
                }

                var shown = 0;
                for (var i = start; i < members.Count && shown < RowsPerPage; i++, shown++)
                {
                    shell.LabelIn(roster.Root,
                        new Rect(m.Px(28f), m.Px(128f) - shown * m.Px(40f), width - m.Px(56f), m.Px(36f)),
                        SectGuild.RosterLine(members[i]),
                        UiTextRole.Body, shell.Size(22f), TextAnchor.MiddleLeft, false);
                }

                if (members.Count > RowsPerPage)
                {
                    var count = members.Count;
                    shell.ButtonIn(roster.Root,
                        new Rect(m.Px(320f), m.Px(16f), m.Px(160f), m.Px(64f)),
                        "上一截",
                        () =>
                        {
                            StepRosterWindow(-1, count);
                            shell.RefreshPage();
                        },
                        UiButtonKind.Quiet);
                    shell.ButtonIn(roster.Root,
                        new Rect(m.Px(496f), m.Px(16f), m.Px(160f), m.Px(64f)),
                        "下一截",
                        () =>
                        {
                            StepRosterWindow(1, count);
                            shell.RefreshPage();
                        },
                        UiButtonKind.Quiet);
                }
            }

            shell.ButtonIn(roster.Root,
                new Rect(m.Px(28f), m.Px(16f), m.Px(280f), m.Px(64f)),
                "读取名册",
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.LoadRoster(SectIdOf(flow, sectChoice[0]), shell.RefreshPage);
                },
                UiButtonKind.Primary);

            var rows = flow.HasRoster && flow.LastRoster.Members != null
                ? flow.LastRoster.Members.Count
                : 0;
            Debug.Log("n3_roster_screen=ready has=" + (flow.HasRoster ? "true" : "false")
                + " rows=" + rows.ToString(CultureInfo.InvariantCulture));
        }

        static void SyncWindow(UiSocialFlow flow)
        {
            var stamp = RosterStamp(flow);
            if (stamp == _rosterStamp)
            {
                return;
            }

            _rosterStamp = stamp;
            _rosterWindow = 0;
        }

        static string RosterStamp(UiSocialFlow flow)
        {
            if (!flow.HasRoster)
            {
                return "none";
            }

            var roster = flow.LastRoster;
            var n = roster.Members != null ? roster.Members.Count : 0;
            var first = n > 0 ? roster.Members[0].CharacterId : 0UL;
            var last = n > 0 ? roster.Members[n - 1].CharacterId : 0UL;
            return roster.Ok + ":" + roster.Reason + ":" + roster.SectId.ToString(CultureInfo.InvariantCulture)
                + ":" + n.ToString(CultureInfo.InvariantCulture)
                + ":" + first.ToString(CultureInfo.InvariantCulture)
                + ":" + last.ToString(CultureInfo.InvariantCulture);
        }

        static string RosterStatus(UiSocialFlow flow)
        {
            if (!flow.HasRoster)
            {
                return "尚未请求";
            }

            var roster = flow.LastRoster;
            if (!roster.Ok)
            {
                return "被拒：" + (roster.Reason ?? string.Empty);
            }

            var n = roster.Members != null ? roster.Members.Count : 0;
            if (n == 0)
            {
                return "回包 0 人";
            }

            var start = _rosterWindow * RowsPerPage;
            if (start >= n)
            {
                start = 0;
            }

            var end = start + RowsPerPage;
            if (end > n)
            {
                end = n;
            }

            return "回包 " + n.ToString(CultureInfo.InvariantCulture)
                + " 人 · 这一截 " + (start + 1).ToString(CultureInfo.InvariantCulture)
                + "-" + end.ToString(CultureInfo.InvariantCulture);
        }

        static int Pages(int rowCount)
        {
            if (rowCount <= 0)
            {
                return 1;
            }

            return (rowCount + RowsPerPage - 1) / RowsPerPage;
        }

        static void Act(UiSocialFlow flow, UiShell shell, int index, ulong sectId, ulong targetId)
        {
            if (flow.Busy)
            {
                return;
            }

            switch (index)
            {
                case 0:
                    flow.Create(UiSocialCodex.DemoSectName, shell.RefreshPage);
                    break;
                case 1:
                    flow.Join(sectId, shell.RefreshPage);
                    break;
                case 2:
                    flow.Upgrade(sectId, shell.RefreshPage);
                    break;
                case 3:
                    flow.Leave(sectId, shell.RefreshPage);
                    break;
                case 4:
                    flow.Appoint(sectId, targetId, Mmo.Net.SectGuild.ViceMaster, shell.RefreshPage);
                    break;
                case 5:
                    flow.Appoint(sectId, targetId, Mmo.Net.SectGuild.Elder, shell.RefreshPage);
                    break;
                case 6:
                    flow.Dismiss(sectId, targetId, Mmo.Net.SectGuild.Elder, shell.RefreshPage);
                    break;
                default:
                    flow.Transfer(sectId, targetId, shell.RefreshPage);
                    break;
            }
        }
    }
}
