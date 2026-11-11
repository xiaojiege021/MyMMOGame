using UnityEngine;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 界面字体。安卓上只有系统字体带中文字形，内置的 LegacyRuntime 只有拉丁字母，
    /// 找不到就退回去，界面上的字不会变方块。
    ///
    /// 沙盒里取不到系统字体时返回 null，<c>Text</c> 会用内置默认字体，不抛异常。
    /// </summary>
    public static class UiFont
    {
        static Font _sans;

        static Font _serif;

        static readonly string[] SansNames =
        {
            "PingFang SC", "Microsoft YaHei", "Noto Sans CJK SC", "Droid Sans Fallback", "sans-serif",
        };

        // 标题用宋体类，取不到就回落到无衬线，字形不换、层级靠字号与颜色区分。
        static readonly string[] SerifNames =
        {
            "Songti SC", "Noto Serif CJK SC", "Source Han Serif SC", "STSong", "SimSun", "serif",
        };

        /// <summary>正文与控件用。</summary>
        public static Font Sans()
        {
            if (_sans == null)
            {
                _sans = Pick(SansNames);
            }

            return _sans;
        }

        /// <summary>标题用。取不到就跟正文同一份。</summary>
        public static Font Serif()
        {
            if (_serif == null)
            {
                _serif = Pick(SerifNames) ?? Sans();
            }

            return _serif;
        }

        static Font Pick(string[] names)
        {
            var font = Font.CreateDynamicFontFromOSFont(names, 32);
            if (font != null)
            {
                return font;
            }

#if UNITY_EDITOR
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
            return null;
#endif
        }
    }
}
