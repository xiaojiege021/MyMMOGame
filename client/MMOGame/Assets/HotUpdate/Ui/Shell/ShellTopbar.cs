using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 控件工厂（Panel / Label / Button / Chip / Divider）与搭骨架（顶栏、Toast 层、弹窗）。
    /// </summary>
    public sealed partial class UiShell : MonoBehaviour
    {
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

        /// <summary>
        /// 把屏自己搭的控件登记进当前层：切皮肤时统一重刷颜色，清屏时一起失效。
        /// 控件只许在 <see cref="Apply"/> 里换颜色，不许动布局与文案。
        /// </summary>
        public void Register(IUiThemed themed)
        {
            Track(themed);
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
