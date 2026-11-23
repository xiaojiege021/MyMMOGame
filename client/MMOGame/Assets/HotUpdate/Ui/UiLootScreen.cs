using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U15 掷点屏（副本内掉落归属与表态）。
    ///
    /// 归属逐行照抄战斗服下发的 `LootAssigned`：谁拿到就是谁，`winner_character_id = 0`
    /// 写「没人要」。掷点本身不在客户端：这一屏没有随机、没有点数、没有区间，
    /// 三个按钮只把 need / greed / pass 这三个词发出去。
    /// </summary>
    public static class UiLootScreen
    {
        public static int Build(UiShell shell, UiLootFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var rowTop = top + m.Px(64f);
            var rowH = m.Px(150f);
            var rowGap = m.Px(16f);
            var btnW = m.Px(150f);

            shell.Label(new Rect(pad, m.Height - top - m.Px(40f), m.Width - pad * 2f, m.Px(40f)),
                UiLootCodex.Notice, UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);

            var rows = flow.Rows;
            if (!flow.HasRows || rows.Count == 0)
            {
                var empty = shell.Panel(null, m.Band(pad, rowTop, m.Width - pad * 2f, rowH), shell.Size(28f));
                shell.LabelIn(empty.Root, new Rect(m.Px(28f), m.Px(24f),
                    m.Width - pad * 2f - m.Px(56f), rowH - m.Px(48f)),
                    UiLootCodex.EmptyList, UiTextRole.Faint, shell.Size(24f), TextAnchor.MiddleLeft, true);

                Debug.Log(UiLootCodex.ReadyLog + " rows=0 mode=" + flow.Mode);
                return 0;
            }

            var shown = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var panel = shell.Panel(null, m.Band(pad, rowTop + i * (rowH + rowGap),
                    m.Width - pad * 2f, rowH), shell.Size(28f));

                shell.LabelIn(panel.Root, new Rect(m.Px(28f), rowH - m.Px(76f),
                    m.Width - pad * 2f - btnW * 3f - m.Px(160f), m.Px(48f)),
                    UiLootCodex.ItemLine(row), UiTextRole.Title, shell.Size(30f), TextAnchor.MiddleLeft, false);

                var owner = row.WinnerCharacterId == 0;
                shell.Chip(panel.Root, new Rect(m.Px(28f), m.Px(28f), m.Px(260f), m.Px(42f)),
                    UiLootCodex.OwnerLine(row), owner ? UiChipKind.Mute : UiChipKind.Success);

                var x = m.Width - pad * 2f - btnW * 3f - m.Px(48f);
                var itemId = row.ItemId;
                shell.ButtonIn(panel.Root, new Rect(x, m.Px(40f), btnW, m.Px(72f)),
                    UiLootCodex.Word(UiLootCodex.Need),
                    () => flow.Decide(itemId, UiLootCodex.Need), UiButtonKind.Primary);
                shell.ButtonIn(panel.Root, new Rect(x + btnW + m.Px(12f), m.Px(40f), btnW, m.Px(72f)),
                    UiLootCodex.Word(UiLootCodex.Greed),
                    () => flow.Decide(itemId, UiLootCodex.Greed), UiButtonKind.Normal);
                shell.ButtonIn(panel.Root,
                    new Rect(x + (btnW + m.Px(12f)) * 2f, m.Px(40f), btnW, m.Px(72f)),
                    UiLootCodex.Word(UiLootCodex.Pass),
                    () => flow.Decide(itemId, UiLootCodex.Pass), UiButtonKind.Quiet);

                shown++;
            }

            var footTop = rowTop + rows.Count * (rowH + rowGap) + m.Px(8f);
            shell.Label(new Rect(pad, m.Height - footTop - m.Px(64f), m.Width - pad * 2f, m.Px(64f)),
                "归属与点数都在战斗服 · 这一屏只发意图、只公布结果",
                UiTextRole.Faint, shell.Size(22f), TextAnchor.MiddleLeft, true);

            Debug.Log(UiLootCodex.ReadyLog + " rows=" + shown + " mode=" + flow.Mode
                + " nobody=" + flow.CountNobody() + " intents=" + flow.Intents);
            return shown;
        }
    }
}
