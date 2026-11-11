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
    public sealed class UiShell : MonoBehaviour
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

            // 像素一一对应：画出来的矩形和点击矩形才是同一套坐标。
            var scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            _metrics = UiMetrics.Of();
            _palette = UiPalette.Of(UiSkinStore.Load());

            BuildChrome();
            ApplySkin();

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

        // ------------------------------------------------------------------ 皮肤

        /// <summary>切皮肤。写不写偏好由调用方定，自检用 false 免得污染本机。</summary>
        public void SetSkin(UiSkin skin, bool persist = true)
        {
            _palette = UiPalette.Of(skin);
            ApplySkin();
            if (persist)
            {
                UiSkinStore.Save(skin);
            }

            Debug.Log(SkinLog + SkinValue(skin) + " layout=unchanged persist=" + (persist ? "true" : "false"));
        }

        /// <summary>明亮 ↔ 暗黑。</summary>
        public void ToggleSkin()
        {
            SetSkin(_palette.Skin == UiSkin.Light ? UiSkin.Dark : UiSkin.Light);
        }

        void ApplySkin()
        {
            for (var i = 0; i < _chrome.Count; i++)
            {
                _chrome[i].Apply(_palette);
            }

            for (var i = 0; i < _page.Count; i++)
            {
                _page[i].Apply(_palette);
            }
        }

        static string SkinValue(UiSkin skin)
        {
            return skin == UiSkin.Dark ? UiSkinStore.DarkValue : UiSkinStore.LightValue;
        }

        // ------------------------------------------------------------------ 屏

        /// <summary>开一屏：清掉上一屏，换标题，让 <paramref name="build"/> 往内容区里填。</summary>
        public void ShowPage(string title, Action<UiShell> build)
        {
            ClearPage();
            _titleText.text = title;
            _buildingPage = true;
            try
            {
                build(this);
            }
            finally
            {
                _buildingPage = false;
            }

            Debug.Log(PageLog + "title=" + title + " widgets=" + _page.Count + " buttons=" + _pageButtons.Count);
        }

        /// <summary>关掉当前屏，只留骨架。</summary>
        public void ClosePage()
        {
            ClearPage();
            _titleText.text = "";
        }

        /// <summary>通用状态屏（原型 G01）。</summary>
        public void OpenStatus()
        {
            ShowPage("状态与提示", UiStatusScreen.Build);
        }

        void ClearPage()
        {
            for (var i = _content.childCount - 1; i >= 0; i--)
            {
                var child = _content.GetChild(i).gameObject;
                if (Application.isPlaying)
                {
                    Destroy(child);
                }
                else
                {
                    DestroyImmediate(child);
                }
            }

            _page.Clear();
            _pageButtons.Clear();
        }

        // ------------------------------------------------------------------ 控件工厂

        /// <summary>卡片。标题为空就只画底与描边。</summary>
        public UiPanel Panel(string title, Rect rect, int titleSize)
        {
            var root = UiBuild.Rect("Panel", rect, _content);
            var back = UiBuild.Box("PanelBack", new Rect(0f, 0f, rect.width, rect.height), root);
            var titleText = string.IsNullOrEmpty(title)
                ? null
                : UiBuild.Label("PanelTitle",
                    new Rect(_metrics.Px(28), rect.height - _metrics.Px(20) - _metrics.Px(48),
                        rect.width - _metrics.Px(56), _metrics.Px(48)),
                    titleSize, TextAnchor.MiddleLeft, UiFont.Serif(), false, root);

            if (titleText != null)
            {
                titleText.text = title;
            }

            var panel = new UiPanel(root, back, titleText);
            var edge = _metrics.Px(2);
            panel.AddEdge(UiBuild.Box("EdgeT", new Rect(0f, rect.height - edge, rect.width, edge), root));
            panel.AddEdge(UiBuild.Box("EdgeB", new Rect(0f, 0f, rect.width, edge), root));
            panel.AddEdge(UiBuild.Box("EdgeL", new Rect(0f, 0f, edge, rect.height), root));
            panel.AddEdge(UiBuild.Box("EdgeR", new Rect(rect.width - edge, 0f, edge, rect.height), root));
            Track(panel);
            return panel;
        }

        /// <summary>内容区里的一段文字。</summary>
        public UiText Label(Rect rect, string text, UiTextRole role, int size, TextAnchor anchor, bool wrap)
        {
            return LabelIn(_content, rect, text, role, size, anchor, wrap);
        }

        /// <summary>某个父节点里的一段文字，坐标是父节点的局部坐标（左下原点）。</summary>
        public UiText LabelIn(
            Transform parent, Rect rect, string text, UiTextRole role, int size, TextAnchor anchor, bool wrap)
        {
            var label = UiBuild.Label("Label", rect, size, anchor, UiFont.Sans(), wrap, parent);
            label.text = text;
            var themed = new UiText(label, role);
            Track(themed);
            return themed;
        }

        /// <summary>
        /// 内容区里的按钮。判定矩形与画出来的矩形是同一个，都是屏幕像素，
        /// 所以能直接和 <c>Pointer</c> 的坐标比。
        /// </summary>
        public UiButton Button(Rect rect, string label, Action onClick, UiButtonKind kind)
        {
            return ButtonIn(_content, rect, label, onClick, kind);
        }

        /// <summary>
        /// 建在指定父节点下的按钮。骨架自己的按钮挂在根上，不能被清屏带走；
        /// 屏里的按钮挂在内容区，随屏一起清。
        /// </summary>
        public UiButton ButtonIn(
            Transform parent, Rect rect, string label, Action onClick, UiButtonKind kind)
        {
            var root = UiBuild.Rect("Button", rect, parent);
            var fill = UiBuild.Box("ButtonFill", new Rect(0f, 0f, rect.width, rect.height), root);
            var text = UiBuild.Label("ButtonLabel", new Rect(0f, 0f, rect.width, rect.height),
                Size(30f), TextAnchor.MiddleCenter, UiFont.Sans(), false, root);
            text.text = label;

            var button = new UiButton(rect, fill, text, kind);
            button.OnClick = onClick;
            Track(button);
            if (_buildingPage)
            {
                _pageButtons.Add(button);
            }
            else
            {
                _chromeButtons.Add(button);
            }

            return button;
        }

        /// <summary>状态标签。</summary>
        public UiChip Chip(Transform parent, Rect rect, string text, UiChipKind kind)
        {
            var root = UiBuild.Rect("Chip", rect, parent);
            var fill = UiBuild.Box("ChipFill", new Rect(0f, 0f, rect.width, rect.height), root);
            var label = UiBuild.Label("ChipLabel", new Rect(0f, 0f, rect.width, rect.height),
                Size(24f), TextAnchor.MiddleCenter, UiFont.Sans(), false, root);
            label.text = text;

            var chip = new UiChip(fill, label, kind);
            Track(chip);
            return chip;
        }

        /// <summary>一条分隔线。</summary>
        public void Divider(Transform parent, Rect rect)
        {
            var line = UiBuild.Box("Divider", rect, parent);
            Track(new UiDivider(line));
        }

        void Track(IUiThemed themed)
        {
            if (_buildingPage)
            {
                _page.Add(themed);
            }
            else
            {
                _chrome.Add(themed);
            }
        }

        // ------------------------------------------------------------------ Toast 与弹窗

        /// <summary>底部一闪而过的提示。不打断当前操作。</summary>
        public void Toast(string message)
        {
            _toastText.text = message;
            _toastAge = 0f;
            _toastGroup.alpha = 1f;
        }

        /// <summary>确认弹窗。文案只陈述结果，不倒计时。</summary>
        public void ShowConfirm(string title, string body, string confirmLabel, Action onConfirm)
        {
            _onConfirm = onConfirm;
            _modalTitle.text = title;
            _modalBody.text = body;
            _modalConfirm.Label.text = confirmLabel;
            _modalLayer.gameObject.SetActive(true);
            Debug.Log("u1_modal=open title=" + title);
        }

        void HideModal()
        {
            _modalLayer.gameObject.SetActive(false);
            _onConfirm = null;
        }

        void ConfirmModal()
        {
            var action = _onConfirm;
            HideModal();
            if (action != null)
            {
                action();
            }
        }

        // ------------------------------------------------------------------ 命中

        /// <summary>这个屏幕点是不是落在界面上。点选那边要跳过这块。</summary>
        public bool HitTest(Vector2 screenPoint)
        {
            if (_modalLayer != null && _modalLayer.gameObject.activeSelf)
            {
                return _modalConfirm.HitRect.Contains(screenPoint) || _modalCancel.HitRect.Contains(screenPoint);
            }

            for (var i = 0; i < _pageButtons.Count; i++)
            {
                if (_pageButtons[i].HitRect.Contains(screenPoint))
                {
                    return true;
                }
            }

            for (var i = 0; i < _chromeButtons.Count; i++)
            {
                if (_chromeButtons[i].HitRect.Contains(screenPoint))
                {
                    return true;
                }
            }

            return false;
        }

        void Update()
        {
            TickToast();
            ReadPress();
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

        void ReadPress()
        {
            var pointer = Pointer.current;
            if (pointer == null)
            {
                _wasPressed = false;
                return;
            }

            if (!pointer.press.isPressed)
            {
                _wasPressed = false;
                return;
            }

            if (_wasPressed)
            {
                return;
            }

            _wasPressed = true;
            var point = pointer.position.ReadValue();

            // 弹窗是最上层，开着就只认它。
            if (_modalLayer.gameObject.activeSelf)
            {
                if (_modalConfirm.HitRect.Contains(point))
                {
                    ConfirmModal();
                }
                else if (_modalCancel.HitRect.Contains(point))
                {
                    HideModal();
                }

                return;
            }

            if (Dispatch(_pageButtons, point))
            {
                return;
            }

            Dispatch(_chromeButtons, point);
        }

        static bool Dispatch(List<UiButton> buttons, Vector2 point)
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var button = buttons[i];
                if (!button.Enabled || !button.HitRect.Contains(point))
                {
                    continue;
                }

                if (button.OnClick != null)
                {
                    button.OnClick();
                }

                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------ 搭骨架

        void BuildChrome()
        {
            var width = _metrics.Width;
            var height = _metrics.Height;

            _content = UiBuild.Rect("Content", new Rect(0f, 0f, width, height), transform);

            var bar = UiBuild.Box("TopBar", _metrics.Band(0f, 0f, width, _metrics.Px(120f)), transform);
            Track(new UiPanel(bar.rectTransform, bar, null));

            var rule = UiBuild.Box("TopRule",
                new Rect(0f, height - _metrics.Px(120f) - _metrics.Px(2f), width, _metrics.Px(2f)), transform);
            Track(new UiDivider(rule));

            _titleText = UiBuild.Label("TopTitle",
                _metrics.Band(_metrics.Px(56f), _metrics.Px(34f), width * 0.5f, _metrics.Px(52f)),
                Size(40f), TextAnchor.MiddleLeft, UiFont.Serif(), false, transform);
            _titleText.text = "";
            Track(new UiText(_titleText, UiTextRole.Title));

            var skinRect = _metrics.Band(width - _metrics.Px(56f) - _metrics.Px(260f), _metrics.Px(28f),
                _metrics.Px(260f), _metrics.Px(64f));
            ButtonIn(transform, skinRect, "切换风格", ToggleSkin, UiButtonKind.Normal);

            BuildToast();
            BuildModal();
        }

        void BuildToast()
        {
            var rect = _metrics.Band(_metrics.Width * 0.3f, _metrics.Height - _metrics.Px(170f),
                _metrics.Width * 0.4f, _metrics.Px(88f));
            _toastLayer = UiBuild.Rect("ToastLayer", rect, transform);
            _toastGroup = _toastLayer.gameObject.AddComponent<CanvasGroup>();
            _toastGroup.alpha = 0f;

            var back = UiBuild.Box("ToastBack", new Rect(0f, 0f, rect.width, rect.height), _toastLayer);
            Track(new UiPanel(back.rectTransform, back, null));

            _toastText = UiBuild.Label("ToastText", new Rect(_metrics.Px(24f), 0f,
                    rect.width - _metrics.Px(48f), rect.height),
                Size(28f), TextAnchor.MiddleCenter, UiFont.Sans(), true, _toastLayer);
            _toastText.text = "";
            Track(new UiText(_toastText, UiTextRole.Body));
        }

        void BuildModal()
        {
            var width = _metrics.Width;
            var height = _metrics.Height;
            _modalLayer = UiBuild.Rect("ModalLayer", new Rect(0f, 0f, width, height), transform);

            var mask = UiBuild.Box("ModalMask", new Rect(0f, 0f, width, height), _modalLayer);
            mask.color = new Color(0f, 0f, 0f, 0.55f);

            var cardW = _metrics.Px(900f);
            var cardH = _metrics.Px(380f);
            var cardPos = new Vector2((width - cardW) / 2f, (height - cardH) / 2f);
            var card = UiBuild.Rect("ModalCard",
                new Rect(cardPos.x, cardPos.y, cardW, cardH), _modalLayer);
            var back = UiBuild.Box("ModalBack", new Rect(0f, 0f, cardW, cardH), card);

            var edge = _metrics.Px(2f);
            var frame = new UiPanel(card, back, null);
            frame.AddEdge(UiBuild.Box("EdgeT", new Rect(0f, cardH - edge, cardW, edge), card));
            frame.AddEdge(UiBuild.Box("EdgeB", new Rect(0f, 0f, cardW, edge), card));
            frame.AddEdge(UiBuild.Box("EdgeL", new Rect(0f, 0f, edge, cardH), card));
            frame.AddEdge(UiBuild.Box("EdgeR", new Rect(cardW - edge, 0f, edge, cardH), card));
            Track(frame);

            _modalTitle = UiBuild.Label("ModalTitle",
                new Rect(_metrics.Px(48f), cardH - _metrics.Px(110f), cardW - _metrics.Px(96f), _metrics.Px(52f)),
                Size(36f), TextAnchor.MiddleLeft, UiFont.Serif(), false, card);
            _modalTitle.text = "";
            Track(new UiText(_modalTitle, UiTextRole.Title));

            _modalBody = UiBuild.Label("ModalBody",
                new Rect(_metrics.Px(48f), _metrics.Px(150f), cardW - _metrics.Px(96f), _metrics.Px(110f)),
                Size(28f), TextAnchor.UpperLeft, UiFont.Sans(), true, card);
            _modalBody.text = "";
            Track(new UiText(_modalBody, UiTextRole.Muted));

            var buttonW = _metrics.Px(220f);
            var buttonH = _metrics.Px(76f);
            var left = cardW - _metrics.Px(48f) - buttonW * 2f - _metrics.Px(20f);

            _modalCancel = MakeModalButton(card, cardPos,
                new Rect(left, _metrics.Px(40f), buttonW, buttonH), "取消", UiButtonKind.Normal, HideModal);
            _modalConfirm = MakeModalButton(card, cardPos,
                new Rect(cardW - _metrics.Px(48f) - buttonW, _metrics.Px(40f), buttonW, buttonH),
                "确定", UiButtonKind.Primary, ConfirmModal);

            _modalLayer.gameObject.SetActive(false);
        }

        /// <summary>
        /// 弹窗里的按钮。卡片是局部坐标，命中矩形要加上卡片在屏幕上的位置，
        /// 否则点上去对不上（判定与绘制必须同一套坐标）。
        /// </summary>
        UiButton MakeModalButton(
            Transform card, Vector2 cardPos, Rect local, string label, UiButtonKind kind, Action onClick)
        {
            var root = UiBuild.Rect("ModalButton", local, card);
            var fill = UiBuild.Box("ModalButtonFill", new Rect(0f, 0f, local.width, local.height), root);
            var text = UiBuild.Label("ModalButtonLabel", new Rect(0f, 0f, local.width, local.height),
                Size(28f), TextAnchor.MiddleCenter, UiFont.Sans(), false, root);
            text.text = label;

            var hit = new Rect(cardPos.x + local.x, cardPos.y + local.y, local.width, local.height);
            var button = new UiButton(hit, fill, text, kind);
            button.OnClick = onClick;
            _chrome.Add(button);
            return button;
        }
    }
}
