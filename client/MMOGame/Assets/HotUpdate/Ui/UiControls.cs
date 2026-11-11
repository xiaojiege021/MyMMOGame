using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>吃色板的东西。切皮肤时统一重刷，不碰布局与文案。</summary>
    public interface IUiThemed
    {
        void Apply(UiPalette palette);
    }

    /// <summary>文字角色。颜色只按角色从色板取，控件里不写色值。</summary>
    public enum UiTextRole
    {
        Title,
        Heading,
        Body,
        Muted,
        Faint,
        Warm,
        Danger,
        Jade,
    }

    /// <summary>按钮三档。主按钮用于「确认」这类唯一主动作。</summary>
    public enum UiButtonKind
    {
        Primary,
        Normal,
        Quiet,
    }

    /// <summary>标签三档，对应原型里的 成功 / 错误 / 禁用。</summary>
    public enum UiChipKind
    {
        Success,
        Error,
        Mute,
    }

    /// <summary>
    /// 控件族。同一套控件吃 <see cref="UiPalette"/>，切皮肤只改颜色：
    /// <see cref="Apply"/> 里不许动 <c>anchoredPosition</c>、<c>sizeDelta</c> 与文案。
    /// 判定沿用战斗界面的做法——屏幕像素矩形，不挂 EventSystem，
    /// 真机上少一层输入模块，核对也好按坐标点。
    /// </summary>
    public static class UiBuild
    {
        /// <summary>锚点左下、pivot 左下的空矩形。</summary>
        public static RectTransform Rect(string name, Rect rect, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(rect.x, rect.y);
            rt.sizeDelta = new Vector2(rect.width, rect.height);
            return rt;
        }

        /// <summary>一块纯色矩形。</summary>
        public static Image Box(string name, Rect rect, Transform parent)
        {
            var rt = Rect(name, rect, parent);
            var image = rt.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        /// <summary>一段文字。<paramref name="wrap"/> 为真时按宽度换行。</summary>
        public static Text Label(
            string name, Rect rect, int size, TextAnchor anchor, Font font, bool wrap, Transform parent)
        {
            var rt = Rect(name, rect, parent);
            var text = rt.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.raycastTarget = false;
            text.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = Color.white;
            return text;
        }
    }

    /// <summary>一段吃角色的文字。</summary>
    public sealed class UiText : IUiThemed
    {
        public readonly Text Label;

        readonly UiTextRole _role;

        public UiText(Text label, UiTextRole role)
        {
            Label = label;
            _role = role;
        }

        public void Apply(UiPalette palette)
        {
            Label.color = ColorOf(_role, palette);
        }

        public static Color ColorOf(UiTextRole role, in UiPalette palette)
        {
            switch (role)
            {
                case UiTextRole.Title:
                    return palette.JadeBright;
                case UiTextRole.Heading:
                    return palette.Jade;
                case UiTextRole.Muted:
                    return palette.Muted;
                case UiTextRole.Faint:
                    return palette.Faint;
                case UiTextRole.Warm:
                    return palette.Warm;
                case UiTextRole.Danger:
                    return palette.Danger;
                case UiTextRole.Jade:
                    return palette.Jade;
                default:
                    return palette.Text;
            }
        }
    }

    /// <summary>带标题与描边的卡片。四个边分四条细条画，切皮肤只换颜色。</summary>
    public sealed class UiPanel : IUiThemed
    {
        public readonly RectTransform Root;

        readonly Image _back;

        readonly Text _title;

        readonly List<Image> _edges = new List<Image>();

        public UiPanel(RectTransform root, Image back, Text title)
        {
            Root = root;
            _back = back;
            _title = title;
        }

        /// <summary>加一条边。接缝在卡片内侧，布局由调用方给。</summary>
        public void AddEdge(Image edge)
        {
            _edges.Add(edge);
        }

        public void Apply(UiPalette palette)
        {
            _back.color = palette.Card;
            if (_title != null)
            {
                _title.color = palette.JadeBright;
            }

            for (var i = 0; i < _edges.Count; i++)
            {
                _edges[i].color = palette.Line;
            }
        }
    }

    /// <summary>像素矩形判定的按钮。</summary>
    public sealed class UiButton : IUiThemed
    {
        public readonly Rect HitRect;

        public readonly Text Label;

        public Action OnClick;

        public bool Enabled = true;

        readonly Image _fill;

        readonly UiButtonKind _kind;

        public UiButton(Rect hitRect, Image fill, Text label, UiButtonKind kind)
        {
            HitRect = hitRect;
            _fill = fill;
            Label = label;
            _kind = kind;
        }

        public void Apply(UiPalette palette)
        {
            if (!Enabled)
            {
                _fill.color = UiPalette.Alpha(palette.LineSoft, 1f);
                Label.color = palette.Faint;
                return;
            }

            switch (_kind)
            {
                case UiButtonKind.Primary:
                    _fill.color = palette.Jade;
                    Label.color = palette.Page;
                    break;
                case UiButtonKind.Quiet:
                    _fill.color = UiPalette.Alpha(palette.Card, 0f);
                    Label.color = palette.Muted;
                    break;
                default:
                    _fill.color = palette.BtnFill;
                    Label.color = palette.BtnText;
                    break;
            }
        }
    }

    /// <summary>状态标签。原型里成功 / 错误 / 禁用三档。</summary>
    public sealed class UiChip : IUiThemed
    {
        readonly Image _fill;

        readonly Text _label;

        readonly UiChipKind _kind;

        public UiChip(Image fill, Text label, UiChipKind kind)
        {
            _fill = fill;
            _label = label;
            _kind = kind;
        }

        public void Apply(UiPalette palette)
        {
            switch (_kind)
            {
                case UiChipKind.Success:
                    _fill.color = UiPalette.Alpha(palette.Jade, 0.18f);
                    _label.color = palette.Jade;
                    break;
                case UiChipKind.Error:
                    _fill.color = UiPalette.Alpha(palette.Danger, 0.18f);
                    _label.color = palette.Danger;
                    break;
                default:
                    _fill.color = palette.LineSoft;
                    _label.color = palette.Faint;
                    break;
            }
        }
    }

    /// <summary>一条分隔线。</summary>
    public sealed class UiDivider : IUiThemed
    {
        readonly Image _line;

        public UiDivider(Image line)
        {
            _line = line;
        }

        public void Apply(UiPalette palette)
        {
            _line.color = palette.LineSoft;
        }
    }
}
