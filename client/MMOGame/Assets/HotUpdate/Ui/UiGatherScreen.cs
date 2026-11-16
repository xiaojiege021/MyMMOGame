using System.Collections.Generic;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 采集节点（原型 B04）。
    ///
    /// 节点清单只来自 <c>GET /world/gather/nodes</c>；没回包时写「尚未请求」。
    /// 采到没采到、采到什么、进第几格，全部等 <c>/world/gather</c> 回包。
    /// </summary>
    public static class UiGatherScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "n4_gather_screen=ready";

        /// <summary>一屏横向展示的卡片数，多了就翻截。</summary>
        public const int CardsPerPage = 2;

        static int _oreWindow;

        static int _herbWindow;

        static int _spiritWindow;

        /// <summary>这一屏建好的日志行（U4 自检仍认 u4_gather_screen）。</summary>
        public const string U4ReadyLog = "u4_gather_screen=ready";

        public static int Build(UiShell shell, UiWorldFlow flow)
        {
            return Build(shell, flow, "矿脉");
        }

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

            var kind = UiWorldCodex.KindOfTab(tab);
            var nodes = flow.NodesOfKind(kind);
            var window = WindowOf(tab);
            var total = nodes.Count;

            var listTop = m.Px(310f);
            shell.Label(m.Band(pad, listTop, wide, m.Px(44f)),
                NodeListCaption(flow, kind, total),
                UiTextRole.Muted, shell.Size(23f), TextAnchor.MiddleLeft, true);

            var cards = new List<UiPanel>();
            var buttons = new List<UiButton>();
            var cardTop = m.Px(360f);
            var cardGap = m.Px(24f);
            var cardW = CardsPerPage > 0
                ? (wide - cardGap * (CardsPerPage - 1)) / CardsPerPage
                : wide;
            var cardH = m.Px(420f);

            if (!flow.HasGatherNodes)
            {
                shell.Label(m.Band(pad, cardTop, wide, m.Px(120f)),
                    UiWorldCodex.NoGatherNodesYet,
                    UiTextRole.Warm, shell.Size(28f), TextAnchor.MiddleLeft, true);
            }
            else if (total == 0)
            {
                shell.Label(m.Band(pad, cardTop, wide, m.Px(80f)),
                    "回包 0 个 · " + kind, UiTextRole.Muted, shell.Size(26f), TextAnchor.MiddleLeft, false);
            }
            else
            {
                var start = window * CardsPerPage;
                var end = Mathf.Min(start + CardsPerPage, total);
                var onRow = end - start;
                for (var i = 0; i < onRow; i++)
                {
                    var node = nodes[start + i];
                    var card = shell.Panel(node.NodeId,
                        m.Band(pad + i * (cardW + cardGap), cardTop, cardW, cardH),
                        shell.Size(26f));
                    cards.Add(card);

                    shell.LabelIn(card.Root, new Rect(m.Px(24f), cardH - m.Px(150f), cardW - m.Px(48f), m.Px(110f)),
                        node.RegionId + " · " + node.Kind + "\n"
                        + "产物 " + (node.ItemId.Length > 0 ? node.ItemId + " ×" + node.Count : "无（只登记归属）")
                        + "\n入格位 " + node.BagSlot + " · 刷新 " + node.RespawnSeconds + "s",
                        UiTextRole.Body, shell.Size(24f), TextAnchor.UpperLeft, true);

                    if (node.Forbidden)
                    {
                        shell.Chip(card.Root, new Rect(m.Px(24f), m.Px(60f), m.Px(280f), m.Px(64f)),
                            "禁止个人采集", UiChipKind.Error);
                    }
                    else
                    {
                        var id = node.NodeId;
                        buttons.Add(shell.ButtonIn(card.Root,
                            new Rect(m.Px(24f), m.Px(60f), m.Px(220f), m.Px(84f)),
                            "采集", () => flow.Gather(id), UiButtonKind.Primary));
                    }
                }

                if (total > CardsPerPage)
                {
                    var navY = cardTop + cardH + m.Px(12f);
                    shell.Button(m.Band(pad, navY, m.Px(200f), m.Px(72f)),
                        "上一截", () =>
                        {
                            StepWindow(tab, -1, total);
                            shell.ShowPage("采集", s => Build(s, flow, tab));
                        }, UiButtonKind.Quiet);
                    shell.Button(m.Band(pad + m.Px(220f), navY, m.Px(200f), m.Px(72f)),
                        "下一截", () =>
                        {
                            StepWindow(tab, 1, total);
                            shell.ShowPage("采集", s => Build(s, flow, tab));
                        }, UiButtonKind.Quiet);
                }
            }

            var loadY = cardTop + cardH + (total > CardsPerPage ? m.Px(100f) : m.Px(24f));
            shell.Button(m.Band(pad, loadY, m.Px(320f), m.Px(84f)),
                "读取节点表",
                () => flow.LoadGatherNodes(() => shell.ShowPage("采集", s => Build(s, flow, tab))),
                UiButtonKind.Quiet);

            var ruleTop = loadY + m.Px(84f) + m.Px(24f);
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
                + " listed=" + total + " window=" + window + " has_nodes=" + flow.HasGatherNodes);
            Debug.Log(U4ReadyLog + " tab=" + tab + " kind=" + kind + " nodes=" + total);

            return total;
        }

        static string NodeListCaption(UiWorldFlow flow, string kind, int total)
        {
            if (!flow.HasGatherNodes)
            {
                return "节点表 · 尚未请求";
            }

            return "节点表 · 回包 " + flow.GatherNodesReply.Count + " 个 · 本组 " + kind + " " + total + " 个";
        }

        static int WindowOf(string tab)
        {
            switch (tab)
            {
                case "药草":
                    return _herbWindow;
                case "灵石矿脉":
                    return _spiritWindow;
                default:
                    return _oreWindow;
            }
        }

        public static void StepWindow(string tab, int delta, int total)
        {
            var pages = CardsPerPage > 0 ? (total + CardsPerPage - 1) / CardsPerPage : 1;
            if (pages < 1)
            {
                pages = 1;
            }

            switch (tab)
            {
                case "药草":
                    _herbWindow = Mathf.Clamp(_herbWindow + delta, 0, pages - 1);
                    break;
                case "灵石矿脉":
                    _spiritWindow = Mathf.Clamp(_spiritWindow + delta, 0, pages - 1);
                    break;
                default:
                    _oreWindow = Mathf.Clamp(_oreWindow + delta, 0, pages - 1);
                    break;
            }
        }

        public static void ResetGatherWindows()
        {
            _oreWindow = 0;
            _herbWindow = 0;
            _spiritWindow = 0;
        }
    }
}