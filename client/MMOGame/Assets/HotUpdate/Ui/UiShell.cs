using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 共用界面骨架：一层 Canvas、顶栏、内容区、Toast、确认弹窗，加一份色板。
    /// 所有 U 卡共用这一套，屏只往内容区里填，不各自新建 Canvas。
    ///
    /// 切皮肤只调 <see cref="SetSkin"/>：色板换一份、已注册控件重刷颜色，
    /// 不动 <c>RectTransform</c>、不动文案、不重建、不重载数据（17.5 第 1 条）。
    /// 偏好交给 <see cref="UiSkinStore"/>，只在本机，不进 config、不进协议。
    ///
    /// 判定用屏幕像素矩形，不挂 EventSystem，和战斗界面同一套口径。
    /// </summary>
    public sealed partial class UiShell : MonoBehaviour
    {
        /// <summary>骨架版本，进日志便于对齐真机抓到的行。</summary>
        public const int Version = 1;

        /// <summary>就绪行前缀。</summary>
        public const string ReadyLog = "u1_shell=ready";

        /// <summary>切皮肤行前缀。</summary>
        public const string SkinLog = "u1_skin=";

        /// <summary>开一屏行前缀。</summary>
        public const string PageLog = "u1_page=";

        Canvas _canvas;

        RectTransform _content;

        RectTransform _toastLayer;

        CanvasGroup _toastGroup;

        Text _toastText;

        RectTransform _modalLayer;

        Text _modalTitle;

        Text _modalBody;

        UiButton _modalConfirm;

        UiButton _modalCancel;

        Text _titleText;

        Action _onConfirm;

        UiMetrics _metrics;

        UiPalette _palette;

        RectTransform _canvasRect;

        // 上一屏的标题与构建回调：分辨率变化要按同一份布局重排，不丢当前屏。
        string _pageTitle;

        Action<UiShell> _pageBuild;

        float _builtWidth;

        readonly List<IUiThemed> _chrome = new List<IUiThemed>();

        readonly List<IUiThemed> _page = new List<IUiThemed>();

        readonly List<UiButton> _chromeButtons = new List<UiButton>();

        readonly List<UiButton> _pageButtons = new List<UiButton>();

        bool _buildingPage;

        bool _wasPressed;

        bool _initialized;

        float _toastAge = 99f;

        /// <summary>当前屏的尺寸口径。</summary>
        public UiMetrics Metrics
        {
            get { return _metrics; }
        }

        /// <summary>当前色板。</summary>
        public UiPalette Palette
        {
            get { return _palette; }
        }

        /// <summary>当前皮肤。</summary>
        public UiSkin Skin
        {
            get { return _palette.Skin; }
        }

        /// <summary>内容区，屏往这里放东西。</summary>
        public RectTransform Content
        {
            get { return _content; }
        }

        /// <summary>骨架搭好了没有。编辑器自检会看这个，别拿默认结构当成功。</summary>
        public bool IsReady
        {
            get { return _initialized; }
        }

        /// <summary>取骨架，没有就建一个。整个客户端只该有一份。</summary>
        public static UiShell Ensure()
        {
            var existing = FindObjectOfType<UiShell>();
            if (existing != null)
            {
                existing.Initialize();
                return existing;
            }

            var go = new GameObject("UiShell", typeof(Canvas), typeof(CanvasScaler), typeof(UiShell));
            var shell = go.GetComponent<UiShell>();
            shell.Initialize();
            return shell;
        }

        void Awake()
        {
            Initialize();
        }

        /// <summary>
        /// 搭骨架。播放模式由 <c>Awake</c> 调；编辑模式下 <c>Awake</c> 不触发，
        /// 所以 <see cref="Ensure"/> 显式调一次，编辑器自检才验得到真东西。幂等。
        /// </summary>
        public void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            _canvas = GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 20;

            // 按屏高等比缩放：画布高恒为 1600 设计单位，宽跟着宽高比走。
            // 布局只在 1600 高一档里算，任何分辨率都由这一层统一缩放；
            // 点选那边用 ToScreen 换回屏幕像素，和 adb input tap 对得上。
            var scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(UiMetrics.DesignWidth, UiMetrics.DesignHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            _canvasRect = (RectTransform)transform;

            _metrics = UiMetrics.Of();
            _palette = UiPalette.Of(UiSkinStore.Load());

            BuildChrome();
            ApplySkin();
            _builtWidth = _metrics.Width;

            Debug.Log(ReadyLog + " version=" + Version
                + " screen=" + (int)_metrics.Width + "x" + (int)_metrics.Height
                + " scale=" + _metrics.Scale.ToString("0.00")
                + " skin=" + SkinValue(_palette.Skin)
                + " buttons=" + (_chromeButtons.Count + _pageButtons.Count));
        }

        /// <summary>设计尺寸转像素，屏里排布局统一走它。</summary>
        public int Size(float design)
        {
            return Mathf.RoundToInt(_metrics.Px(design));
        }
        // ------------------------------------------------------------------ 命中

        /// <summary>屏幕像素 → 画布单位（左下原点）。屏幕空间叠加的画布按 scaleFactor 等比缩放。</summary>
        public Vector2 ToCanvas(Vector2 screenPoint)
        {
            var scale = CanvasScale();
            return screenPoint / scale;
        }

        /// <summary>全局缩放。编辑模式下画布可能还没渲染过，scaleFactor 还是 0，按 1 算。</summary>
        float CanvasScale()
        {
            var scale = _canvas != null ? _canvas.scaleFactor : 1f;
            return scale > 0f ? scale : 1f;
        }

        /// <summary>画布单位 → 屏幕像素。核对与 adb 点选都从这里换算。</summary>
        public Vector2 ToScreen(Vector2 canvasPoint)
        {
            return canvasPoint * CanvasScale();
        }
        void Update()
        {
            TickToast();
            ReadPress();
            CheckCanvasSize();
        }

        /// <summary>
        /// 画布宽度变了（改窗口、换分辨率、旋转）就按新宽度重排一次。
        /// 高度由 CanvasScaler 恒定为设计基准，宽度跟着宽高比走，所以要重算的是宽度相关的部分。
        /// </summary>
        void CheckCanvasSize()
        {
            if (_canvasRect == null)
            {
                return;
            }

            var width = UiMetrics.Of().Width;
            if (width < 1f || Mathf.Abs(width - _builtWidth) < 1f)
            {
                return;
            }

            Rebuild();
        }

        /// <summary>重建骨架与当前屏。分辨率变化时调用；输入框内容会清空，只在编辑器/旋转时发生。</summary>
        void Rebuild()
        {
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(transform.GetChild(i).gameObject);
            }

            _chrome.Clear();
            _page.Clear();
            _chromeButtons.Clear();
            _pageButtons.Clear();

            _metrics = UiMetrics.Of();
            BuildChrome();
            ApplySkin();

            var title = _pageTitle;
            var build = _pageBuild;
            _pageBuild = null;
            if (build != null)
            {
                ShowPage(title, build);
            }

            _builtWidth = UiMetrics.Of().Width;
            Debug.Log("u1_layout=reflow width=" + (int)_builtWidth
                + " height=" + (int)_metrics.Height + " page=" + (title ?? ""));
        }

        void TickToast()
        {
            if (_toastAge > 3f)
            {
                return;
            }

            _toastAge += Time.unscaledDeltaTime;
            _toastGroup.alpha = Mathf.Clamp01(1f - (_toastAge - 1.6f) / 0.6f);
        }
    }
}
