using Mmo.Net;
using Mmo.Proto.World;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 大世界 HUD（原型 B01）。
    ///
    /// 这一屏是**表世界**的常驻界面，与战斗 HUD（`BattleHud`）是两回事：
    /// 战斗那一屏只在副本里出现，这一屏只在表世界出现，两边不共用控件、
    /// 也不动对方一个矩形。P0-3c 那条启动即进战斗场景的链路原样保留。
    ///
    /// 屏上的数字一个都不算：分到第几线、这线几个人、周围连着哪几块地表，
    /// 全部来自 `/world/enter` 回包；没进分线就写「尚未进分线」。
    /// 同屏三档只翻译服务端下发的 `AppearanceTier`，不按距离自己算档。
    /// </summary>
    public static class UiWorldHudScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u4_hud_screen=ready";

        /// <summary>建屏。返回建出来的功能按钮个数，自检拿它核对。</summary>
        public static int Build(UiShell shell, UiWorldFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(90f);
            var wide = m.Width - pad * 2f;
            flow.ClearViews();

            // ---------------------------------------------------------- 标题

            shell.Label(m.Band(pad, m.Px(80f), wide, m.Px(64f)),
                "表世界", UiTextRole.Title, shell.Size(52f), TextAnchor.MiddleLeft, false);
            var sub = shell.Label(m.Band(pad, m.Px(150f), wide, m.Px(44f)),
                UiWorldCodex.HudLine, UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, true);

            // ---------------------------------------------------------- 左上：角色面板

            var cardW = m.Px(700f);
            var card = shell.Panel("本角色", m.Band(pad, m.Px(230f), cardW, m.Px(580f)), shell.Size(30f));
            var roleText = shell.LabelIn(card.Root,
                new Rect(m.Px(30f), m.Px(390f), cardW - m.Px(60f), m.Px(120f)),
                "体力 / 蓝量 / 等级三条只读角色服回包，本地不估、不回写。",
                UiTextRole.Body, shell.Size(26f), TextAnchor.UpperLeft, true);
            var meterNote = shell.LabelIn(card.Root,
                new Rect(m.Px(30f), m.Px(200f), cardW - m.Px(60f), m.Px(170f)),
                "三条在这里只是名字：体力（红条）、蓝量（蓝条）、等级 x / 100。\n"
                + "没有属性回包之前不写数字，免得把配置里的顶格值当成自己的血量。",
                UiTextRole.Faint, shell.Size(24f), TextAnchor.UpperLeft, true);
            var realmNote = shell.LabelIn(card.Root,
                new Rect(m.Px(30f), m.Px(96f), cardW - m.Px(60f), m.Px(96f)),
                "境界 45 级后开启 · 界面写明还差哪一段",
                UiTextRole.Muted, shell.Size(24f), TextAnchor.UpperLeft, true);

            // ---------------------------------------------------------- 右上：同屏三档

            var tierW = m.Px(760f);
            var tierX = m.Width - pad - tierW;
            var tier = shell.Panel(UiWorldCodex.HudTierNote, m.Band(tierX, m.Px(230f), tierW, m.Px(380f)),
                shell.Size(28f));

            for (var i = 0; i < UiWorldCodex.TierLabels.Length; i++)
            {
                var kind = i == 0 ? UiChipKind.Success : i == 1 ? UiChipKind.Mute : UiChipKind.Mute;
                shell.Chip(tier.Root,
                    new Rect(m.Px(28f) + i * m.Px(240f), m.Px(250f), m.Px(220f), m.Px(64f)),
                    (i + 1) + " " + UiWorldCodex.TierLabels[i], kind);
            }

            // 三档的翻译结果由既有 WorldPeerTiers 算，屏上只把它的字段念出来。
            var tierLines = shell.LabelIn(tier.Root,
                new Rect(m.Px(28f), m.Px(70f), tierW - m.Px(56f), m.Px(170f)),
                TierLine(AppearanceTier.Full) + "\n"
                + TierLine(AppearanceTier.Simple) + "\n"
                + TierLine(AppearanceTier.Name),
                UiTextRole.Body, shell.Size(24f), TextAnchor.UpperLeft, true);

            shell.LabelIn(tier.Root,
                new Rect(m.Px(28f), m.Px(310f), tierW - m.Px(56f), m.Px(56f)),
                "两档距离只在 config/world.json（" + UiWorldCodex.TierNearDistance
                + " / " + UiWorldCodex.TierFarDistance + "），客户端不拿它算档",
                UiTextRole.Faint, shell.Size(22f), TextAnchor.MiddleLeft, true);

            // ---------------------------------------------------------- 右中：八块地表

            var mapTop = m.Px(650f);
            var mapH = m.Px(500f);
            var map = shell.Panel("八块地表 · 无缝相连", m.Band(tierX, mapTop, tierW, mapH), shell.Size(28f));
            var dot = new UiText[UiWorldCodex.Regions.Length];
            for (var i = 0; i < UiWorldCodex.Regions.Length; i++)
            {
                var region = UiWorldCodex.Regions[i];
                var x = m.Px(40f) + (tierW - m.Px(80f)) * (region.MapX / 100f);
                var y = m.Px(70f) + (mapH - m.Px(170f)) * (1f - region.MapY / 100f);
                dot[i] = shell.LabelIn(map.Root,
                    new Rect(x - m.Px(80f), y, m.Px(160f), m.Px(76f)),
                    region.Name + "\n" + region.Realm + (region.Open ? "" : " · 未开"),
                    region.Open ? UiTextRole.Body : UiTextRole.Faint,
                    shell.Size(20f), TextAnchor.MiddleCenter, false);
            }

            // ---------------------------------------------------------- 左下：附近

            var nearTop = m.Height - m.Px(700f);
            var near = shell.Panel("附近", m.Band(pad, nearTop, cardW, m.Px(380f)), shell.Size(28f));
            for (var i = 0; i < UiWorldCodex.HudNearby.Length; i++)
            {
                shell.LabelIn(near.Root,
                    new Rect(m.Px(30f), m.Px(280f) - i * m.Px(76f), cardW - m.Px(60f), m.Px(66f)),
                    "· " + UiWorldCodex.HudNearby[i], UiTextRole.Body, shell.Size(24f),
                    TextAnchor.MiddleLeft, true);
            }

            // ---------------------------------------------------------- 底部：回包行 + 操作签 + 功能条

            var lineTop = m.Height - m.Px(250f);
            var line = shell.Label(m.Band(pad, lineTop, wide, m.Px(56f)),
                UiWorldCodex.NoEnterYet, UiTextRole.Muted, shell.Size(26f), TextAnchor.MiddleLeft, true);

            var actionTop = m.Height - m.Px(180f);
            var actionW = m.Px(300f);
            for (var i = 0; i < UiWorldCodex.HudActions.Length; i++)
            {
                var label = UiWorldCodex.HudActions[i];
                var index = i;
                shell.Button(m.Band(pad + i * (actionW + m.Px(16f)), actionTop, actionW, m.Px(88f)),
                    label, () => OnAction(flow, shell, index), UiButtonKind.Quiet);
            }

            // 右下功能条：只标名字，点了做什么归后续卡。
            var funcW = m.Px(196f);
            var funcGap = m.Px(12f);
            var funcCount = UiWorldCodex.HudFunctions.Length;
            var funcStartX = m.Width - pad - funcCount * funcW - (funcCount - 1) * funcGap;
            for (var i = 0; i < funcCount; i++)
            {
                shell.Button(m.Band(funcStartX + i * (funcW + funcGap), actionTop, funcW, m.Px(88f)),
                    UiWorldCodex.HudFunctions[i],
                    () => shell.Toast("这一格留给后续卡，本卡只画表世界的回包"), UiButtonKind.Quiet);
            }

            flow.RegisterView(() =>
            {
                line.Label.text = flow.EnterLine();
                sub.Label.text = flow.HasEnter ? UiWorldCodex.HudLine : UiWorldCodex.NoEnterYet;
                for (var i = 0; i < dot.Length; i++)
                {
                    dot[i].Label.text = UiWorldCodex.Regions[i].Name + "\n" + UiWorldCodex.Regions[i].Realm
                        + (UiWorldCodex.Regions[i].Open ? "" : " · 未开");
                }
            });

            Debug.Log(ReadyLog + " functions=" + funcCount
                + " actions=" + UiWorldCodex.HudActions.Length
                + " region_dots=" + dot.Length
                + " tiers=" + UiWorldCodex.TierLabels.Length);

            return funcCount;
        }

        /// <summary>三档的翻译结果念出来。字段全部来自 WorldPeerTiers，屏上不重算。</summary>
        public static string TierLine(AppearanceTier tier)
        {
            var visual = WorldPeerTiers.For(tier, string.Empty);
            var what = visual.FullModel ? "完整模型" : visual.SimpleModel ? "简模" : "只留名字";
            return TierNameOf(tier) + " → " + what
                + " · 名字牌" + (visual.NamePlate ? "开" : "关")
                + " · 拖尾光效" + (visual.TrailsAndEffects ? "开" : "关");
        }

        static string TierNameOf(AppearanceTier tier)
        {
            switch (tier)
            {
                case AppearanceTier.Full:
                    return UiWorldCodex.TierLabels[0];
                case AppearanceTier.Simple:
                    return UiWorldCodex.TierLabels[1];
                default:
                    return UiWorldCodex.TierLabels[2];
            }
        }

        // 五个操作签点了去哪：只做已经有的那两屏，其余留给后续卡，
        // 不给一个「看起来能点但什么都不做」的假入口。
        static void OnAction(UiWorldFlow flow, UiShell shell, int index)
        {
            switch (index)
            {
                case 0:
                    shell.ShowPage("采集", s => UiGatherScreen.Build(s, flow));
                    break;
                case 1:
                    shell.ShowPage("对白", s => UiDialogueScreen.Build(s, flow));
                    break;
                case 2:
                    shell.ShowPage("飞行", s => UiFlightScreen.Build(s, flow));
                    break;
                default:
                    shell.Toast("切目标与裂隙入口在别的卡里，这一格先只标名字");
                    break;
            }
        }
    }
}
