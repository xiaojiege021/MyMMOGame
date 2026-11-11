using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 载入屏（原型 A03「载入」）。
    ///
    /// 进度条只作表现：自己最多走到 92%，剩下那截等服务端确认。
    /// 走满 100% 也不代表能进游戏——`UiEntryFlow.Entered` 只由回包决定，
    /// 这一屏拿不到「服务端说行」就永远停在「正在接引」。
    /// </summary>
    public sealed class UiLoadingScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u2_loading=ready";

        RectTransform _fill;

        Text _tip;

        float _barWidth;

        /// <summary>建屏，把抓手留给流程。题记由调用方从洗牌袋抽好再传进来。</summary>
        public static UiLoadingScreen Build(UiShell shell, string[] verse)
        {
            var m = shell.Metrics;
            var screen = new UiLoadingScreen();

            var centerW = m.Px(1000f);
            var centerX = (m.Width - centerW) * 0.5f;

            shell.Label(m.Band(centerX, m.Px(500f), centerW, m.Px(90f)),
                UiEntryCodex.ActOne, UiTextRole.Title, shell.Size(56f), TextAnchor.MiddleCenter, false);

            shell.Label(m.Band(centerX, m.Px(596f), centerW, m.Px(50f)),
                "正在进入表世界", UiTextRole.Muted, shell.Size(28f), TextAnchor.MiddleCenter, false);

            var first = verse != null && verse.Length > 0 ? verse[0] : UiEntryCodex.VerseFixedA;
            var second = verse != null && verse.Length > 1 ? verse[1] : UiEntryCodex.VerseFixedB;
            shell.Label(m.Band(centerX, m.Px(700f), centerW, m.Px(50f)),
                first, UiTextRole.Warm, shell.Size(30f), TextAnchor.MiddleCenter, false);
            shell.Label(m.Band(centerX, m.Px(752f), centerW, m.Px(50f)),
                second, UiTextRole.Warm, shell.Size(30f), TextAnchor.MiddleCenter, false);
            shell.Label(m.Band(centerX, m.Px(812f), centerW, m.Px(44f)),
                UiEntryCodex.VerseSign, UiTextRole.Faint, shell.Size(22f), TextAnchor.MiddleCenter, false);

            // 进度条：一条轨 + 一条填充。填充的宽度只由 SetProgress 改，切皮肤只换颜色。
            screen._barWidth = m.Px(900f);
            var barRect = m.Band((m.Width - screen._barWidth) * 0.5f, m.Px(920f), screen._barWidth, m.Px(8f));
            var track = UiBuild.Rect("LoadTrack", barRect, shell.Content);
            var trackImage = UiBuild.Box("LoadTrackFill", new Rect(0f, 0f, barRect.width, barRect.height), track);
            shell.Register(new UiDivider(trackImage));

            var fillImage = UiBuild.Box("LoadBar", new Rect(0f, 0f, 0f, barRect.height), track);
            shell.Register(new UiTint(fillImage, UiTextRole.Jade));
            screen._fill = fillImage.rectTransform;

            screen._tip = shell.Label(m.Band(centerX, m.Px(956f), centerW, m.Px(44f)),
                "正在接引", UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleCenter, false).Label;

            Debug.Log(ReadyLog + " verse=" + first + " bar=" + (int)screen._barWidth);
            return screen;
        }

        /// <summary>改进度。只动填充的宽度，不动位置与别的控件。</summary>
        public void SetProgress(float value)
        {
            if (_fill == null)
            {
                return;
            }

            value = Mathf.Clamp01(value);
            _fill.sizeDelta = new Vector2(_barWidth * value, _fill.sizeDelta.y);
        }

        /// <summary>改下面那行小字。拒绝与成功都用它说清楚。</summary>
        public void SetTip(string text)
        {
            if (_tip != null)
            {
                _tip.text = text;
            }
        }
    }
}
