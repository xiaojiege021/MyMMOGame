using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>选中态卡片与签。出身四选一用卡片，职责五选一用签。</summary>
    public enum UiOptionKind
    {
        Card,
        Chip,
    }

    /// <summary>
    /// 可选项。选中只改颜色与描边，不动位置尺寸，也不改文案——
    /// 这样切皮肤与切换选择走的是同一条 <see cref="Apply"/>，界面不会跳。
    ///
    /// 只记「选了哪一个」，不对选项本身做任何判定：出身不产生属性、不锁职业，
    /// 选完由服务端认不认。
    /// </summary>
    public sealed class UiOption : IUiThemed
    {
        /// <summary>点击判定矩形，屏幕像素。</summary>
        public readonly Rect HitRect;

        /// <summary>选项编号，交给服务端的就是这个（`chen` / `warrior` 这种）。</summary>
        public readonly string Id;

        /// <summary>名字，界面按选择改颜色用得到。</summary>
        public readonly Text Label;

        /// <summary>选中后回调。参数是它自己，调用方不用再自己找。</summary>
        public Action<UiOption> OnPick;

        public bool Selected;

        readonly Image _fill;

        readonly List<Image> _edges = new List<Image>();

        readonly Text _sub;

        readonly UiOptionKind _kind;

        public UiOption(
            Transform parent, Rect rect, string id, string name, string sub,
            UiOptionKind kind, int nameSize, int subSize)
        {
            Id = id;
            _kind = kind;
            HitRect = rect;

            var root = UiBuild.Rect("Option", rect, parent);
            _fill = UiBuild.Box("OptionFill", new Rect(0f, 0f, rect.width, rect.height), root);

            var edge = kind == UiOptionKind.Card ? 2f : 1.5f;
            _edges.Add(UiBuild.Box("EdgeT", new Rect(0f, rect.height - edge, rect.width, edge), root));
            _edges.Add(UiBuild.Box("EdgeB", new Rect(0f, 0f, rect.width, edge), root));
            _edges.Add(UiBuild.Box("EdgeL", new Rect(0f, 0f, edge, rect.height), root));
            _edges.Add(UiBuild.Box("EdgeR", new Rect(rect.width - edge, 0f, edge, rect.height), root));

            if (kind == UiOptionKind.Card)
            {
                Label = UiBuild.Label("OptionName",
                    new Rect(rect.width * 0.08f, rect.height * 0.46f, rect.width * 0.84f, rect.height * 0.34f),
                    nameSize, TextAnchor.MiddleLeft, UiFont.Serif(), false, root);
                _sub = UiBuild.Label("OptionSub",
                    new Rect(rect.width * 0.08f, rect.height * 0.12f, rect.width * 0.84f, rect.height * 0.36f),
                    subSize, TextAnchor.UpperLeft, UiFont.Sans(), true, root);
            }
            else
            {
                Label = UiBuild.Label("OptionName", new Rect(0f, 0f, rect.width, rect.height),
                    nameSize, TextAnchor.MiddleCenter, UiFont.Sans(), false, root);
                _sub = null;
            }

            Label.text = name;
            if (_sub != null)
            {
                _sub.text = sub == null ? string.Empty : sub;
            }
        }

        public void Apply(UiPalette palette)
        {
            if (Selected)
            {
                _fill.color = UiPalette.Alpha(palette.Jade, _kind == UiOptionKind.Card ? 0.14f : 0.22f);
                Label.color = palette.JadeBright;
                if (_sub != null)
                {
                    _sub.color = palette.Muted;
                }

                for (var i = 0; i < _edges.Count; i++)
                {
                    _edges[i].color = palette.Jade;
                }

                return;
            }

            _fill.color = palette.Card;
            Label.color = palette.Text;
            if (_sub != null)
            {
                _sub.color = palette.Muted;
            }

            for (var i = 0; i < _edges.Count; i++)
            {
                _edges[i].color = palette.LineSoft;
            }
        }
    }
}
