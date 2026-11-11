using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 登入屏（原型 A01「登入」，`docs/登录界面原型.html` 01 屏）。
    ///
    /// 左边是品牌与幕一题记，右边是入世卡片：账号、口令、一行提示、登入。
    /// 空账号或空口令按钮就置灰、不发请求；失败留在本屏，输入与口令都不清。
    /// 这一屏一个判定都没有：口令对不对、账号在不在，全等回包。
    /// </summary>
    public static class UiLoginScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u2_login=ready";

        /// <summary>建屏。骨架负责清屏，这里只管排，并把控件交给流程。</summary>
        public static void Build(UiShell shell, UiEntryFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(120f);

            BuildBrand(shell, m, pad);
            BuildVerse(shell, m, pad);
            BuildCard(shell, m, pad, flow);

            Debug.Log(ReadyLog + " screen=" + (int)m.Width + "x" + (int)m.Height
                + " verse=" + UiEntryCodex.VerseFixedA);
        }

        /// <summary>竖排主标题、副题、印章、幕一标识。</summary>
        static void BuildBrand(UiShell shell, in UiMetrics m, float pad)
        {
            var title = shell.Label(
                m.Band(pad, m.Px(230f), m.Px(180f), m.Px(380f)),
                UiEntryCodex.Brand, UiTextRole.Title, shell.Size(84f), TextAnchor.UpperCenter, false);
            // 竖排：三个字各占一行，行距压紧，和原型的竖排标题一致。
            title.Label.lineSpacing = 1.12f;

            shell.Label(m.Band(pad + m.Px(196f), m.Px(268f), m.Px(760f), m.Px(48f)),
                UiEntryCodex.BrandSub, UiTextRole.Heading, shell.Size(30f), TextAnchor.MiddleLeft, false);

            shell.Label(m.Band(pad + m.Px(196f), m.Px(336f), m.Px(160f), m.Px(56f)),
                UiEntryCodex.Seal, UiTextRole.Danger, shell.Size(26f), TextAnchor.MiddleLeft, false);

            shell.Label(m.Band(pad + m.Px(196f), m.Px(408f), m.Px(760f), m.Px(48f)),
                "幕一 · " + UiEntryCodex.ActOne, UiTextRole.Faint, shell.Size(26f), TextAnchor.MiddleLeft, false);
        }

        /// <summary>幕一题记。登入屏固定显示 01 定稿，不随机。</summary>
        static void BuildVerse(UiShell shell, in UiMetrics m, float pad)
        {
            shell.Label(new Rect(pad, m.Px(176f), m.Px(1000f), m.Px(52f)),
                UiEntryCodex.VerseFixedA, UiTextRole.Warm, shell.Size(34f), TextAnchor.MiddleLeft, false);
            shell.Label(new Rect(pad, m.Px(118f), m.Px(1000f), m.Px(52f)),
                UiEntryCodex.VerseFixedB, UiTextRole.Warm, shell.Size(34f), TextAnchor.MiddleLeft, false);
            shell.Label(new Rect(pad, m.Px(62f), m.Px(420f), m.Px(40f)),
                UiEntryCodex.VerseSign, UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);
        }

        /// <summary>入世卡片。控件按屏幕矩形排，画与点同一套坐标。</summary>
        static void BuildCard(UiShell shell, in UiMetrics m, float pad, UiEntryFlow flow)
        {
            var cardW = m.Px(700f);
            var cardH = m.Px(780f);
            var cardX = m.Width - pad - cardW;
            var cardY = (m.Height - cardH) * 0.5f;
            var card = shell.Panel("入世", new Rect(cardX, cardY, cardW, cardH), shell.Size(40f));

            var inner = m.Px(32f);
            var innerW = cardW - inner * 2f;

            // 卡片从下往上排：注脚、按钮、提示、口令、账号、分隔线、副题，标题由卡片自己画。
            var yNote = m.Px(52f);
            var yButton = m.Px(132f);
            var yHint = m.Px(236f);
            var yPassword = m.Px(300f);
            var yAccount = m.Px(432f);
            var yDivider = m.Px(566f);
            var ySub = m.Px(580f);
            var fieldH = m.Px(112f);

            shell.LabelIn(card.Root, new Rect(inner, ySub, innerW, m.Px(36f)),
                "幕一 · " + UiEntryCodex.ActOne, UiTextRole.Faint, shell.Size(22f), TextAnchor.MiddleLeft, false);

            shell.Divider(card.Root, new Rect(inner, yDivider, innerW, m.Px(2f)));

            var account = new UiTextField(shell, shell.Content,
                new Rect(cardX + inner, cardY + yAccount, innerW, fieldH), "账 号", "请输入账号", false, 32);

            var password = new UiTextField(shell, shell.Content,
                new Rect(cardX + inner, cardY + yPassword, innerW, fieldH), "口 令", "请输入口令", true, 64);

            var hint = shell.Label(new Rect(cardX + inner, cardY + yHint, innerW, m.Px(48f)),
                string.Empty, UiTextRole.Danger, shell.Size(24f), TextAnchor.MiddleLeft, false).Label;

            var button = shell.Button(new Rect(cardX + inner, cardY + yButton, innerW, m.Px(88f)),
                "登 入", flow.SubmitLogin, UiButtonKind.Primary);
            button.Enabled = false;
            button.Apply(shell.Palette);

            shell.Label(new Rect(cardX + inner, cardY + yNote, innerW, m.Px(64f)),
                "账号或口令不对时不区分原因；连不上网关不重试刷屏。",
                UiTextRole.Faint, shell.Size(20f), TextAnchor.UpperLeft, true);

            flow.BindLogin(account, password, hint, button);
        }
    }
}
