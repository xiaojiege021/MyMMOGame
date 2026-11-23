using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 屏导航、切皮肤、Toast 与确认弹窗、点选分发（U1 的一半）。
    /// </summary>
    public sealed partial class UiShell : MonoBehaviour
    {
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
            _pageTitle = title;
            _pageBuild = build;
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

            // 新屏一建好就按当前色板上色。少了这一步，刚开的屏要等下一次切皮肤才有颜色。
            ApplySkin();

            Debug.Log(PageLog + "title=" + title + " widgets=" + _page.Count + " buttons=" + _pageButtons.Count);        }

        /// <summary>按同一份构建回调重开当前屏。回包里来了新事实值时用，不换标题也不记日志。</summary>
        public void RefreshPage()
        {
            if (_pageBuild != null)
            {
                ShowPage(_pageTitle, _pageBuild);
            }
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
        /// <summary>这个屏幕点是不是落在界面上。点选那边要跳过这块。</summary>
        public bool HitTest(Vector2 screenPoint)
        {
            // 点选传进来的是屏幕像素；布局是画布单位，先除全局缩放再比矩形。
            var point = ToCanvas(screenPoint);
            if (_modalLayer != null && _modalLayer.gameObject.activeSelf)
            {
                return _modalConfirm.HitRect.Contains(point) || _modalCancel.HitRect.Contains(point);
            }

            for (var i = 0; i < _pageButtons.Count; i++)
            {
                if (_pageButtons[i].HitRect.Contains(point))
                {
                    return true;
                }
            }

            for (var i = 0; i < _chromeButtons.Count; i++)
            {
                if (_chromeButtons[i].HitRect.Contains(point))
                {
                    return true;
                }
            }

            return false;
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
            var point = ToCanvas(pointer.position.ReadValue());

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
    }
}
