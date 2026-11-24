using System;
using Mmo.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 竞技场与战场（原型 F03）。左边讲三层战力怎么分开，右边是 N5 已经落地的两条进场口。
    ///
    /// 进场走 `POST /versus/arena/enter` 与 `POST /versus/battlefield/enter`：屏上只发
    /// 角色号、模式编号、场次与队伍，回来的进场路径、这一场多少人、职业分支照抄。
    /// 界面不算战力、不乘平衡系数、不排队、不做匹配算法，也不读配置。
    /// </summary>
    public static class UiArenaScreen
    {
        public const string ReadyLog = "u7_arena_screen=ready";

        public const string WiredLog = "u12_versus_wired=ready";

        public static void Build(UiShell shell, UiSocialFlow flow)
        {
            UiUpgradeViews.Note(shell, UiSocialCodex.VersusNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(16f);
            var leftW = (m.Width - pad * 2f - m.Px(40f)) * 0.46f;
            var rightX = pad + leftW + m.Px(40f);
            var rightW = m.Width - pad - rightX;

            // ---------------------------------------------------------- 左：三层

            var layersH = m.Px(84f) + UiSocialCodex.Layers.Length * m.Px(96f) + m.Px(16f);
            var layers = shell.Panel(UiStrings.UiArenaScreen.T001, m.Band(pad, top + m.Px(56f), leftW, layersH),
                shell.Size(30f));
            for (var i = 0; i < UiSocialCodex.Layers.Length; i++)
            {
                var row = UiSocialCodex.Layers[i];
                var y = layersH - m.Px(84f) - m.Px(88f) - i * m.Px(96f);
                shell.LabelIn(layers.Root, new Rect(m.Px(28f), y + m.Px(38f), leftW - m.Px(56f), m.Px(36f)),
                    row[0], UiTextRole.Body, shell.Size(24f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(layers.Root, new Rect(m.Px(28f), y, leftW - m.Px(56f), m.Px(34f)),
                    row[1], UiTextRole.Muted, shell.Size(21f), TextAnchor.MiddleLeft, false);
            }

            var rewardTop = top + m.Px(56f) + layersH + gap;
            var reward = shell.Panel(UiStrings.UiArenaScreen.T002, m.Band(pad, rewardTop, leftW, m.Px(220f)), shell.Size(30f));
            shell.LabelIn(reward.Root, new Rect(m.Px(28f), m.Px(28f), leftW - m.Px(56f), m.Px(140f)),
                UiSocialCodex.VersusReward, UiTextRole.Body, shell.Size(23f), TextAnchor.UpperLeft, true);

            var ruleTop = rewardTop + m.Px(220f) + gap;
            var rule = shell.Panel(UiStrings.UiArenaScreen.T003, m.Band(pad, ruleTop, leftW, m.Px(280f)), shell.Size(30f));
            shell.LabelIn(rule.Root, new Rect(m.Px(28f), m.Px(152f), leftW - m.Px(56f), m.Px(100f)),
                UiSocialCodex.VersusEnterNote, UiTextRole.Warm, shell.Size(22f), TextAnchor.UpperLeft, true);
            shell.LabelIn(rule.Root, new Rect(m.Px(28f), m.Px(96f), leftW - m.Px(56f), m.Px(40f)),
                UiStrings.UiArenaScreen.T004,
                UiTextRole.Muted, shell.Size(21f), TextAnchor.MiddleLeft, true);
            shell.LabelIn(rule.Root, new Rect(m.Px(28f), m.Px(44f), leftW - m.Px(56f), m.Px(40f)),
                UiStrings.UiArenaScreen.T005,
                UiTextRole.Muted, shell.Size(21f), TextAnchor.MiddleLeft, true);

            // ---------------------------------------------------------- 右：四个入场

            var modes = new[]
            {
                UiStrings.UiArenaScreen.T006,
                UiStrings.UiArenaScreen.T007,
                UiStrings.UiArenaScreen.T008,
                UiStrings.UiArenaScreen.T009,
            };
            var notes = new[]
            {
                UiStrings.UiArenaScreen.T010,
                UiStrings.UiArenaScreen.T011,
                UiStrings.UiArenaScreen.T012,
                UiStrings.UiArenaScreen.T013,
            };

            var cardH = m.Px(148f);
            var listTitleH = m.Px(56f);
            var listH = cardH * modes.Length + m.Px(28f) + listTitleH;
            var list = shell.Panel(UiStrings.UiArenaScreen.T014, m.Band(rightX, top + m.Px(56f), rightW, listH), shell.Size(30f));
            for (var i = 0; i < modes.Length; i++)
            {
                var y = listH - m.Px(84f) - (i + 1) * cardH + m.Px(24f);
                shell.LabelIn(list.Root, new Rect(m.Px(28f), y + m.Px(46f), rightW - m.Px(56f), m.Px(36f)),
                    modes[i], UiTextRole.Body, shell.Size(25f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(list.Root, new Rect(m.Px(28f), y, rightW - m.Px(56f), m.Px(34f)),
                    notes[i], UiTextRole.Muted, shell.Size(21f), TextAnchor.MiddleLeft, false);

                var buttonY = y + m.Px(34f);
                if (i < UiSocialCodex.ArenaModeIds.Length)
                {
                    var mode = UiSocialCodex.ArenaModeIds[i];
                    shell.ButtonIn(list.Root,
                        new Rect(rightW - m.Px(28f) - m.Px(200f), buttonY, m.Px(200f), m.Px(56f)),
                        UiStrings.UiArenaScreen.T015,
                        () => flow.EnterArena(mode, () => shell.RefreshPage()),
                        UiButtonKind.Primary);
                }
                else if (i == 2)
                {
                    var match = UiSocialCodex.BattlefieldMatchIds[0];
                    for (var t = 0; t < UiSocialCodex.BattlefieldTeams.Length; t++)
                    {
                        var team = UiSocialCodex.BattlefieldTeams[t];
                        shell.ButtonIn(list.Root,
                            new Rect(rightW - m.Px(28f) - (UiSocialCodex.BattlefieldTeams.Length - t)
                                     * m.Px(112f) - t * m.Px(12f), buttonY, m.Px(112f), m.Px(56f)),
                            team.ToUpperInvariant() + UiStrings.UiArenaScreen.T016,
                            () => flow.EnterBattlefield(match, team, () => shell.RefreshPage()),
                            t == 0 ? UiButtonKind.Primary : UiButtonKind.Normal);
                    }
                }
                else
                {
                    shell.Chip(list.Root,
                        new Rect(rightW - m.Px(28f) - m.Px(170f), buttonY + m.Px(6f), m.Px(170f), m.Px(44f)),
                        UiStrings.UiArenaScreen.T017, UiChipKind.Mute);
                }
            }

            // ---------------------------------------------------------- 右：回包事实

            var factsTop = top + m.Px(56f) + listH + gap;
            var factsH = m.Px(420f);
            var facts = shell.Panel(UiStrings.UiArenaScreen.T018, m.Band(rightX, factsTop, rightW, factsH), shell.Size(30f));
            DrawFacts(shell, flow, facts, rightW, factsH);

            Debug.Log(ReadyLog + " modes=" + modes.Length
                + " layers=" + UiSocialCodex.Layers.Length
                + " requests=" + flow.Requests);
            Debug.Log(WiredLog + " has_arena=" + flow.HasArena
                + " has_battlefield=" + flow.HasBattlefield
                + " arena_tried=" + flow.ArenaTried
                + " battlefield_tried=" + flow.BattlefieldTried
                + " arena_mode=" + (flow.HasArena ? flow.LastArena.ArenaMode : string.Empty)
                + " party_size=" + (flow.HasArena ? flow.LastArena.PartySize : 0)
                + " battle_path=" + (flow.HasArena ? flow.LastArena.BattlePath
                    : flow.HasBattlefield ? flow.LastBattlefield.BattlePath : string.Empty));
        }

        /// <summary>
        /// 右：两条口各自的回包事实。进场路径、模式人数、场次与队伍、职业分支都照抄；
        /// 没发过请求写「尚未请求」，发过没拿到 200 写回执，回包 ok=false 就照抄原因。
        /// </summary>
        static void DrawFacts(UiShell shell, UiSocialFlow flow, UiPanel panel, float width, float height)
        {
            var m = shell.Metrics;
            var lineH = m.Px(84f);

            var arenaText = flow.HasArena
                ? UiStrings.UiArenaScreen.T019 + VersusEntrySummary(flow.LastArena)
                : (flow.ArenaTried ? UiStrings.UiArenaScreen.T019 + flow.Error : UiStrings.UiArenaScreen.T020);
            var battleText = flow.HasBattlefield
                ? UiStrings.UiArenaScreen.T021 + VersusEntrySummary(flow.LastBattlefield)
                : (flow.BattlefieldTried ? UiStrings.UiArenaScreen.T021 + flow.Error : UiStrings.UiArenaScreen.T022);

            shell.LabelIn(panel.Root, new Rect(m.Px(28f), height - m.Px(160f), width - m.Px(56f), lineH),
                arenaText,
                flow.HasArena && flow.LastArena.Ok ? UiTextRole.Body : UiTextRole.Warm,
                shell.Size(22f), TextAnchor.UpperLeft, true);
            shell.LabelIn(panel.Root, new Rect(m.Px(28f), height - m.Px(248f), width - m.Px(56f), lineH),
                battleText,
                flow.HasBattlefield && flow.LastBattlefield.Ok ? UiTextRole.Body : UiTextRole.Warm,
                shell.Size(22f), TextAnchor.UpperLeft, true);

            var latest = flow.HasBattlefield ? flow.LastBattlefield : flow.LastArena;
            var any = flow.HasArena || flow.HasBattlefield;
            shell.LabelIn(panel.Root, new Rect(m.Px(28f), m.Px(92f), width - m.Px(56f), m.Px(44f)),
                any ? VersusEntry.ModeLine(latest) : UiStrings.UiArenaScreen.T023,
                UiTextRole.Faint, shell.Size(20f), TextAnchor.MiddleLeft, true);

            var tried = flow.ArenaTried || flow.BattlefieldTried;
            shell.Chip(panel.Root,
                new Rect(m.Px(28f), m.Px(24f), m.Px(320f), m.Px(52f)),
                any ? UiStrings.UiArenaScreen.T024 : (tried ? UiStrings.UiArenaScreen.T025 : UiStrings.UiArenaScreen.T026),
                any ? UiChipKind.Success : (tried ? UiChipKind.Error : UiChipKind.Mute));
        }

        /// <summary>一条回包的排版：成功的照抄事实，失败的照抄原因。</summary>
        static string VersusEntrySummary(VersusOutcome outcome)
        {
            return VersusEntry.SummaryLine(outcome);
        }
    }
}
