using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 世界地图与跨区（原型 B02）。
    ///
    /// 八块地表与它们的邻接、开放、对白编号都照 `config/region/` 与 `/world/regions` 回包；
    /// 跨区请求只带目标地表编号，相连 / 开放 / 境界门槛三条全由世界服判。
    /// 被拒时屏上写服务端给的 reason 与「仍在原地」，
    /// 不把目标地名换成「已到」的样子。
    /// </summary>
    public static class UiWorldMapScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u4_map_screen=ready";

        /// <summary>建屏。返回建出来的地表卡片个数，自检拿它核对。</summary>
        public static int Build(UiShell shell, UiWorldFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(90f);
            var gap = m.Px(40f);
            var leftW = (m.Width - pad * 2f - gap) * 0.56f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;
            flow.ClearViews();

            shell.Label(m.Band(pad, m.Px(80f), m.Width - pad * 2f, m.Px(60f)),
                "世界地图与跨区", UiTextRole.Title, shell.Size(50f), TextAnchor.MiddleLeft, false);
            shell.Label(m.Band(pad, m.Px(146f), leftW, m.Px(44f)),
                "文件名即地点编号 · 相连对称 · 剧情未开仍归区域表",
                UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, true);

            // ---------------------------------------------------------- 左：八块地表

            var cols = 2;
            var rowH = m.Px(150f);
            var rowGap = m.Px(16f);
            var cellW = (leftW - m.Px(20f)) / cols;
            var gridTop = m.Px(210f);

            var options = new List<UiOption>();
            for (var i = 0; i < UiWorldCodex.Regions.Length; i++)
            {
                var region = UiWorldCodex.Regions[i];
                var col = i % cols;
                var row = i / cols;
                var rect = m.Band(pad + col * (cellW + m.Px(20f)), gridTop + row * (rowH + rowGap), cellW, rowH);
                var sub = region.Realm + (region.Open ? " · 已开" : " · 未开")
                    + " · 连 " + region.Neighbors.Length + " 块";
                var option = new UiOption(
                    shell.Content, rect, region.Id, region.Name, sub,
                    UiOptionKind.Card, shell.Size(32f), shell.Size(22f));
                option.Selected = region.Id == flow.ChosenRegion;
                option.OnPick = picked => flow.ChooseRegion(picked.Id);
                shell.Register(option);
                options.Add(option);
            }

            var noteTop = gridTop + 4 * (rowH + rowGap) + m.Px(12f);
            shell.Label(new Rect(pad, m.Height - noteTop - m.Px(48f), leftW, m.Px(48f)),
                "两块未开的是第 11 场主线开放（旧天城区 / 焦原），本卡不改剧情进度。",
                UiTextRole.Faint, shell.Size(22f), TextAnchor.MiddleLeft, true);

            // ---------------------------------------------------------- 右：跨区

            var boxH = m.Px(340f);
            var target = shell.Panel("跨区目标", m.Band(rightX, gridTop, rightW, boxH), shell.Size(28f));
            var targetText = shell.LabelIn(target.Root,
                new Rect(m.Px(28f), m.Px(190f), rightW - m.Px(56f), m.Px(120f)),
                "还没选地表", UiTextRole.Title, shell.Size(32f), TextAnchor.UpperLeft, true);
            var neighbors = shell.LabelIn(target.Root,
                new Rect(m.Px(28f), m.Px(96f), rightW - m.Px(56f), m.Px(96f)),
                "相邻地表以 /world/regions 回包为准", UiTextRole.Muted, shell.Size(23f),
                TextAnchor.UpperLeft, true);

            var goTop = gridTop + boxH + m.Px(20f);
            var cross = shell.Button(m.Band(rightX, goTop, rightW, m.Px(100f)),
                "跨过去", () => flow.CrossRegion(flow.ChosenRegion), UiButtonKind.Primary);

            var loadTop = goTop + m.Px(100f) + m.Px(16f);
            shell.Button(m.Band(rightX, loadTop, rightW, m.Px(84f)),
                "读一次地点表", () => flow.LoadRegions(), UiButtonKind.Quiet);

            // ---------------------------------------------------------- 右下：规则

            var ruleTop = loadTop + m.Px(84f) + m.Px(20f);
            var ruleH = m.Px(300f);
            var rule = shell.Panel("请求与拒绝顺序", m.Band(rightX, ruleTop, rightW, ruleH), shell.Size(26f));
            shell.LabelIn(rule.Root, new Rect(m.Px(26f), ruleH - m.Px(150f), rightW - m.Px(52f), m.Px(110f)),
                "请求只带 target_region。身份由网关放进 gRPC header；"
                + "客户端带不进境界、位置、身份。",
                UiTextRole.Body, shell.Size(23f), TextAnchor.UpperLeft, true);
            shell.LabelIn(rule.Root, new Rect(m.Px(26f), m.Px(30f), rightW - m.Px(52f), m.Px(120f)),
                UiWorldCodex.RegionRejectOrder,
                UiTextRole.Faint, shell.Size(21f), TextAnchor.UpperLeft, true);

            // ---------------------------------------------------------- 底：回包行

            var lineTop = ruleTop + ruleH + m.Px(18f);
            var line = shell.Label(m.Band(rightX, lineTop, rightW, m.Px(56f)),
                UiWorldCodex.NoRegionYet, UiTextRole.Muted, shell.Size(25f), TextAnchor.MiddleLeft, true);

            flow.RegisterView(() =>
            {
                for (var i = 0; i < options.Count; i++)
                {
                    var selected = options[i].Id == flow.ChosenRegion;
                    if (options[i].Selected != selected)
                    {
                        options[i].Selected = selected;
                        options[i].Apply(shell.Palette);
                    }
                }

                UiWorldCodex.Region region;
                if (UiWorldCodex.FindRegion(flow.ChosenRegion, out region))
                {
                    targetText.Label.text = region.Name + " · " + region.Realm
                        + (region.Open ? " · 已开" : " · 未开");
                    neighbors.Label.text = "相邻：" + string.Join(" / ", region.Neighbors)
                        + "\n" + region.Position;
                }
                else
                {
                    targetText.Label.text = "还没选地表";
                    neighbors.Label.text = "相邻地表以 /world/regions 回包为准";
                }

                cross.Enabled = !flow.Busy && flow.ChosenRegion.Length > 0;
                line.Label.text = flow.RegionLine();
            });

            Debug.Log(ReadyLog + " regions=" + options.Count
                + " reject_steps=" + UiWorldCodex.RegionRejectOrder.Split('→').Length);

            return options.Count;
        }
    }
}
