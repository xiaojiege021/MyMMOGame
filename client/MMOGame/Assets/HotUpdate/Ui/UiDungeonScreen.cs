using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 裂隙·副本入口（原型 C03）。
    ///
    /// 列的是 `config/dungeons.json` 里真有的三条——主线、深渊、巢穴；
    /// 龙巢、世界 Boss、宗门秘境在副本表里还没有，这里只写一句「后续卡」，不列成可点的行。
    ///
    /// 屏上一个数都不算：人数只能在 1 到这张副本的 max_players 之间动，
    /// 疲劳、日上限、本周剩余份额、每人几份全部等 `/dungeon/enter` 回包，
    /// 没收到回包就写「尚未进本」。
    /// </summary>
    public static class UiDungeonScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u3_dungeon_screen=ready";

        /// <summary>建屏。返回建出来的页签个数，自检拿它核对。</summary>
        public static int Build(UiShell shell, UiDungeonFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.56f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;

            shell.Label(new Rect(pad, m.Height - top - m.Px(40f), m.Width - pad * 2f, m.Px(40f)),
                "副本 / 战场的唯一通道 · 准入只看等级与境界门禁 · 人数与消耗都由服务端算",
                UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);

            // ---------------------------------------------------------- 左：三条副本

            var options = new List<UiOption>();
            var rowH = m.Px(150f);
            var rowGap = m.Px(18f);
            var rowTop = top + m.Px(64f);
            for (var i = 0; i < UiBattleCodex.Dungeons.Length; i++)
            {
                var dungeon = UiBattleCodex.Dungeons[i];
                var rect = m.Band(pad, rowTop + i * (rowH + rowGap), leftW, rowH);
                var option = new UiOption(
                    shell.Content, rect, dungeon.Id, dungeon.Name, dungeon.OneLine,
                    UiOptionKind.Card, shell.Size(34f), shell.Size(22f));
                option.Selected = dungeon.Id == flow.ChosenId;
                option.OnPick = picked => flow.Choose(picked.Id);
                shell.Register(option);
                options.Add(option);
            }

            var noteTop = rowTop + UiBattleCodex.Dungeons.Length * (rowH + rowGap) + m.Px(16f);
            shell.Label(new Rect(pad, m.Height - noteTop - m.Px(48f), leftW, m.Px(48f)),
                UiBattleCodex.NotYet + "：" + UiBattleCodex.NotYetNote,
                UiTextRole.Faint, shell.Size(22f), TextAnchor.MiddleLeft, true);

            // ---------------------------------------------------------- 右：人数、口径、进本

            var boxH = m.Px(300f);
            var size = shell.Panel("进本人数", m.Band(rightX, rowTop, rightW, boxH), shell.Size(30f));
            var sizeText = shell.LabelIn(size.Root,
                new Rect(m.Px(28f), boxH - m.Px(130f), rightW - m.Px(56f), m.Px(52f)),
                "进本人数 1", UiTextRole.Title, shell.Size(34f), TextAnchor.MiddleLeft, false);
            var minus = shell.ButtonIn(size.Root,
                new Rect(m.Px(28f), m.Px(40f), m.Px(120f), m.Px(80f)), "−",
                () => flow.StepParty(-1), UiButtonKind.Quiet);
            var plus = shell.ButtonIn(size.Root,
                new Rect(m.Px(164f), m.Px(40f), m.Px(120f), m.Px(80f)), "+",
                () => flow.StepParty(1), UiButtonKind.Quiet);
            shell.LabelIn(size.Root, new Rect(m.Px(300f), m.Px(40f), rightW - m.Px(328f), m.Px(80f)),
                "人数是这一场几个人，产出与扣除都由服务端乘",
                UiTextRole.Faint, shell.Size(22f), TextAnchor.MiddleLeft, true);

            var ruleTop = rowTop + boxH + m.Px(20f);
            var rule = shell.Panel("倍率口径", m.Band(rightX, ruleTop, rightW, m.Px(240f)), shell.Size(30f));
            shell.LabelIn(rule.Root, new Rect(m.Px(28f), m.Px(96f), rightW - m.Px(56f), m.Px(104f)),
                "每人产出 = 单人一份 × 人数；每人扣的疲劳 / 周份额 = 同样倍数。\n"
                + "一天或一周的总产出不变，省的是次数和时间。",
                UiTextRole.Body, shell.Size(24f), TextAnchor.UpperLeft, true);

            var enterTop = ruleTop + m.Px(240f) + m.Px(20f);
            var enter = shell.Button(m.Band(rightX, enterTop, rightW, m.Px(104f)),
                "进入副本", () => flow.SubmitEnter(), UiButtonKind.Primary);

            // ---------------------------------------------------------- 底：回包那一行

            var resultTop = enterTop + m.Px(104f) + m.Px(24f);
            var result = shell.Label(new Rect(rightX, m.Height - resultTop - m.Px(44f), rightW, m.Px(44f)),
                "尚未进本 · 疲劳与份额以回包为准", UiTextRole.Body, shell.Size(24f),
                TextAnchor.MiddleLeft, true);
            var hint = shell.Label(new Rect(rightX, m.Height - resultTop - m.Px(100f), rightW, m.Px(44f)),
                string.Empty, UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, true);

            flow.BindDungeon(options, sizeText.Label, result.Label, hint.Label, enter);

            Debug.Log(ReadyLog + " dungeons=" + options.Count
                + " chosen=" + flow.ChosenId
                + " party=" + flow.PartySize
                + " not_yet=" + UiBattleCodex.NotYet);

            return options.Count;
        }
    }
}
