using UnityEngine;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 界面尺寸口径。设计基准按平板 2560×1600 写，运行时按屏高等比缩放，
    /// 所以同一份布局在编辑器小窗和真机上比例一致。
    ///
    /// 坐标沿用战斗界面的做法：锚点左下、pivot 左下，矩形就是屏幕像素，
    /// 和 <c>adb input tap</c> 的口径对得上（adb 的 y 从上往下，换算在核对脚本里）。
    /// </summary>
    public readonly struct UiMetrics
    {
        /// <summary>设计基准高度。</summary>
        public const float DesignHeight = 1600f;

        /// <summary>设计基准宽度，只用于算默认比例的兜底值。</summary>
        public const float DesignWidth = 2560f;

        /// <summary>实际屏宽。</summary>
        public readonly float Width;

        /// <summary>实际屏高。</summary>
        public readonly float Height;

        /// <summary>设计尺寸到实际像素的倍数。</summary>
        public readonly float Scale;

        UiMetrics(float width, float height, float scale)
        {
            Width = width;
            Height = height;
            Scale = scale;
        }

        /// <summary>
        /// 按当前屏算。批处理模式与小窗口的屏尺寸不足以排这套布局，退到设计基准，
        /// 让编辑器自检也能验完整一屏，而不是缩成一条。
        /// </summary>
        public static UiMetrics Of()
        {
            var width = Screen.width >= 900 ? (float)Screen.width : DesignWidth;
            var height = Screen.height >= 600 ? (float)Screen.height : DesignHeight;
            var scale = Mathf.Clamp(height / DesignHeight, 0.5f, 2f);
            return new UiMetrics(width, height, scale);
        }

        /// <summary>设计尺寸转实际像素。</summary>
        public float Px(float design)
        {
            return design * Scale;
        }

        /// <summary>
        /// 从顶部往下量一条带子，返回锚点在左下的矩形。
        /// 界面是按「距顶多少」排的，UGUI 是距底，这里换算一次，别处不再重复。
        /// </summary>
        public Rect Band(float left, float top, float width, float height)
        {
            return new Rect(left, Height - top - height, width, height);
        }
    }
}
