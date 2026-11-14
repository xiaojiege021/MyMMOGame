using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 竞技场与战场（原型 F03）。左边讲三层战力怎么分开，右边是这一卡能提供的入口。
    ///
    /// 界面不算战力、不乘平衡系数、不读配置：数值只来自战斗服回包（P2-3 做到的
    /// 是按模板与平衡系数开战）。匹配和进场没有 HTTP 入口，所以这一屏只写分层文案，
    /// 并如实标明「本卡无匹配入口」，不做假按钮。
    /// </summary>
    public static class UiArenaScreen
    {
        public const string ReadyLog = "u7_arena_screen=ready";

        public static void Build(UiShell shell, UiSocialFlow flow)
        {
            UiUpgradeViews.Note(shell, UiSocialCodex.VersusNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(16f);
            var leftW = (m.Width - pad * 2f - m.Px(40f)) * 0.46f;
            var rightX = pad + leftW + m.Px(40f);
            var rightW = m.Width - pad - rightX;

            // ---------------------------------------------------------- 左：三层

            var layersH = m.Px(84f) + UiSocialCodex.Layers.Length * m.Px(96f) + m.Px(16f);
            var layers = shell.Panel("三种场，三套算法", m.Band(pad, top + m.Px(56f), leftW, layersH),
                shell.Size(30f));
            for (var i = 0; i < UiSocialCodex.Layers.Length; i++)
            {
                var row = UiSocialCodex.Layers[i];
                var y = layersH - m.Px(84f) - m.Px(88f) - i * m.Px(96f);
                shell.LabelIn(layers.Root, new Rect(m.Px(28f), y + m.Px(38f), leftW - m.Px(56f), m.Px(36f)),
                    row[0], UiTextRole.Body, shell.Size(24f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(layers.Root, new Rect(m.Px(28f), y, leftW - m.Px(56f), m.Px(34f)),
                    row[1], UiTextRole.Muted, shell.Size(21f), TextAnchor.MiddleLeft, false);
            }

            var rewardTop = top + m.Px(56f) + layersH + gap;
            var reward = shell.Panel("奖励取向", m.Band(pad, rewardTop, leftW, m.Px(220f)), shell.Size(30f));
            shell.LabelIn(reward.Root, new Rect(m.Px(28f), m.Px(28f), leftW - m.Px(56f), m.Px(140f)),
                UiSocialCodex.VersusReward, UiTextRole.Body, shell.Size(23f), TextAnchor.UpperLeft, true);

            var gapTop = rewardTop + m.Px(220f) + gap;
            var missing = shell.Panel("这一屏的边界", m.Band(pad, gapTop, leftW, m.Px(240f)), shell.Size(30f));
            shell.LabelIn(missing.Root, new Rect(m.Px(28f), m.Px(28f), leftW - m.Px(56f), m.Px(120f)),
                UiSocialCodex.VersusGap, UiTextRole.Warm, shell.Size(22f), TextAnchor.UpperLeft, true);
            shell.Chip(missing.Root, new Rect(m.Px(28f), m.Px(152f), m.Px(240f), m.Px(52f)),
                "无匹配入口", UiChipKind.Mute);

            // ---------------------------------------------------------- 右：三个场

            var modes = new[]
            {
                "竞技场 1v1",
                "竞技场 3v3",
                "战场 10v10",
                "城战 / 领地",
            };
            var notes = new[]
            {
                "属性模板统一，只留职业与分支 · 不看装备",
                "同 1v1，人数按队伍配 · 平衡系数由战斗服压",
                "装备生效，同队战力压缩 · 数值只认回包",
                "装备全额生效，再加宗门科技 · 见「领地城战」屏",
            };

            var cardH = m.Px(148f);
            var listTitleH = m.Px(56f);
            var listH = cardH * modes.Length + m.Px(28f) + listTitleH;
            var list = shell.Panel("入口现状", m.Band(rightX, top + m.Px(56f), rightW, listH), shell.Size(30f));
            for (var i = 0; i < modes.Length; i++)
            {
                var y = listH - m.Px(84f) - (i + 1) * cardH + m.Px(24f);
                shell.LabelIn(list.Root, new Rect(m.Px(28f), y + m.Px(46f), rightW - m.Px(56f), m.Px(36f)),
                    modes[i], UiTextRole.Body, shell.Size(25f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(list.Root, new Rect(m.Px(28f), y, rightW - m.Px(56f), m.Px(34f)),
                    notes[i], UiTextRole.Muted, shell.Size(21f), TextAnchor.MiddleLeft, false);
                shell.Chip(list.Root,
                    new Rect(rightW - m.Px(28f) - m.Px(170f), y + m.Px(30f), m.Px(170f), m.Px(44f)),
                    i == 3 ? "看领地层" : "无入口", UiChipKind.Mute);
            }

            var liveTop = top + m.Px(56f) + listH + gap;
            var live = shell.Panel("本场数值", m.Band(rightX, liveTop, rightW, m.Px(220f)), shell.Size(30f));
            UiUpgradeViews.Result(shell, live, m.Px(120f), "战力来源", "战斗服回包");
            UiUpgradeViews.Result(shell, live, m.Px(72f), "平衡系数", "由战斗服压，界面不乘");
            shell.LabelIn(live.Root, new Rect(m.Px(28f), m.Px(20f), rightW - m.Px(56f), m.Px(44f)),
                "没有回包就不显示数字：这一屏不拿装备面板自己算一份战力顶上去。",
                UiTextRole.Faint, shell.Size(20f), TextAnchor.MiddleLeft, false);

            Debug.Log(ReadyLog + " modes=" + modes.Length
                + " layers=" + UiSocialCodex.Layers.Length
                + " requests=" + flow.Requests);
        }
    }
}
