using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 技能与分支（原型 D06）。只列方案写定的五职业与分支名；
    /// 技能表在服务端，界面不排技能、不算伤害，也不产生属性。
    /// </summary>
    public static class UiSkillScreen
    {
        public const string ReadyLog = "u5_skill_screen=ready";

        public static int Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell, UiUpgradeCodex.SkillsNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var halfW = (m.Width - pad * 2f - gap) * 0.5f;
            var cardH = m.Px(560f);

            var roles = shell.Panel("职业", m.Band(pad, top + m.Px(56f), halfW, cardH), shell.Size(34f));
            var roleLabels = new string[UiUpgradeCodex.Classes.Length];
            var roleValues = new string[UiUpgradeCodex.Classes.Length];
            for (var i = 0; i < UiUpgradeCodex.Classes.Length; i++)
            {
                roleLabels[i] = "职业 " + (i + 1);
                roleValues[i] = UiUpgradeCodex.Classes[i];
            }

            UiUpgradeViews.Rows(shell, roles, roleLabels, roleValues, m.Px(72f), m.Px(32f));

            var rightX = pad + halfW + gap;
            var branches = shell.Panel("分支", m.Band(rightX, top + m.Px(56f), halfW, cardH), shell.Size(34f));
            var branchLabels = new string[UiUpgradeCodex.Branches.Length];
            var branchValues = new string[UiUpgradeCodex.Branches.Length];
            for (var i = 0; i < UiUpgradeCodex.Branches.Length; i++)
            {
                branchLabels[i] = "分支 " + (i + 1);
                branchValues[i] = UiUpgradeCodex.Branches[i];
            }

            UiUpgradeViews.Rows(shell, branches, branchLabels, branchValues, m.Px(72f), m.Px(32f));

            Debug.Log(ReadyLog + " classes=" + UiUpgradeCodex.Classes.Length
                + " branches=" + UiUpgradeCodex.Branches.Length);
            return UiUpgradeCodex.Classes.Length + UiUpgradeCodex.Branches.Length;
        }
    }
}
