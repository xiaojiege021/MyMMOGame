using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 一行输入框（原型：高约 40、仅下边框、聚焦转青）。
    ///
    /// 不挂 <c>EventSystem</c>：判定沿用骨架那套屏幕像素矩形，点上去才开键盘。
    /// 真机上走 <see cref="TouchScreenKeyboard"/>（系统输入法，名号能打中文）；
    /// 编辑器里键盘由硬件接管，肉眼验收时能直接敲。
    ///
    /// 只管收字与画：口令对不对、名号能不能用，一个都不在这里判。
    /// </summary>
    public sealed class UiTextField : IUiThemed
    {
        /// <summary>点击判定矩形，屏幕像素。</summary>
        public readonly Rect HitRect;

        /// <summary>输入内容变了就回调一次。界面用它清提示。</summary>
        public Action OnChanged;

        readonly Text _label;

        readonly Text _value;

        readonly Text _placeholder;

        readonly Image _line;

        readonly bool _secure;

        readonly int _maxLength;

        readonly UiShell _shell;

        string _text = string.Empty;

        bool _focused;

        TouchScreenKeyboard _keyboard;

        public UiTextField(
            UiShell shell, Transform parent, Rect rect, string label, string placeholder,
            bool secure, int maxLength)
        {
            _shell = shell;
            _secure = secure;
            _maxLength = maxLength;
            HitRect = rect;

            var m = shell.Metrics;
            var labelH = m.Px(34f);
            var lineH = m.Px(2f);
            var valueH = rect.height - labelH - m.Px(10f);

            // 三行文字与下边框都直接按屏幕矩形排在父节点里，画与点同一套坐标。
            _label = UiBuild.Label("FieldLabel",
                new Rect(rect.x, rect.y + rect.height - labelH, rect.width, labelH),
                shell.Size(22f), TextAnchor.MiddleLeft, UiFont.Sans(), false, parent);
            _label.text = label;

            _value = UiBuild.Label("FieldValue", new Rect(rect.x, rect.y + m.Px(10f), rect.width, valueH),
                shell.Size(30f), TextAnchor.MiddleLeft, UiFont.Sans(), false, parent);
            _value.text = string.Empty;

            _placeholder = UiBuild.Label("FieldHint", new Rect(rect.x, rect.y + m.Px(10f), rect.width, valueH),
                shell.Size(28f), TextAnchor.MiddleLeft, UiFont.Sans(), false, parent);
            _placeholder.text = placeholder;

            _line = UiBuild.Box("FieldLine", new Rect(rect.x, rect.y, rect.width, lineH), parent);

            shell.Register(this);
        }

        /// <summary>当前文字。空串表示没填。</summary>
        public string Text => _text;

        /// <summary>正在输入。</summary>
        public bool Focused => _focused;

        /// <summary>直接置值。核对与「回填」用；超长按上限截断。</summary>
        public void SetText(string text)
        {
            text = text ?? string.Empty;
            if (text.Length > _maxLength)
            {
                text = text.Substring(0, _maxLength);
            }

            if (string.Equals(text, _text, StringComparison.Ordinal))
            {
                return;
            }

            _text = text;
            Refresh();
            if (OnChanged != null)
            {
                OnChanged();
            }
        }

        /// <summary>点进来了：开键盘，边框转青。</summary>
        public void BeginEdit()
        {
            _focused = true;
            if (TouchScreenKeyboard.isSupported)
            {
                _keyboard = TouchScreenKeyboard.Open(
                    _text, TouchScreenKeyboardType.Default, false, false, _secure, false, string.Empty);
            }
            else if (Keyboard.current != null)
            {
                Keyboard.current.onTextInput += OnTextInput;
            }

            Apply(_shell.Palette);
        }

        /// <summary>离开输入：关键盘，边框回常态。</summary>
        public void EndEdit()
        {
            if (!_focused)
            {
                return;
            }

            _focused = false;
            if (Keyboard.current != null)
            {
                Keyboard.current.onTextInput -= OnTextInput;
            }

            if (_keyboard != null)
            {
                if (_keyboard.active)
                {
                    _keyboard.active = false;
                }

                _keyboard = null;
            }

            Apply(_shell.Palette);
        }

        /// <summary>每帧同步一次。由入口流程驱动，骨架不替它转。</summary>
        public void Tick()
        {
            if (!_focused)
            {
                return;
            }

            if (_keyboard != null)
            {
                if (_keyboard.status == TouchScreenKeyboard.Status.Done
                    || _keyboard.status == TouchScreenKeyboard.Status.Canceled
                    || _keyboard.status == TouchScreenKeyboard.Status.LostFocus)
                {
                    SetText(_keyboard.text);
                    EndEdit();
                    return;
                }

                SetText(_keyboard.text);
                return;
            }

            // 编辑器里没有软键盘，退格与回车由硬件给。
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard.backspaceKey.wasPressedThisFrame && _text.Length > 0)
            {
                SetText(_text.Substring(0, _text.Length - 1));
            }

            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                EndEdit();
            }
        }

        public void Apply(UiPalette palette)
        {
            _label.color = palette.Muted;
            _value.color = palette.Text;
            _placeholder.color = palette.Faint;
            _line.color = _focused ? palette.Jade : palette.Line;
        }

        void OnTextInput(char c)
        {
            if (c == '\b' || c == '\n' || c == '\r')
            {
                return;
            }

            SetText(_text + c);
        }

        void Refresh()
        {
            _value.text = _secure ? new string('•', _text.Length) : _text;
            _placeholder.gameObject.SetActive(_text.Length == 0);
        }
    }
}
