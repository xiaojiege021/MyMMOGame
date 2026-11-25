using System.Globalization;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 觉醒屏（U21，B6 的界面欠项）。分支、等级、觉醒技编号与觉醒时刻照抄
    /// `GET /character/awaken`；点「觉醒」只 POST `/character/awaken`
    /// （请求体只有角色号与回包里那条分支，事务号由网关生成）。
    ///
    /// 客户端不判等级够不够、不判分支对不对、不自己把状态改成已觉醒，
    /// 也不读觉醒配置表——技能编号与门槛都在角色服。
    /// </summary>
    public static class UiAwakenScreen
    {
        public const string ReadyLog = "u21_awaken_screen=ready";

        public static void Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell, UiUpgradeCodex.AwakenNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var leftW = (m.Width - pad * 2f - m.Px(40f)) * 0.46f;
            var rightX = pad + leftW + m.Px(40f);
            var rightW = m.Width - pad - rightX;

            var state = flow.LastAwaken;
            var shown = flow.HasAwaken;
            var notYet = UiStrings.UiAwakenScreen.T001;

            // ---------------------------------------------------------- 左：觉醒状态

            var leftH = m.Px(600f);
            var facts = shell.Panel(UiStrings.UiAwakenScreen.T002,
                m.Band(pad, top + m.Px(56f), leftW, leftH), shell.Size(34f));

            // 六行都是回包事实；没觉醒时「觉醒技」「觉醒时刻」「状态」写「未觉醒」，
            // 这是 skill_id = 0 这个字段的有无，不是本地推出来的结论。
            var awakened = shown && state.Awakened;
            UiUpgradeViews.Rows(shell, facts,
                new[]
                {
                    UiStrings.UiAwakenScreen.T003,
                    UiStrings.UiAwakenScreen.T004,
                    UiStrings.UiAwakenScreen.T005,
                    UiStrings.UiAwakenScreen.T006,
                    UiStrings.UiAwakenScreen.T007,
                    UiStrings.UiAwakenScreen.T008,
                },
                new[]
                {
                    shown ? state.Branch : notYet,
                    shown ? state.Level.ToString(CultureInfo.InvariantCulture) : notYet,
                    shown ? (awakened ? SkillText(state.SkillId) : UiStrings.UiAwakenScreen.T009) : notYet,
                    shown ? (awakened ? TimeText(state.AwakenAtMs) : UiStrings.UiAwakenScreen.T009) : notYet,
                    shown ? (awakened ? UiStrings.UiAwakenScreen.T010 : UiStrings.UiAwakenScreen.T009) : notYet,
                    shown ? UiStrings.UiAwakenScreen.T011 : notYet,
                },
                m.Px(72f), m.Px(28f));

            // 按钮只在「状态读到了、还没觉醒、分支不为空」时给：分支取回包里服务端写下的那条，
            // 客户端不提供选分支。
            var canAwaken = shown && !awakened && !string.IsNullOrEmpty(state.Branch);
            if (canAwaken)
            {
                var branch = state.Branch;
                shell.ButtonIn(facts.Root,
                    new Rect(m.Px(28f), m.Px(36f), m.Px(220f), m.Px(76f)),
                    UiStrings.UiAwakenScreen.T012,
                    () =>
                    {
                        if (flow.Busy)
                        {
                            return;
                        }

                        flow.Awaken(branch, shell.RefreshPage);
                    }, UiButtonKind.Primary);
            }
            else if (shown && awakened)
            {
                shell.Chip(facts.Root, new Rect(m.Px(28f), m.Px(36f), m.Px(260f), m.Px(64f)),
                    UiStrings.UiAwakenScreen.T013, UiChipKind.Success);
            }

            shell.ButtonIn(facts.Root,
                new Rect(m.Px(28f) + m.Px(280f), m.Px(36f), m.Px(200f), m.Px(76f)),
                UiStrings.UiAwakenScreen.T014,
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.LoadAwaken(shell.RefreshPage);
                }, UiButtonKind.Quiet);

            // ---------------------------------------------------------- 右：觉醒回包

            var act = flow.LastAwakenAct;
            var resultH = m.Px(520f);
            var result = shell.Panel(UiStrings.UiAwakenScreen.T015,
                m.Band(rightX, top + m.Px(56f), rightW, resultH), shell.Size(34f));

            UiUpgradeViews.Result(shell, result, m.Px(432f), UiStrings.UiAwakenScreen.T016,
                flow.HasAwakenAct ? ActText(act) : UiStrings.UiAwakenScreen.T019);
            UiUpgradeViews.Result(shell, result, m.Px(376f), UiStrings.UiAwakenScreen.T003,
                flow.HasAwakenAct ? act.Branch : UiStrings.UiAwakenScreen.T019);
            UiUpgradeViews.Result(shell, result, m.Px(320f), UiStrings.UiAwakenScreen.T017,
                flow.HasAwakenAct ? SkillText(act.SkillId) : UiStrings.UiAwakenScreen.T019);
            UiUpgradeViews.Result(shell, result, m.Px(264f), UiStrings.UiAwakenScreen.T004,
                flow.HasAwakenAct ? act.Level.ToString(CultureInfo.InvariantCulture) : UiStrings.UiAwakenScreen.T019);
            UiUpgradeViews.Result(shell, result, m.Px(208f), UiStrings.UiAwakenScreen.T018,
                flow.HasAwakenAct
                    ? (act.Duplicate ? UiStrings.UiAwakenScreen.T021 : UiStrings.UiAwakenScreen.T022)
                    : UiStrings.UiAwakenScreen.T019);

            // 一条口径：觉醒一次性、不可逆，客户端不提供「换分支」这个动作。
            shell.LabelIn(result.Root, new Rect(m.Px(28f), m.Px(96f), rightW - m.Px(56f), m.Px(84f)),
                UiStrings.UiAwakenScreen.T023, UiTextRole.Faint, shell.Size(21f), TextAnchor.UpperLeft, true);

            Debug.Log(ReadyLog + " shown=" + (shown ? "true" : "false")
                + " branch=" + state.Branch
                + " skill=" + state.SkillId
                + " level=" + state.Level
                + " act=" + (flow.HasAwakenAct ? (act.Ok ? "ok" : "reject") : "none"));
        }

        /// <summary>「#1004」。技能编号只从回包来，界面不查名字表。</summary>
        static string SkillText(int skillId) =>
            UiStrings.UiAwakenScreen.T024 + skillId.ToString(CultureInfo.InvariantCulture);

        /// <summary>服务端毫秒原样显示，不在客户端折时区。</summary>
        static string TimeText(long atMs) =>
            atMs.ToString(CultureInfo.InvariantCulture) + UiStrings.UiAwakenScreen.T025;

        /// <summary>结果那一行：成功写「已生效」，被拒把原因原样带出来。</summary>
        static string ActText(AwakenOutcome act) =>
            act.Ok
                ? (act.Duplicate ? UiStrings.UiAwakenScreen.T021 : UiStrings.UiAwakenScreen.T022)
                : UiStrings.UiAwakenScreen.T020 + act.Reason;
    }
}
