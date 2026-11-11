using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// NPC 对白（原型 B03）。
    ///
    /// 九位「开口的人」与他们的定词、边界都写在 `config/dialogue/` 里，
    /// 屏上逐字照抄，不写进场景脚本、不改写、不缩短。
    /// 选中谁只看本地选中态；对白本身是配置文本，这一屏没有回包可等。
    /// </summary>
    public static class UiDialogueScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u4_dialogue_screen=ready";

        /// <summary>右边那几块会被反复改写的控件。</summary>
        sealed class Parts
        {
            public UiText Head;
            public UiText Meta;
            public UiText FirstText;
            public UiText FirstLimit;
            public UiText SecondText;
            public UiText SecondLimit;
        }

        /// <summary>建屏。返回建出来的人物条目个数，自检拿它核对。</summary>
        public static int Build(UiShell shell, UiWorldFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(90f);
            var gap = m.Px(40f);
            var leftW = (m.Width - pad * 2f - gap) * 0.42f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;
            flow.ClearViews();

            shell.Label(m.Band(pad, m.Px(80f), m.Width - pad * 2f, m.Px(60f)),
                "对白 · 开口的人", UiTextRole.Title, shell.Size(50f), TextAnchor.MiddleLeft, false);
            shell.Label(m.Band(pad, m.Px(146f), leftW, m.Px(44f)),
                "定词照方案 2.7 原文，只留配置，不写进场景",
                UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, true);

            // ---------------------------------------------------------- 左：九位

            var rowH = m.Px(132f);
            var rowGap = m.Px(12f);
            var listTop = m.Px(210f);
            var options = new List<UiOption>();
            var parts = new Parts();

            for (var i = 0; i < UiWorldCodex.Speakers.Length; i++)
            {
                var speaker = UiWorldCodex.Speakers[i];
                var sub = (speaker.Origin.Length > 0 ? speaker.Origin + " · " : string.Empty)
                    + "第 " + speaker.FirstChapter + " 场"
                    + (speaker.FirstRegion.Length > 0 ? " · " + speaker.FirstRegion : string.Empty);
                var option = new UiOption(
                    shell.Content, m.Band(pad, listTop + i * (rowH + rowGap), leftW, rowH),
                    speaker.Name, speaker.Name, sub,
                    UiOptionKind.Chip, shell.Size(30f), shell.Size(21f));
                option.Selected = i == 0;
                option.OnPick = picked => Show(shell, parts, options, picked.Id);
                shell.Register(option);
                options.Add(option);
            }

            // ---------------------------------------------------------- 右：定词

            parts.Head = shell.Label(m.Band(rightX, m.Px(210f), rightW, m.Px(56f)),
                string.Empty, UiTextRole.Title, shell.Size(36f), TextAnchor.MiddleLeft, false);
            parts.Meta = shell.Label(m.Band(rightX, m.Px(266f), rightW, m.Px(44f)),
                string.Empty, UiTextRole.Faint, shell.Size(22f), TextAnchor.MiddleLeft, true);

            var first = shell.Panel("第 1 场", m.Band(rightX, m.Px(320f), rightW, m.Px(400f)), shell.Size(26f));
            parts.FirstText = shell.LabelIn(first.Root,
                new Rect(m.Px(28f), m.Px(120f), rightW - m.Px(56f), m.Px(230f)),
                string.Empty, UiTextRole.Body, shell.Size(27f), TextAnchor.UpperLeft, true);
            parts.FirstLimit = shell.LabelIn(first.Root,
                new Rect(m.Px(28f), m.Px(40f), rightW - m.Px(56f), m.Px(70f)),
                string.Empty, UiTextRole.Faint, shell.Size(22f), TextAnchor.UpperLeft, true);

            var second = shell.Panel("第 2 场", m.Band(rightX, m.Px(740f), rightW, m.Px(400f)), shell.Size(26f));
            parts.SecondText = shell.LabelIn(second.Root,
                new Rect(m.Px(28f), m.Px(120f), rightW - m.Px(56f), m.Px(230f)),
                string.Empty, UiTextRole.Body, shell.Size(27f), TextAnchor.UpperLeft, true);
            parts.SecondLimit = shell.LabelIn(second.Root,
                new Rect(m.Px(28f), m.Px(40f), rightW - m.Px(56f), m.Px(70f)),
                string.Empty, UiTextRole.Faint, shell.Size(22f), TextAnchor.UpperLeft, true);

            shell.Label(new Rect(rightX, m.Height - m.Px(1220f), rightW, m.Px(48f)),
                "十四条定词不改写；对白只留配置，不进场景脚本。",
                UiTextRole.Muted, shell.Size(22f), TextAnchor.MiddleLeft, true);

            Show(shell, parts, options, UiWorldCodex.Speakers[0].Name);

            Debug.Log(ReadyLog + " speakers=" + options.Count
                + " with_second_chapter=" + CountMultiChapter());

            return options.Count;
        }

        /// <summary>换一位。定词从展示表取，展示表与配置逐字相同，这里不再加工。</summary>
        static void Show(UiShell shell, Parts parts, List<UiOption> options, string name)
        {
            for (var i = 0; i < options.Count; i++)
            {
                var selected = options[i].Id == name;
                if (options[i].Selected != selected)
                {
                    options[i].Selected = selected;
                    options[i].Apply(shell.Palette);
                }
            }

            UiWorldCodex.Speaker speaker;
            if (!Find(name, out speaker))
            {
                return;
            }

            parts.Head.Label.text = speaker.Name;
            parts.Meta.Label.text = (speaker.Origin.Length > 0 ? speaker.Origin + " · " : string.Empty)
                + "第 " + speaker.FirstChapter + " 场"
                + (speaker.FirstRegion.Length > 0 ? " · " + speaker.FirstRegion : string.Empty)
                + " · 共 " + speaker.LineCount + " 场";

            parts.FirstText.Label.text = "「" + speaker.Text + "」";
            parts.FirstLimit.Label.text = "限制：" + speaker.Limit;

            // 第二块只放配置里真有的那一段。原型 B03 要展示乔小满的两场，
            // 别的多场角色这里不拿他的台词顶上，只写明「还有几场在配置里」。
            if (!string.IsNullOrEmpty(speaker.SecondText))
            {
                parts.SecondText.Label.text = "「" + speaker.SecondText + "」";
                parts.SecondLimit.Label.text = "限制：" + speaker.SecondLimit;
            }
            else if (speaker.LineCount > 1)
            {
                parts.SecondText.Label.text = "这一位在配置里还有 " + (speaker.LineCount - 1)
                    + " 场，本屏只按原型展示第一场。";
                parts.SecondLimit.Label.text = "不补台词、不串用别人的定词";
            }
            else
            {
                parts.SecondText.Label.text = "这一位只有一场，没有第 2 场。";
                parts.SecondLimit.Label.text = "不加台词、不补戏";
            }
        }

        static int CountMultiChapter()
        {
            var count = 0;
            for (var i = 0; i < UiWorldCodex.Speakers.Length; i++)
            {
                if (UiWorldCodex.Speakers[i].LineCount > 1)
                {
                    count++;
                }
            }

            return count;
        }

        static bool Find(string name, out UiWorldCodex.Speaker speaker)
        {
            for (var i = 0; i < UiWorldCodex.Speakers.Length; i++)
            {
                if (UiWorldCodex.Speakers[i].Name == name)
                {
                    speaker = UiWorldCodex.Speakers[i];
                    return true;
                }
            }

            speaker = default;
            return false;
        }
    }
}
