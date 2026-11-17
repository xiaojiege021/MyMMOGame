#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using Mmo.Net;
using Mmo.Proto.World;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U4 五屏自检。`HotUpdate` 是 `autoReferenced: false`，`Assets/Editor` 引不到这里，
    /// 所以实例断言放在本程序集，由编辑器入口反射调 <see cref="Run"/>。
    ///
    /// 验的正对第 14 节第 74 行的三条完成标准与 14.1 的口径：
    /// 1. 同屏三档只按服务端下发的 `AppearanceTier` 翻译，屏上没有按距离算档的路径；
    /// 2. 飞行没权限被拉回时，画面高度改成回包给的 y，拖尾亮不起来；
    /// 3. 采集与跨区只展示回包，没回包就写「尚未…」，不拿配置顶格值假装；
    /// 4. 装备、坐骑、拖尾都打不开飞行，只有境界能；
    /// 5. 切皮肤只换颜色，文案一字不差。
    ///
    /// 回包靠替换发送实现喂进去，不连网关：这一层验的是「回包 → 屏上说什么」，
    /// 真网关那一段由 `tools/check/check-u4.sh` 用同一个 `WorldEntry` 验。
    /// </summary>
    public static class UiWorldSelfCheck
    {
        static bool _failed;

        static string _lastMethod = string.Empty;

        static string _lastPath = string.Empty;

        static string _lastBody = string.Empty;

        static int _nextStatus;

        static string _nextBody = string.Empty;

        /// <summary>跑一遍。返回是否全过。</summary>
        public static bool Run()
        {
            _failed = false;

            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            var flow = UiWorldFlow.Ensure(shell);
            flow.SetSender((method, path, body, token, done) =>
            {
                _lastMethod = method;
                _lastPath = path;
                _lastBody = body;
                done(_nextStatus, _nextBody);
            });
            flow.Configure("token-u4-self", 77);

            try
            {
                CheckTiers();
                CheckEnter(flow);

                var hudButtons = 0;
                shell.ShowPage("表世界", s => { hudButtons = UiWorldHudScreen.Build(s, flow); });
                CheckHud(hudButtons, flow);

                var mapCards = 0;
                shell.ShowPage("世界地图", s => { mapCards = UiWorldMapScreen.Build(s, flow); });
                CheckMap(mapCards, flow);

                var speakers = 0;
                shell.ShowPage("对白", s => { speakers = UiDialogueScreen.Build(s, flow); });
                CheckDialogue(speakers);

                UiGatherScreen.ResetGatherWindows();
                FeedGatherNodes(flow);
                var ore = 0;
                shell.ShowPage("采集", s => { ore = UiGatherScreen.Build(s, flow, "矿脉"); });
                var herb = 0;
                shell.ShowPage("采集", s => { herb = UiGatherScreen.Build(s, flow, "药草"); });
                var spirit = 0;
                shell.ShowPage("采集", s => { spirit = UiGatherScreen.Build(s, flow, "灵石矿脉"); });
                CheckGather(ore, herb, spirit, flow);

                var regions = 0;
                shell.ShowPage("飞行", s => { regions = UiFlightScreen.Build(s, flow); });
                CheckFly(regions, flow);

                CheckSkinKeepsCopy(shell, flow);
            }
            catch (Exception error)
            {
                Report("exception", false);
                Debug.LogError("u4_self_stack " + error);
                _failed = true;
            }

            UnityEngine.Object.DestroyImmediate(flow);
            if (shell != null)
            {
                shell.ClosePage();
                UnityEngine.Object.DestroyImmediate(shell.gameObject);
            }

            UiSkinStore.Reset();
            Debug.Log("u4_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        // ------------------------------------------------------------------ 档位

        static void CheckTiers()
        {
            // 三档的翻译结果必须与 WorldPeerTiers 的字段一致：档位说什么就画什么。
            var full = WorldPeerTiers.For(AppearanceTier.Full, "甲");
            var simple = WorldPeerTiers.For(AppearanceTier.Simple, "乙");
            var name = WorldPeerTiers.For(AppearanceTier.Name, "丙");
            var unknown = WorldPeerTiers.For(AppearanceTier.Unspecified, "丁");

            Report("tier_full", full.FullModel && !full.SimpleModel && full.NamePlate && !full.TrailsAndEffects);
            Report("tier_simple", !simple.FullModel && simple.SimpleModel && simple.NamePlate && !simple.TrailsAndEffects);
            Report("tier_name", !name.FullModel && !name.SimpleModel && name.NamePlate && !name.TrailsAndEffects);

            // 未识别的档位按最省的一档处理，绝不擅自画全。
            Report("tier_unknown_cheapest",
                !unknown.FullModel && !unknown.SimpleModel && unknown.NamePlate && !unknown.TrailsAndEffects);

            // 名字牌文本只认服务端给的那一份。
            Report("tier_name_from_reply", full.DisplayName == "甲" && name.DisplayName == "丙");

            // 屏上那三行与翻译结果同源。
            Report("tier_panel_text",
                UiWorldHudScreen.TierLine(AppearanceTier.Full).Contains("完整模型")
                && UiWorldHudScreen.TierLine(AppearanceTier.Simple).Contains("简模")
                && UiWorldHudScreen.TierLine(AppearanceTier.Name).Contains("只留名字"));
        }

        // ------------------------------------------------------------------ 进分线

        static void CheckEnter(UiWorldFlow flow)
        {
            Report("enter_initial", !flow.HasEnter && flow.EnterLine().Contains("尚未进分线"));

            _nextStatus = 200;
            _nextBody = "{\"entered\":true,\"reason\":\"\",\"line_id\":\"line-1\",\"region_id\":\"枯岚道\","
                + "\"line_no\":1,\"line_size\":2,\"line_capacity\":4,\"duplicate\":false,"
                + "\"neighbors\":[\"承露门\"],\"dialogue_ids\":[\"乔小满\"]}";
            flow.SubmitEnter("枯岚道");

            var ok = _lastPath == "/world/enter"
                && _lastBody.Contains("\"character_id\":77")
                && _lastBody.Contains("\"region_id\":\"枯岚道\"")
                && !_lastBody.Contains("display_name")
                && !_lastBody.Contains("line_id");
            Report("enter_body", ok);
            Report("enter_ok_line",
                flow.HasEnter && flow.Enter.Entered
                && flow.RegionId == "枯岚道"
                && flow.EnterLine().Contains("第 1 线")
                && flow.Enter.Neighbors.Length == 1
                && flow.Enter.Neighbors[0] == "承露门");
            Report("enter_ok_line_text", flow.EnterLine().Contains("2 / 4"));

            // 被拒：世界服给的是原来那块地表，界面不许把目标地名写成已到。
            _nextStatus = 200;
            _nextBody = "{\"entered\":false,\"reason\":\"line_full\",\"line_id\":\"line-9\","
                + "\"region_id\":\"枯岚道\",\"line_no\":1,\"line_size\":4,\"line_capacity\":4,"
                + "\"duplicate\":false,\"neighbors\":[],\"dialogue_ids\":[]}";
            flow.SubmitEnter("栖云城");
            Report("enter_refused",
                flow.Enter.Kind == WorldEnterKind.Refused
                && flow.RegionId == "枯岚道"
                && flow.EnterLine().Contains("没进去"));

            _nextStatus = 0;
            _nextBody = string.Empty;
            flow.SubmitEnter("枯岚道");
            Report("enter_network", flow.Enter.Kind == WorldEnterKind.Network
                && flow.EnterLine().Contains("连不上网关"));

            _nextStatus = 401;
            _nextBody = "{\"error\":\"invalid_token\"}";
            flow.SubmitEnter("枯岚道");
            Report("enter_401", flow.Enter.Kind == WorldEnterKind.InvalidToken);

            // 网关把中文写成 \u67AF\u5C9A\u9053 这种形态发出来，拆装要解开，
            // 否则屏上显示成一串十六进制，看着像地名没进来。
            _nextStatus = 200;
            _nextBody = "{\"entered\":true,\"reason\":\"\",\"line_id\":\"\\u67AF\\u5C9A\\u9053#1\","
                + "\"region_id\":\"\\u67AF\\u5C9A\\u9053\",\"line_no\":1,\"line_size\":1,"
                + "\"line_capacity\":4,\"duplicate\":false,"
                + "\"neighbors\":[\"\\u627F\\u9732\\u95E8\"],\"dialogue_ids\":[]}";
            flow.SubmitEnter("枯岚道");
            Report("enter_unescape",
                flow.RegionId == "枯岚道"
                && flow.Enter.LineId == "枯岚道#1"
                && flow.Enter.Neighbors.Length == 1
                && flow.Enter.Neighbors[0] == "承露门");
        }

        // ------------------------------------------------------------------ B01

        static void CheckHud(int buttons, UiWorldFlow flow)
        {
            Report("hud_built", buttons == UiWorldCodex.HudFunctions.Length && buttons == 7);

            // 建屏本身不发请求：屏只是把回包念出来，不主动去要数据。
            var before = flow.Requests;
            UiShell.Ensure().ShowPage("表世界", s => UiWorldHudScreen.Build(s, flow));
            Report("hud_build_no_request", flow.Requests == before);

            // 地貌点八块，名字与展示表一致。
            Report("hud_regions", UiWorldCodex.Regions.Length == 8);

            // 三档说明照旧在屏上（由 TierLine 拼，前面已验）。
            Report("hud_tier_panel", UiWorldHudScreen.TierLine(AppearanceTier.Simple).Contains("简模"));
        }

        // ------------------------------------------------------------------ B02

        static void CheckMap(int cards, UiWorldFlow flow)
        {
            Report("map_cards", cards == 8 && UiWorldCodex.Regions.Length == 8);

            // 只换选中态不发请求。
            var before = flow.Requests;
            flow.ChooseRegion("承露门");
            Report("map_choose_no_request", flow.Requests == before && flow.ChosenRegion == "承露门");
            Report("map_initial", flow.RegionLine().Contains("尚未跨区") || flow.HasRegion);

            flow.ClearViews();
            UiShell.Ensure().ShowPage("世界地图", s => UiWorldMapScreen.Build(s, flow));

            _nextStatus = 200;
            _nextBody = "{\"ok\":true,\"reason\":\"\",\"region_id\":\"承露门\",\"line_id\":\"line-2\","
                + "\"line_no\":3,\"realm\":\"炼体\",\"required_realm\":\"炼体\"}";
            flow.CrossRegion("承露门");
            Report("map_cross_body", _lastPath == "/world/region"
                && _lastBody == "{\"target_region\":\"承露门\"}");
            Report("map_cross_ok", flow.RegionReply.Ok
                && flow.RegionId == "承露门"
                && flow.RegionLine().Contains("已到 承露门"));

            _nextStatus = 200;
            _nextBody = "{\"ok\":false,\"reason\":\"region_realm_locked\",\"region_id\":\"旧天城区\","
                + "\"line_id\":\"\",\"line_no\":0,\"realm\":\"炼体\",\"required_realm\":\"结丹\"}";
            flow.CrossRegion("旧天城区");
            var line = flow.RegionLine();
            Report("map_cross_refused",
                !flow.RegionReply.Ok
                && line.Contains("没跨成")
                && line.Contains("境界不够")
                && line.Contains("仍在 承露门"));

            _nextStatus = 0;
            _nextBody = string.Empty;
            flow.CrossRegion("栖云城");
            Report("map_cross_network",
                !flow.RegionReply.Ok
                && flow.RegionLine().Contains("连不上网关")
                && flow.RegionId == "承露门");

            CheckRegionOpen(flow);
        }

        // ------------------------------------------------------------------ N14 地点表的开放

        static void CheckRegionOpen(UiWorldFlow flow)
        {
            // 还没读过地点表：屏上退回配置静态，状态取不到回包。
            Report("regions_before_reply", !flow.TryRegionState("焦原", out _, out _));

            _nextStatus = 200;
            _nextBody = RegionsJson;
            flow.LoadRegions();

            // 带角色号去读：进度与路线都在服务端，客户端只带角色号。
            Report("regions_path",
                _lastPath.IndexOf("character_id=77", StringComparison.Ordinal) >= 0);

            // 静态 open=false 的旧天城区，回包说开就是开——屏上不再按配置标死。
            UiWorldCodex.Region codexOld;
            var staticLocked = UiWorldCodex.FindRegion("旧天城区", out codexOld) && !codexOld.Open;
            Report("regions_from_reply",
                staticLocked
                && flow.TryRegionState("旧天城区", out var oldOpen, out var oldText)
                && oldOpen && oldText == "已开");

            // 路线不匹配是「这条路不开」，不是「还没到」，两种回包写两种字。
            Report("regions_route_mismatch",
                flow.TryRegionState("焦原", out var jyOpen, out var jyText)
                && !jyOpen && jyText == "这条路不开");
            Report("regions_locked_stage",
                WorldEntry.RegionStateText(new WorldRegionRow
                {
                    Open = false,
                    Reason = "region_locked",
                    UnlockStage = 11,
                }) == "第 11 场才开");

            // 屏上：卡片小字与底部说明都跟着回包改，写死第 11 场那句不再出现。
            flow.ClearViews();
            var shell = UiShell.Ensure();
            shell.ShowPage("世界地图", s => UiWorldMapScreen.Build(s, flow));
            var copy = Text(shell);
            Report("regions_screen_from_reply",
                copy.Contains("旧天城区") && copy.Contains("已开")
                && copy.Contains("这条路不开")
                && copy.Contains("开放以 /world/regions 回包为准")
                && !copy.Contains("未开的是第 11 场主线开放")
                && !copy.Contains("（配置）"));
        }

        static string Text(UiShell shell)
        {
            var builder = new StringBuilder();
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                builder.Append(labels[i].text).Append(';');
            }

            return builder.ToString();
        }

        // ------------------------------------------------------------------ B03

        static void CheckDialogue(int speakers)
        {
            Report("dialogue_speakers", speakers == 9 && UiWorldCodex.Speakers.Length == 9);

            // 每一条定词都来自生成的配置表（config/dialogue），本屏不再有写死的文本；
            // 这里只核对「生成表 → 展示表」没有丢行、没有改写。
            var qiao = UiWorldCodex.Speakers[0];
            Report("dialogue_qiao",
                qiao.Name == "乔小满"
                && qiao.Lines != null && qiao.Lines.Length == 2
                && qiao.Text == qiao.Lines[0].Text
                && qiao.Limit == qiao.Lines[0].Limit
                && qiao.SecondText == qiao.Lines[1].Text
                && qiao.SecondLimit == qiao.Lines[1].Limit
                && qiao.LineCount == 2);

            var multi = 0;
            var withSecond = 0;
            var generated = 0;
            var linesOk = UiContentTable.SpeakerDialogue.Length == UiWorldCodex.Speakers.Length;
            for (var i = 0; i < UiWorldCodex.Speakers.Length; i++)
            {
                var speaker = UiWorldCodex.Speakers[i];
                linesOk &= speaker.Lines != null && speaker.Lines.Length > 0
                    && speaker.LineCount == speaker.Lines.Length;
                if (speaker.LineCount > 1)
                {
                    multi++;
                }

                if (!string.IsNullOrEmpty(speaker.SecondText))
                {
                    withSecond++;
                }

                if (i < UiContentTable.SpeakerDialogue.Length)
                {
                    generated += UiContentTable.SpeakerDialogue[i].Length;
                    linesOk &= speaker.Lines.Length == UiContentTable.SpeakerDialogue[i].Length;
                }

                for (var j = 0; j < speaker.Lines.Length; j++)
                {
                    linesOk &= speaker.Lines[j].Chapter > 0
                        && !string.IsNullOrEmpty(speaker.Lines[j].Text);
                }
            }

            Report("dialogue_lines", linesOk && generated == UiWorldCodex.TotalDialogueLines);

            // 配置里有三位不止一场（乔小满 2、苏晚织 2、阮青 4）；
            // 多场的都把下一条从配置里带出来，单场的如实写「没有第 2 场」。
            Report("dialogue_second_chapter", multi == 3 && withSecond == 3);
        }

        // ------------------------------------------------------------------ B04

        static void FeedGatherNodes(UiWorldFlow flow)
        {
            _nextStatus = 200;
            _nextBody = MockGatherNodesJson();
            flow.LoadGatherNodes();
        }

        static string MockGatherNodesJson()
        {
            var items = new List<string>();
            for (var i = 1; i <= 9; i++)
            {
                items.Add("{\"node_id\":\"ore_mock_" + i + "\",\"kind\":\"ore\",\"region_id\":\"枯岚道\","
                    + "\"item_id\":\"ore_iron\",\"count\":2,\"bag_slot\":1,\"respawn_s\":2}");
            }

            for (var i = 1; i <= 8; i++)
            {
                items.Add("{\"node_id\":\"herb_mock_" + i + "\",\"kind\":\"herb\",\"region_id\":\"听谣镇\","
                    + "\"item_id\":\"herb_ling\",\"count\":3,\"bag_slot\":2,\"respawn_s\":2}");
            }

            items.Add("{\"node_id\":\"spirit_qiyun_1\",\"kind\":\"spirit_stone\",\"region_id\":\"栖云城\","
                + "\"item_id\":\"\",\"count\":0,\"bag_slot\":0,\"respawn_s\":0}");
            return "{\"count\":18,\"nodes\":[" + string.Join(",", items.ToArray()) + "]}";
        }

        static void CheckGather(int ore, int herb, int spirit, UiWorldFlow flow)
        {
            Report("gather_groups", ore == 9 && herb == 8 && spirit == 1);
            Report("gather_nodes_path", _lastPath == "/world/gather/nodes" && _lastMethod == "GET");

            GatherNodeRow stone = default;
            var found = false;
            if (flow.GatherNodesReply.Nodes != null)
            {
                for (var i = 0; i < flow.GatherNodesReply.Nodes.Length; i++)
                {
                    if (flow.GatherNodesReply.Nodes[i].NodeId == "spirit_qiyun_1")
                    {
                        stone = flow.GatherNodesReply.Nodes[i];
                        found = true;
                        break;
                    }
                }
            }

            Report("gather_spirit_forbidden",
                found && stone.Forbidden && stone.ItemId.Length == 0 && stone.Count == 0 && stone.RespawnSeconds == 0);

            flow.ClearViews();
            UiShell.Ensure().ShowPage("采集", s => UiGatherScreen.Build(s, flow, "矿脉"));
            Report("gather_initial", flow.GatherLine().Contains("尚未采集"));

            _nextStatus = 200;
            _nextBody = "{\"ok\":true,\"reason\":\"\",\"node_id\":\"ore_kulan_1\",\"kind\":\"ore\","
                + "\"item_id\":\"ore_iron\",\"count\":2,\"bag_slot\":1,\"txn_id\":\"t-1\","
                + "\"duplicate\":false,\"respawn_s\":2}";
            flow.Gather("ore_kulan_1");
            Report("gather_body", _lastPath == "/world/gather"
                && _lastBody == "{\"node_id\":\"ore_kulan_1\"}");
            Report("gather_ok",
                flow.GatherReply.Ok
                && flow.GatherLine().Contains("ore_iron ×2")
                && flow.GatherLine().Contains("入格位 1"));

            _nextStatus = 200;
            _nextBody = "{\"ok\":false,\"reason\":\"spirit_stone_forbidden\",\"node_id\":\"spirit_qiyun_1\","
                + "\"kind\":\"spirit_stone\",\"item_id\":\"\",\"count\":0,\"bag_slot\":0,"
                + "\"txn_id\":\"\",\"duplicate\":false,\"respawn_s\":0}";
            flow.Gather("spirit_qiyun_1");
            Report("gather_refused",
                !flow.GatherReply.Ok && flow.GatherLine().Contains("灵石矿脉不开放个人采集"));
        }

        // ------------------------------------------------------------------ B05

        static void CheckFly(int regions, UiWorldFlow flow)
        {
            Report("fly_regions", regions == 8 && UiWorldCodex.Realms.Length == 3);
            Report("fly_initial", !flow.HasFly && flow.FlyLine().Contains("尚未起飞"));

            // 没权限：回包的 y 是拉回后的地面高度，画面高度跟它走。
            _nextStatus = 200;
            _nextBody = "{\"ok\":false,\"reason\":\"flight_forbidden\",\"flying\":false,"
                + "\"can_fly\":false,\"realm\":\"炼体\",\"required_realm\":\"筑基\",\"y\":0}";
            flow.Fly(true);
            Report("fly_body", _lastPath == "/world/fly" && _lastBody == "{\"want_fly\":true}");
            Report("fly_denied_pullback",
                !flow.FlyReply.Ok
                && flow.ScreenY == 0f
                && !flow.TrailAllowed
                && flow.FlyLine().Contains("已回到地面高度")
                && flow.FlyLine().Contains("筑基"));

            // 起飞成功：高度取回包的 y，拖尾跟着 flying 走。
            _nextStatus = 200;
            _nextBody = "{\"ok\":true,\"reason\":\"\",\"flying\":true,\"can_fly\":true,"
                + "\"realm\":\"筑基\",\"required_realm\":\"筑基\",\"y\":12.5}";
            flow.Fly(true);
            Report("fly_ok", flow.FlyReply.Ok
                && Mathf.Abs(flow.ScreenY - 12.5f) < 0.001f
                && flow.TrailAllowed
                && flow.FlyLine().Contains("已起飞"));

            // 落地：flying=false 时拖尾立刻关。
            _nextStatus = 200;
            _nextBody = "{\"ok\":true,\"reason\":\"\",\"flying\":false,\"can_fly\":true,"
                + "\"realm\":\"筑基\",\"required_realm\":\"筑基\",\"y\":0}";
            flow.Fly(false);
            Report("fly_landed", flow.FlyReply.Ok && !flow.TrailAllowed
                && Mathf.Abs(flow.ScreenY) < 0.001f);

            // 装备 / 坐骑 / 拖尾都打不开飞行：高度与拖尾都是只读属性，
            // 唯一能改它们的是回包，界面没有第二个写入口。
            Report("fly_only_by_realm",
                !typeof(UiWorldFlow).GetProperty("TrailAllowed").CanWrite
                && !typeof(UiWorldFlow).GetProperty("ScreenY").CanWrite);
        }

        // ------------------------------------------------------------------ 皮肤

        static void CheckSkinKeepsCopy(UiShell shell, UiWorldFlow flow)
        {
            flow.ClearViews();
            shell.ShowPage("表世界", s => UiWorldHudScreen.Build(s, flow));
            var before = flow.EnterLine();
            shell.SetSkin(UiSkin.Dark, false);
            var after = flow.EnterLine();
            Report("skin_keeps_copy", before == after);
            shell.SetSkin(UiSkin.Light, false);
        }

        // ------------------------------------------------------------------ 断言

        /// <summary>
        /// N14 的地点表回包，八块都给：旧天城区静态 open=false，回包按主线进度说开；
        /// 焦原是路线不匹配（立仙的人走不到）；其余照旧开着。
        /// </summary>
        const string RegionsJson =
            "{\"version\":1,\"character_id\":77,\"regions\":[" +
            "{\"id\":\"枯岚道\",\"name\":\"枯岚道\",\"chapters\":[1],\"neighbors\":[\"承露门\"]," +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":0,\"main_stage\":0}," +
            "{\"id\":\"承露门\",\"name\":\"承露门\",\"chapters\":[1],\"neighbors\":[\"枯岚道\"]," +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":0,\"main_stage\":0}," +
            "{\"id\":\"听谣镇\",\"name\":\"听谣镇\",\"chapters\":[2],\"neighbors\":[\"承露门\"]," +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":0,\"main_stage\":0}," +
            "{\"id\":\"外环隙\",\"name\":\"外环隙\",\"chapters\":[3],\"neighbors\":[\"听谣镇\"]," +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":0,\"main_stage\":0}," +
            "{\"id\":\"栖云城\",\"name\":\"栖云城\",\"chapters\":[5],\"neighbors\":[\"外环隙\"]," +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":0,\"main_stage\":0}," +
            "{\"id\":\"旧天城区\",\"name\":\"旧天城区\",\"chapters\":[11],\"neighbors\":[\"栖云城\"]," +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":11,\"main_stage\":11}," +
            "{\"id\":\"焦原\",\"name\":\"焦原\",\"chapters\":[11],\"neighbors\":[\"栖云城\"]," +
            "\"dialogue_ids\":[],\"open\":false,\"reason\":\"region_route_mismatch\"," +
            "\"unlock_stage\":11,\"main_stage\":11}," +
            "{\"id\":\"龙巢门口\",\"name\":\"龙巢门口\",\"chapters\":[9],\"neighbors\":[\"栖云城\"]," +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":0,\"main_stage\":0}]," +
            "\"speakers\":[],\"factions\":[]}";

        static void Report(string name, bool ok)
        {
            if (!ok)
            {
                _failed = true;
            }

            Debug.Log("u4_self_" + name + "=" + (ok ? "ok" : "fail"));
        }
    }
}
#endif
