using UnityEngine;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 界面尺寸口径。设计基准按平板 2560×1600 写，运行时长宽都换到**画布单位**：
    /// Canvas 用 ScaleWithScreenSize 按屏高等比缩放，画布高恒为 1600、宽随宽高比变化，
    /// 所以同一份布局在任何分辨率下比例一致，屏幕像素只差一个全局缩放系数。
    ///
    /// 坐标沿用战斗界面的做法：锚点左下、pivot 左下，矩形是画布单位；点选那边
    /// 用 <c>UiShell.ToScreen</c> 换成屏幕像素，和 <c>adb input tap</c> 对得上。
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
        /// 按当前画布算。画布高恒为设计基准高（CanvasScaler 按屏高等比缩放），
        /// 宽跟着宽高比走；Scale 恒为 1，设计值就是画布单位。
        ///
        /// 批处理模式或窗口小到排不下时退到 2560×1600，让编辑器自检也能验完整一屏。
        /// </summary>
        public static UiMetrics Of()
        {
            var screenWidth = Screen.width;
            var screenHeight = Screen.height;
            if (screenWidth < 900 || screenHeight < 600)
            {
                return new UiMetrics(DesignWidth, DesignHeight, 1f);
            }

            // 与 CanvasScaler 的 matchWidthOrHeight = 1（按高匹配）同一套算法：
            // 画布高恒为 DesignHeight，宽 = 设计高 × 屏幕宽高比。
            return new UiMetrics(DesignHeight * screenWidth / screenHeight, DesignHeight, 1f);
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
