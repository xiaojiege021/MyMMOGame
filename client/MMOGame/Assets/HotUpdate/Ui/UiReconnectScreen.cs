using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U16 重连屏（掉线回原场 + 逃跑惩罚倒计时）。
    ///
    /// 倒计时那一行只有一个来源：`/dungeon/enter` 被拒回包里的 `deserter_remaining_s`。
    /// 屏上不写「还剩几分几秒」以外的任何时间——宽限窗与窗口时长都在服务端，
    /// 客户端折时区、拿进场时刻相减都会与服务端对不上。
    /// </summary>
    public static class UiReconnectScreen
    {
        public static int Build(UiShell shell, UiReconnectFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.56f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;
            var rowTop = top + m.Px(64f);

            shell.Label(new Rect(pad, m.Height - top - m.Px(40f), m.Width - pad * 2f, m.Px(40f)),
                UiReconnectCodex.Notice, UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);

            // ---------------------------------------------------------- 左：惩罚倒计时

            var penaltyH = m.Px(320f);
            var penalty = shell.Panel(UiStrings.UiReconnectScreen.T001, m.Band(pad, rowTop, leftW, penaltyH), shell.Size(30f));
            var countdown = shell.LabelIn(penalty.Root,
                new Rect(m.Px(28f), penaltyH - m.Px(160f), leftW - m.Px(56f), m.Px(88f)),
                UiReconnectCodex.PenaltyLine(flow.HasFact, flow.Remaining),
                UiTextRole.Title, shell.Size(46f), TextAnchor.MiddleLeft, false);
            flow.BindCountdown(countdown);

            shell.LabelIn(penalty.Root,
                new Rect(m.Px(28f), m.Px(36f), leftW - m.Px(56f), penaltyH - m.Px(220f)),
                UiStrings.UiReconnectScreen.T002
                + UiStrings.UiReconnectScreen.T003,
                UiTextRole.Faint, shell.Size(23f), TextAnchor.UpperLeft, true);

            // ---------------------------------------------------------- 右：重连

            var reconnect = shell.Panel(UiStrings.UiReconnectScreen.T004, m.Band(rightX, rowTop, rightW, m.Px(320f)), shell.Size(30f));
            var line = shell.LabelIn(reconnect.Root,
                new Rect(m.Px(28f), m.Px(150f), rightW - m.Px(56f), m.Px(120f)),
                UiReconnectCodex.ReconnectLine(flow.Tried, flow.Error),
                flow.Error.Length == 0 ? UiTextRole.Body : UiTextRole.Danger, shell.Size(26f),
                TextAnchor.UpperLeft, true);
            shell.ButtonIn(reconnect.Root, new Rect(m.Px(28f), m.Px(36f), rightW - m.Px(56f), m.Px(88f)),
                UiStrings.UiReconnectScreen.T005, () => { flow.ReconnectNow(); shell.RefreshPage(); }, UiButtonKind.Primary);

            // ---------------------------------------------------------- 底：口径

            var footTop = rowTop + m.Px(320f) + m.Px(24f);
            shell.Label(new Rect(pad, m.Height - footTop - m.Px(96f), m.Width - pad * 2f, m.Px(96f)),
                UiReconnectCodex.Footer, UiTextRole.Faint, shell.Size(22f), TextAnchor.UpperLeft, true);

            Debug.Log(UiReconnectCodex.ReadyLog + " remaining=" + flow.Remaining
                + " has_fact=" + (flow.HasFact ? "true" : "false")
                + " tried=" + (flow.Tried ? "true" : "false")
                + " line=" + line.Label.text);
            return flow.Remaining;
        }
    }
}
