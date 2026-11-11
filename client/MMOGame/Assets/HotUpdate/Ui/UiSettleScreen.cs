using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 战斗结算（原型 C02）。
    ///
    /// 通没通关、结算事务号、等级经验三样都来自战斗服的 `Ended` 回包；
    /// 疲劳与份额来自 `/dungeon/enter` 回包。屏上不按血量、掉落或人数自己推一个结果，
    /// 也不给经验加成翻倍——加成在服务端那一笔 `SettleExp` 里只算一次。
    ///
    /// 掉落这一格只写「以角色服背包回包为准」：这张卡没有掉落表，
    /// 编一份出来就是拿本地数当权威。
    /// </summary>
    public static class UiSettleScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u3_settle_screen=ready";

        /// <summary>建屏。</summary>
        public static void Build(UiShell shell, UiDungeonFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(150f);
            var width = m.Width - pad * 2f;

            var state = shell.Label(new Rect(pad, m.Height - top - m.Px(96f), width, m.Px(96f)),
                "未 结 算", UiTextRole.Title, shell.Size(64f), TextAnchor.MiddleCenter, false);
            var head = shell.Label(new Rect(pad, m.Height - top - m.Px(150f), width, m.Px(48f)),
                "等战斗服的结算回包", UiTextRole.Muted, shell.Size(26f), TextAnchor.MiddleCenter, false);

            // ---------------------------------------------------------- 三块口径

            var boxTop = top + m.Px(176f);
            var gap = m.Px(28f);
            var colW = (width - gap * 2f) / 3f;
            var boxH = m.Px(340f);

            var exp = shell.Panel("等级经验", m.Band(pad, boxTop, colW, boxH), shell.Size(30f));
            var expText = shell.LabelIn(exp.Root,
                new Rect(m.Px(28f), m.Px(92f), colW - m.Px(56f), m.Px(180f)),
                "等结算回包", UiTextRole.Body, shell.Size(26f), TextAnchor.UpperLeft, true);
            shell.LabelIn(exp.Root, new Rect(m.Px(28f), m.Px(30f), colW - m.Px(56f), m.Px(56f)),
                UiBattleCodex.SettleExpNote, UiTextRole.Faint, shell.Size(20f), TextAnchor.MiddleLeft, true);

            var drop = shell.Panel("掉落", m.Band(pad + colW + gap, boxTop, colW, boxH), shell.Size(30f));
            var dropText = shell.LabelIn(drop.Root,
                new Rect(m.Px(28f), m.Px(92f), colW - m.Px(56f), m.Px(180f)),
                UiBattleCodex.SettleDropNote, UiTextRole.Body, shell.Size(26f), TextAnchor.UpperLeft, true);
            shell.LabelIn(drop.Root, new Rect(m.Px(28f), m.Px(30f), colW - m.Px(56f), m.Px(56f)),
                "产出 = 单人一份 × 进本人数，份数在回包里", UiTextRole.Faint, shell.Size(20f),
                TextAnchor.MiddleLeft, true);

            var cost = shell.Panel("消耗", m.Band(pad + (colW + gap) * 2f, boxTop, colW, boxH), shell.Size(30f));
            var costText = shell.LabelIn(cost.Root,
                new Rect(m.Px(28f), m.Px(92f), colW - m.Px(56f), m.Px(180f)),
                "进本回包没到，消耗口径以回包为准", UiTextRole.Body, shell.Size(26f),
                TextAnchor.UpperLeft, true);
            shell.LabelIn(cost.Root, new Rect(m.Px(28f), m.Px(30f), colW - m.Px(56f), m.Px(56f)),
                UiBattleCodex.SettleCostNote, UiTextRole.Faint, shell.Size(20f), TextAnchor.MiddleLeft, true);

            // ---------------------------------------------------------- 底：事务号与出口

            var footTop = boxTop + boxH + m.Px(36f);
            shell.Divider(shell.Content, m.Band(pad, footTop, width, m.Px(2f)));

            var backTop = footTop + m.Px(28f);
            var button = shell.Button(m.Band(pad, backTop + m.Px(96f), m.Px(420f), m.Px(104f)),
                "返回表世界", () => shell.Toast("表世界入口等 U4 接上"), UiButtonKind.Primary);

            flow.BindSettle(state.Label, head.Label, expText.Label, costText.Label, dropText.Label);

            Debug.Log(ReadyLog + " blocks=3 footer=" + (button != null ? "ready" : "none"));
        }
    }
}
