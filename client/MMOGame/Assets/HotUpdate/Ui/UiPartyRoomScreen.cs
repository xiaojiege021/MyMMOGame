using System.Globalization;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U14 组队屏（社交服的队伍，不是战斗服的同场名单）。
    ///
    /// 成员、队长、准备态、分配口径逐条照抄 `GET /party` 回包；没回包就写
    /// 「队伍名单还没到」，不拿自己一个人凑一队。六个写操作只发请求，
    /// 成不成、为什么不成全部照回包的 ok / reason 上屏。
    ///
    /// 与 U3 的 `UiPartyScreen` 不合并：那一屏读战斗服下发的 `party`（这一场谁在），
    /// 这一屏读社交服的队伍状态（谁在一队、谁准备好了、掉落怎么分）。两处不互相补人。
    /// </summary>
    public static class UiPartyRoomScreen
    {
        public static int Build(UiShell shell, UiPartyFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.48f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;

            shell.Label(new Rect(pad, m.Height - top - m.Px(40f), m.Width - pad * 2f, m.Px(40f)),
                UiPartyCodex.Notice, UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);

            var rowTop = top + m.Px(64f);
            var rowH = m.Px(150f);
            var rowGap = m.Px(16f);

            // ---------------------------------------------------------- 左：成员

            var shown = 0;
            var members = flow.Last.Members;
            if (members == null || members.Count == 0)
            {
                var empty = shell.Panel(null, m.Band(pad, rowTop, leftW, rowH), shell.Size(28f));
                shell.LabelIn(empty.Root, new Rect(m.Px(28f), m.Px(24f), leftW - m.Px(56f), rowH - m.Px(48f)),
                    UiPartyCodex.EmptyList, UiTextRole.Faint, shell.Size(24f), TextAnchor.MiddleLeft, true);
            }
            else
            {
                for (var i = 0; i < members.Count; i++)
                {
                    var row = members[i];
                    var panel = shell.Panel(null, m.Band(pad, rowTop + i * (rowH + rowGap), leftW, rowH),
                        shell.Size(28f));

                    shell.LabelIn(panel.Root, new Rect(m.Px(28f), rowH - m.Px(76f), leftW - m.Px(200f), m.Px(48f)),
                        UiStrings.UiPartyRoomScreen.T001 + row.CharacterId.ToString(CultureInfo.InvariantCulture),
                        UiTextRole.Title, shell.Size(30f), TextAnchor.MiddleLeft, false);

                    shell.Chip(panel.Root, new Rect(leftW - m.Px(176f), rowH - m.Px(70f), m.Px(148f), m.Px(42f)),
                        row.IsLeader ? UiStrings.UiPartyRoomScreen.T002 : UiStrings.UiPartyRoomScreen.T003,
                        row.IsLeader ? UiChipKind.Success : UiChipKind.Mute);

                    shell.LabelIn(panel.Root, new Rect(m.Px(28f), m.Px(28f), leftW - m.Px(56f), m.Px(48f)),
                        row.Ready ? UiStrings.UiPartyRoomScreen.T004 : UiStrings.UiPartyRoomScreen.T005,
                        row.Ready ? UiTextRole.Jade : UiTextRole.Faint, shell.Size(24f),
                        TextAnchor.MiddleLeft, false);

                    shown++;
                }
            }

            var footTop = rowTop + Mathf.Max(1, members == null ? 1 : members.Count) * (rowH + rowGap) + m.Px(16f);
            shell.Label(new Rect(pad, m.Height - footTop - m.Px(120f), leftW, m.Px(120f)),
                UiPartyCodex.Footer + "。\n" + UiPartyCodex.LeaderHint,
                UiTextRole.Faint, shell.Size(22f), TextAnchor.UpperLeft, true);

            // ---------------------------------------------------------- 右上：回包那一队

            var headH = m.Px(220f);
            var head = shell.Panel(UiStrings.UiPartyRoomScreen.T006, m.Band(rightX, rowTop, rightW, headH), shell.Size(30f));
            var headLine = shell.LabelIn(head.Root,
                new Rect(m.Px(28f), headH - m.Px(120f), rightW - m.Px(56f), m.Px(56f)),
                CharacterParty.HeadLine(flow.Tried, flow.Last), UiTextRole.Title, shell.Size(32f),
                TextAnchor.MiddleLeft, true);
            shell.LabelIn(head.Root,
                new Rect(m.Px(28f), m.Px(40f), rightW - m.Px(56f), m.Px(56f)),
                flow.HasReply
                    ? UiPartyCodex.SlotsLine(flow.Last.Size, flow.Last.MaxSize)
                    : UiStrings.UiPartyRoomScreen.T007,
                UiTextRole.Muted, shell.Size(26f), TextAnchor.MiddleLeft, false);

            // ---------------------------------------------------------- 右中：分配口径

            var lootTop = rowTop + headH + m.Px(20f);
            var lootH = m.Px(180f);
            var loot = shell.Panel(UiStrings.UiPartyRoomScreen.T008, m.Band(rightX, lootTop, rightW, lootH), shell.Size(30f));
            shell.LabelIn(loot.Root, new Rect(m.Px(28f), m.Px(28f), rightW - m.Px(56f), lootH - m.Px(96f)),
                flow.HasReply ? CharacterParty.LootLine(flow.Last) : UiStrings.UiPartyRoomScreen.T009,
                UiTextRole.Body, shell.Size(24f), TextAnchor.UpperLeft, true);

            // ---------------------------------------------------------- 右下：六个写操作

            var fieldTop = lootTop + lootH + m.Px(20f);
            var fieldRect = m.Band(rightX, fieldTop, rightW, m.Px(110f));
            var target = new UiTextField(shell, shell.Content, fieldRect, UiStrings.UiPartyRoomScreen.T010,
                UiStrings.UiPartyRoomScreen.T011, false, 20);

            var actTop = fieldTop + m.Px(110f) + m.Px(18f);
            var half = (rightW - m.Px(16f)) / 2f;
            var third = (rightW - m.Px(32f)) / 3f;
            var onRefresh = (System.Action)shell.RefreshPage;

            shell.ButtonIn(shell.Content, m.Band(rightX, actTop, half, m.Px(88f)), UiStrings.UiPartyRoomScreen.T012,
                () =>
                {
                    ulong id;
                    if (!ulong.TryParse(target.Text, out id) || id == 0)
                    {
                        shell.Toast(UiPartyCodex.TargetMissing);
                        return;
                    }

                    flow.Invite(id, onRefresh);
                }, UiButtonKind.Primary);

            shell.ButtonIn(shell.Content, m.Band(rightX + half + m.Px(16f), actTop, half, m.Px(88f)), UiStrings.UiPartyRoomScreen.T013,
                () => flow.StartReady(onRefresh), UiButtonKind.Normal);

            var act2 = actTop + m.Px(88f) + m.Px(12f);
            shell.ButtonIn(shell.Content, m.Band(rightX, act2, third, m.Px(88f)), UiStrings.UiPartyRoomScreen.T014,
                () => Respond(shell, flow, target, true, onRefresh), UiButtonKind.Normal);
            shell.ButtonIn(shell.Content, m.Band(rightX + third + m.Px(16f), act2, third, m.Px(88f)), UiStrings.UiPartyRoomScreen.T015,
                () => Respond(shell, flow, target, false, onRefresh), UiButtonKind.Quiet);
            shell.ButtonIn(shell.Content,
                m.Band(rightX + (third + m.Px(16f)) * 2f, act2, third, m.Px(88f)), UiStrings.UiPartyRoomScreen.T016,
                () => flow.SetReady(true, onRefresh), UiButtonKind.Normal);

            var act3 = act2 + m.Px(88f) + m.Px(12f);
            shell.ButtonIn(shell.Content, m.Band(rightX, act3, half, m.Px(88f)), UiStrings.UiPartyRoomScreen.T017,
                () => flow.Leave(onRefresh), UiButtonKind.Quiet);
            shell.ButtonIn(shell.Content, m.Band(rightX + half + m.Px(16f), act3, half, m.Px(88f)), UiStrings.UiPartyRoomScreen.T018,
                () => flow.Disband(onRefresh), UiButtonKind.Quiet);

            // ---------------------------------------------------------- 底：回执

            var errorTop = act3 + m.Px(88f) + m.Px(16f);
            shell.Label(new Rect(rightX, m.Height - errorTop - m.Px(44f), rightW, m.Px(44f)),
                flow.Error.Length == 0 ? string.Empty : flow.Error, UiTextRole.Danger, shell.Size(24f),
                TextAnchor.MiddleLeft, true);

            Debug.Log(UiPartyCodex.ReadyLog + " members=" + shown
                + " in_party=" + (flow.Last.InParty ? "true" : "false")
                + " tried=" + (flow.Tried ? "true" : "false")
                + " head=" + headLine.Label.text);
            return shown;
        }

        /// <summary>接受 / 拒绝：目标那一格填的是发起邀请的人。没填就不发请求，屏上直说。</summary>
        static void Respond(UiShell shell, UiPartyFlow flow, UiTextField target, bool accept, System.Action done)
        {
            ulong from;
            if (!ulong.TryParse(target.Text, out from) || from == 0)
            {
                shell.Toast(UiPartyCodex.TargetMissing);
                return;
            }

            flow.Respond(from, accept, done);
        }
    }
}
