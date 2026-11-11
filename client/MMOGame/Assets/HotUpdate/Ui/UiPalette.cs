using UnityEngine;

namespace HotUpdate.Ui
{
    /// <summary>两套皮肤。默认明亮，玩家可在设置里切，偏好只存本地。</summary>
    public enum UiSkin
    {
        Light = 0,
        Dark = 1,
    }

    /// <summary>
    /// 皮肤色板。这是界面颜色的唯一正本：控件不写死色值，切皮肤只换这份表。
    ///
    /// 色值取自 <c>docs/UI原型全集.html</c> 两套 CSS 变量（17.5 写定：同一套界面两套皮肤，
    /// 只有色板与背景插画成对替换）。字段对应关系：
    /// page = --ink-0，card = --glass，line = --line，text = --text，
    /// jade = --jade，warm = --warm，danger = --danger，
    /// btnFill / btnText = 原型 <c>#skinBtn</c> 的底色与字色。
    /// </summary>
    public readonly struct UiPalette
    {
        /// <summary>哪一套。</summary>
        public readonly UiSkin Skin;

        /// <summary>皮肤名，进日志与设置页显示。</summary>
        public readonly string Name;

        /// <summary>整页底色。</summary>
        public readonly Color Page;

        /// <summary>卡片底色。</summary>
        public readonly Color Card;

        /// <summary>描边。</summary>
        public readonly Color Line;

        /// <summary>更淡的分隔线。</summary>
        public readonly Color LineSoft;

        /// <summary>正文。</summary>
        public readonly Color Text;

        /// <summary>次要文字。</summary>
        public readonly Color Muted;

        /// <summary>更淡的说明文字。</summary>
        public readonly Color Faint;

        /// <summary>青绿主色：标题、强调、成功。</summary>
        public readonly Color Jade;

        /// <summary>深一档的青绿，用在明亮皮肤的大标题上。</summary>
        public readonly Color JadeBright;

        /// <summary>暖色，用在提示与标价。</summary>
        public readonly Color Warm;

        /// <summary>错误与拒绝。</summary>
        public readonly Color Danger;

        /// <summary>普通按钮底色。</summary>
        public readonly Color BtnFill;

        /// <summary>普通按钮字色。</summary>
        public readonly Color BtnText;

        public UiPalette(
            UiSkin skin,
            string name,
            Color page,
            Color card,
            Color line,
            Color lineSoft,
            Color text,
            Color muted,
            Color faint,
            Color jade,
            Color jadeBright,
            Color warm,
            Color danger,
            Color btnFill,
            Color btnText)
        {
            Skin = skin;
            Name = name;
            Page = page;
            Card = card;
            Line = line;
            LineSoft = lineSoft;
            Text = text;
            Muted = muted;
            Faint = faint;
            Jade = jade;
            JadeBright = jadeBright;
            Warm = warm;
            Danger = danger;
            BtnFill = btnFill;
            BtnText = btnText;
        }

        /// <summary>明亮「晨雾宣纸」。</summary>
        public static readonly UiPalette Light = new UiPalette(
            UiSkin.Light,
            "晨雾宣纸",
            page: Hex("f4f2e9"),
            card: Alpha(Hex("fffdf6"), 0.90f),
            line: Alpha(Hex("2f6b60"), 0.24f),
            lineSoft: Alpha(Hex("2f6b60"), 0.13f),
            text: Hex("26363a"),
            muted: Hex("4b6062"),
            faint: Hex("718582"),
            jade: Hex("3f8577"),
            jadeBright: Hex("234f49"),
            warm: Hex("9a6b25"),
            danger: Hex("b25145"),
            btnFill: Hex("e7f1e9"),
            btnText: Hex("26433c"));

        /// <summary>暗黑「夜山」。</summary>
        public static readonly UiPalette Dark = new UiPalette(
            UiSkin.Dark,
            "夜山",
            page: Hex("08121c"),
            card: Alpha(Hex("08131d"), 0.80f),
            line: Alpha(Hex("9fd8cf"), 0.22f),
            lineSoft: Alpha(Hex("9fd8cf"), 0.11f),
            text: Hex("e6eef0"),
            muted: Hex("8fa3ad"),
            faint: Hex("5d7280"),
            jade: Hex("9fd8cf"),
            jadeBright: Hex("cfe9e4"),
            warm: Hex("e8c98a"),
            danger: Hex("d98a76"),
            btnFill: Hex("1d3634"),
            btnText: Hex("dcefe9"));

        /// <summary>按枚举取色板。</summary>
        public static UiPalette Of(UiSkin skin)
        {
            return skin == UiSkin.Dark ? Dark : Light;
        }

        /// <summary>加一档透明度，用于禁用态与标签底。</summary>
        public static Color Alpha(Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, alpha);
        }

        /// <summary>六位十六进制转色。色值只在这里出现，别处不写颜色字面量。</summary>
        static Color Hex(string hex)
        {
            var r = System.Convert.ToInt32(hex.Substring(0, 2), 16) / 255f;
            var g = System.Convert.ToInt32(hex.Substring(2, 2), 16) / 255f;
            var b = System.Convert.ToInt32(hex.Substring(4, 2), 16) / 255f;
            return new Color(r, g, b, 1f);
        }
    }
}
