using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 采集节点（原型 B04）。
    ///
    /// 节点、分组、产物、数量、格位、刷新秒数都照 `config/gather.json`；
    /// 灵石矿脉只登记归属，屏上写明「禁止个人采集」，不给采集按钮。
    /// 采到没采到、采到什么、进第几格，全部等 `/world/gather` 回包，
    /// 没回包就写「尚未采集」，不拿配置里的产量当成背包里的东西。
    /// </summary>
    public static class UiGatherScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u4_gather_screen=ready";

        /// <summary>建屏。返回这一组建出来的节点卡片个数，自检拿它核对。</summary>
        public static int Build(UiShell shell, UiWorldFlow flow)
        {
            return Build(shell, flow, "矿脉");
        }

        /// <summary>按分组建屏。页签点了就按新分组重建这一屏。</summary>
        public static int Build(UiShell shell, UiWorldFlow flow, string tab)
        {
            var m = shell.Metrics;
            var pad = m.Px(90f);
            var wide = m.Width - pad * 2f;
            flow.ClearViews();

            shell.Label(m.Band(pad, m.Px(80f), wide, m.Px(60f)),
                "采集", UiTextRole.Title, shell.Size(50f), TextAnchor.MiddleLeft, false);
            shell.Label(m.Band(pad, m.Px(146f), wide, m.Px(44f)),
                "谁都能学、可同时学 · 不耗疲劳 · 灵石矿脉不开放个人采集",
                UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, true);

            // ---------------------------------------------------------- 页签

            var tabTop = m.Px(206f);
            var tabW = m.Px(260f);
            var options = new List<UiOption>();
            for (var i = 0; i < UiWorldCodex.GatherTabs.Length; i++)
            {
                var name = UiWorldCodex.GatherTabs[i];
                var option = new UiOption(
                    shell.Content, m.Band(pad + i * (tabW + m.Px(16f)), tabTop, tabW, m.Px(84f)),
                    name, name, string.Empty, UiOptionKind.Chip, shell.Size(28f), shell.Size(20f));
                option.Selected = name == tab;
                var picked = name;
                option.OnPick = _ =>
                {
                    shell.ShowPage("采集", s => Build(s, flow, picked));
                };
                shell.Register(option);
                options.Add(option);
            }

            // ---------------------------------------------------------- 节点卡片

            var kind = UiWorldCodex.KindOfTab(tab);
            var nodes = UiWorldCodex.NodesOf(kind);
            var cards = new List<UiPanel>();
            var buttons = new List<UiButton>();
            var cardTop = m.Px(320f);
            var cardGap = m.Px(24f);
            var cardW = nodes.Count > 0 ? (wide - cardGap * (nodes.Count - 1)) / nodes.Count : wide;
            var cardH = m.Px(420f);

            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var card = shell.Panel(node.Id, m.Band(pad + i * (cardW + cardGap), cardTop, cardW, cardH),
                    shell.Size(26f));
                cards.Add(card);

                shell.LabelIn(card.Root, new Rect(m.Px(24f), cardH - m.Px(150f), cardW - m.Px(48f), m.Px(110f)),
                    node.Region + " · " + node.Kind + "\n"
                    + "产物 " + (node.ItemId.Length > 0 ? node.ItemId + " ×" + node.Count : "无（只登记归属）")
                    + "\n入格位 " + node.BagSlot + " · 刷新 " + node.RespawnSeconds + "s（待定）",
                    UiTextRole.Body, shell.Size(24f), TextAnchor.UpperLeft, true);

                if (node.Forbidden)
                {
                    shell.Chip(card.Root, new Rect(m.Px(24f), m.Px(60f), m.Px(280f), m.Px(64f)),
                        "禁止个人采集", UiChipKind.Error);
                    shell.LabelIn(card.Root, new Rect(m.Px(24f), m.Px(140f), cardW - m.Px(48f), m.Px(56f)),
                        "不许配产物 / 刷新，也不许删",
                        UiTextRole.Danger, shell.Size(21f), TextAnchor.MiddleLeft, true);
                }
                else
                {
                    var id = node.Id;
                    buttons.Add(shell.ButtonIn(card.Root,
                        new Rect(m.Px(24f), m.Px(60f), m.Px(220f), m.Px(84f)),
                        "采集", () => flow.Gather(id), UiButtonKind.Primary));
                }
            }

            // ---------------------------------------------------------- 底部：回包 + 规则

            var ruleTop = cardTop + cardH + m.Px(24f);
            var line = shell.Label(m.Band(pad, ruleTop, wide, m.Px(56f)),
                UiWorldCodex.NoGatherYet, UiTextRole.Muted, shell.Size(26f), TextAnchor.MiddleLeft, true);

            var rule = shell.Panel("请求字段与拒绝顺序",
                m.Band(pad, ruleTop + m.Px(72f), wide, m.Px(260f)), shell.Size(26f));
            shell.LabelIn(rule.Root, new Rect(m.Px(26f), m.Px(150f), wide - m.Px(52f), m.Px(90f)),
                "请求只有 node_id。账号取自令牌，网关复核角色归属；"
                + "成功复用角色服的入包路径，产物与格位由回包给。",
                UiTextRole.Body, shell.Size(23f), TextAnchor.UpperLeft, true);
            shell.LabelIn(rule.Root, new Rect(m.Px(26f), m.Px(20f), wide - m.Px(52f), m.Px(120f)),
                UiWorldCodex.GatherRejectOrder,
                UiTextRole.Faint, shell.Size(21f), TextAnchor.UpperLeft, true);

            flow.RegisterView(() =>
            {
                line.Label.text = flow.GatherLine();
                for (var i = 0; i < buttons.Count; i++)
                {
                    buttons[i].Enabled = !flow.Busy;
                }
            });

            Debug.Log(ReadyLog + " tab=" + tab + " kind=" + kind
                + " nodes=" + nodes.Count + " forbidden=" + CountForbidden(nodes));

            return nodes.Count;
        }

        static int CountForbidden(List<UiWorldCodex.GatherNode> nodes)
        {
            var count = 0;
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Forbidden)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
