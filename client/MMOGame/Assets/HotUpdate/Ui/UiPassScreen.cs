using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 赛季通行证（原型 E04）。这一张卡在后面的 P3-4a2，界面只占位：
    /// 不请求、不显示进度、不显示奖励，避免把没定下来的玩法画成已实现。
    /// </summary>
    public static class UiPassScreen
    {
        public const string ReadyLog = "u6_pass_screen=ready";

        public static void Build(UiShell shell, UiCommerceFlow flow)
        {
            UiUpgradeViews.Note(shell, UiCommerceCodex.PassNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var card = shell.Panel("赛季通行证", m.Band(pad, top + m.Px(56f), m.Width - pad * 2f, m.Px(360f)),
                shell.Size(32f));
            shell.LabelIn(card.Root,
                new Rect(m.Px(32f), m.Px(140f), m.Width - pad * 2f - m.Px(64f), m.Px(120f)),
                "免费轨与付费轨、月叠与第二角色栏都在后面的卡。这一屏只留入口，不做请求。",
                UiTextRole.Faint, shell.Size(26f), TextAnchor.MiddleLeft, true);

            Debug.Log(ReadyLog + " placeholder=true");
        }
    }
}
