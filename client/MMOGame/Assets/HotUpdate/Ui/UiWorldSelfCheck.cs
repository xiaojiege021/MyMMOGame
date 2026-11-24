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
                shell.ShowPage(UiStrings.UiWorldSelfCheck.T001, s => { hudButtons = UiWorldHudScreen.Build(s, flow); });
                CheckHud(hudButtons, flow);

                var mapCards = 0;
                shell.ShowPage(UiStrings.UiWorldSelfCheck.T002, s => { mapCards = UiWorldMapScreen.Build(s, flow); });
                CheckMap(mapCards, flow);

                var speakers = 0;
                shell.ShowPage(UiStrings.UiWorldSelfCheck.T003, s => { speakers = UiDialogueScreen.Build(s, flow); });
                CheckDialogue(speakers);

                UiGatherScreen.ResetGatherWindows();
                FeedGatherNodes(flow);
                var ore = 0;
                shell.ShowPage(UiStrings.UiWorldSelfCheck.T004, s => { ore = UiGatherScreen.Build(s, flow, UiStrings.UiWorldSelfCheck.T005); });
                var herb = 0;
                shell.ShowPage(UiStrings.UiWorldSelfCheck.T004, s => { herb = UiGatherScreen.Build(s, flow, UiStrings.UiWorldSelfCheck.T006); });
                var spirit = 0;
                shell.ShowPage(UiStrings.UiWorldSelfCheck.T004, s => { spirit = UiGatherScreen.Build(s, flow, UiStrings.UiWorldSelfCheck.T007); });
                CheckGather(ore, herb, spirit, flow);

                var regions = 0;
                shell.ShowPage(UiStrings.UiWorldSelfCheck.T008, s => { regions = UiFlightScreen.Build(s, flow); });
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
            var full = WorldPeerTiers.For(AppearanceTier.Full, UiStrings.UiWorldSelfCheck.T009);
            var simple = WorldPeerTiers.For(AppearanceTier.Simple, UiStrings.UiWorldSelfCheck.T010);
            var name = WorldPeerTiers.For(AppearanceTier.Name, UiStrings.UiWorldSelfCheck.T011);
            var unknown = WorldPeerTiers.For(AppearanceTier.Unspecified, UiStrings.UiWorldSelfCheck.T012);

            Report("tier_full", full.FullModel && !full.SimpleModel && full.NamePlate && !full.TrailsAndEffects);
            Report("tier_simple", !simple.FullModel && simple.SimpleModel && simple.NamePlate && !simple.TrailsAndEffects);
            Report("tier_name", !name.FullModel && !name.SimpleModel && name.NamePlate && !name.TrailsAndEffects);

            // 未识别的档位按最省的一档处理，绝不擅自画全。
            Report("tier_unknown_cheapest",
                !unknown.FullModel && !unknown.SimpleModel && unknown.NamePlate && !unknown.TrailsAndEffects);

            // 名字牌文本只认服务端给的那一份。
            Report("tier_name_from_reply", full.DisplayName == UiStrings.UiWorldSelfCheck.T009 && name.DisplayName == UiStrings.UiWorldSelfCheck.T011);

            // 屏上那三行与翻译结果同源。
            Report("tier_panel_text",
                UiWorldHudScreen.TierLine(AppearanceTier.Full).Contains(UiStrings.UiWorldSelfCheck.T013)
                && UiWorldHudScreen.TierLine(AppearanceTier.Simple).Contains(UiStrings.UiWorldSelfCheck.T014)
                && UiWorldHudScreen.TierLine(AppearanceTier.Name).Contains(UiStrings.UiWorldSelfCheck.T015));
        }

        // ------------------------------------------------------------------ 进分线

        static void CheckEnter(UiWorldFlow flow)
        {
            Report("enter_initial", !flow.HasEnter && flow.EnterLine().Contains(UiStrings.UiWorldSelfCheck.T016));

            _nextStatus = 200;
            _nextBody = UiStrings.UiWorldSelfCheck.T017
                + "\"line_no\":1,\"line_size\":2,\"line_capacity\":4,\"duplicate\":false,"
                + UiStrings.UiWorldSelfCheck.T018;
            flow.SubmitEnter(UiStrings.UiWorldSelfCheck.T019);

            var ok = _lastPath == "/world/enter"
                && _lastBody.Contains("\"character_id\":77")
                && _lastBody.Contains(UiStrings.UiWorldSelfCheck.T020)
                && !_lastBody.Contains("display_name")
                && !_lastBody.Contains("line_id");
            Report("enter_body", ok);
            Report("enter_ok_line",
                flow.HasEnter && flow.Enter.Entered
                && flow.RegionId == UiStrings.UiWorldSelfCheck.T019
                && flow.EnterLine().Contains(UiStrings.UiWorldSelfCheck.T021)
                && flow.Enter.Neighbors.Length == 1
                && flow.Enter.Neighbors[0] == UiStrings.UiWorldSelfCheck.T022);
            Report("enter_ok_line_text", flow.EnterLine().Contains("2 / 4"));

            // 被拒：世界服给的是原来那块地表，界面不许把目标地名写成已到。
            _nextStatus = 200;
            _nextBody = "{\"entered\":false,\"reason\":\"line_full\",\"line_id\":\"line-9\","
                + UiStrings.UiWorldSelfCheck.T023
                + "\"duplicate\":false,\"neighbors\":[],\"dialogue_ids\":[]}";
            flow.SubmitEnter(UiStrings.UiWorldSelfCheck.T024);
            Report("enter_refused",
                flow.Enter.Kind == WorldEnterKind.Refused
                && flow.RegionId == UiStrings.UiWorldSelfCheck.T019
                && flow.EnterLine().Contains(UiStrings.UiWorldSelfCheck.T025));

            _nextStatus = 0;
            _nextBody = string.Empty;
            flow.SubmitEnter(UiStrings.UiWorldSelfCheck.T019);
            Report("enter_network", flow.Enter.Kind == WorldEnterKind.Network
                && flow.EnterLine().Contains(UiStrings.UiWorldSelfCheck.T026));

            _nextStatus = 401;
            _nextBody = "{\"error\":\"invalid_token\"}";
            flow.SubmitEnter(UiStrings.UiWorldSelfCheck.T019);
            Report("enter_401", flow.Enter.Kind == WorldEnterKind.InvalidToken);

            // 网关把中文写成 \u67AF\u5C9A\u9053 这种形态发出来，拆装要解开，
            // 否则屏上显示成一串十六进制，看着像地名没进来。
            _nextStatus = 200;
            _nextBody = "{\"entered\":true,\"reason\":\"\",\"line_id\":\"\\u67AF\\u5C9A\\u9053#1\","
                + "\"region_id\":\"\\u67AF\\u5C9A\\u9053\",\"line_no\":1,\"line_size\":1,"
                + "\"line_capacity\":4,\"duplicate\":false,"
                + "\"neighbors\":[\"\\u627F\\u9732\\u95E8\"],\"dialogue_ids\":[]}";
            flow.SubmitEnter(UiStrings.UiWorldSelfCheck.T019);
            Report("enter_unescape",
                flow.RegionId == UiStrings.UiWorldSelfCheck.T019
                && flow.Enter.LineId == UiStrings.UiWorldSelfCheck.T027
                && flow.Enter.Neighbors.Length == 1
                && flow.Enter.Neighbors[0] == UiStrings.UiWorldSelfCheck.T022);
        }

        // ------------------------------------------------------------------ B01

        static void CheckHud(int buttons, UiWorldFlow flow)
        {
            Report("hud_built", buttons == UiWorldCodex.HudFunctions.Length && buttons == 7);

            // 建屏本身不发请求：屏只是把回包念出来，不主动去要数据。
            var before = flow.Requests;
            UiShell.Ensure().ShowPage(UiStrings.UiWorldSelfCheck.T001, s => UiWorldHudScreen.Build(s, flow));
            Report("hud_build_no_request", flow.Requests == before);

            // 地貌点八块，名字与展示表一致。
            Report("hud_regions", UiWorldCodex.Regions.Length == 8);

            // 三档说明照旧在屏上（由 TierLine 拼，前面已验）。
            Report("hud_tier_panel", UiWorldHudScreen.TierLine(AppearanceTier.Simple).Contains(UiStrings.UiWorldSelfCheck.T014));
        }

        // ------------------------------------------------------------------ B02

        static void CheckMap(int cards, UiWorldFlow flow)
        {
            Report("map_cards", cards == 8 && UiWorldCodex.Regions.Length == 8);

            // 只换选中态不发请求。
            var before = flow.Requests;
            flow.ChooseRegion(UiStrings.UiWorldSelfCheck.T022);
            Report("map_choose_no_request", flow.Requests == before && flow.ChosenRegion == UiStrings.UiWorldSelfCheck.T022);
            Report("map_initial", flow.RegionLine().Contains(UiStrings.UiWorldSelfCheck.T028) || flow.HasRegion);

            flow.ClearViews();
            UiShell.Ensure().ShowPage(UiStrings.UiWorldSelfCheck.T002, s => UiWorldMapScreen.Build(s, flow));

            _nextStatus = 200;
            _nextBody = UiStrings.UiWorldSelfCheck.T029
                + UiStrings.UiWorldSelfCheck.T030;
            flow.CrossRegion(UiStrings.UiWorldSelfCheck.T022);
            Report("map_cross_body", _lastPath == "/world/region"
                && _lastBody == UiStrings.UiWorldSelfCheck.T031);
            Report("map_cross_ok", flow.RegionReply.Ok
                && flow.RegionId == UiStrings.UiWorldSelfCheck.T022
                && flow.RegionLine().Contains(UiStrings.UiWorldSelfCheck.T032));

            _nextStatus = 200;
            _nextBody = UiStrings.UiWorldSelfCheck.T033
                + UiStrings.UiWorldSelfCheck.T034;
            flow.CrossRegion(UiStrings.UiWorldSelfCheck.T035);
            var line = flow.RegionLine();
            Report("map_cross_refused",
                !flow.RegionReply.Ok
                && line.Contains(UiStrings.UiWorldSelfCheck.T036)
                && line.Contains(UiStrings.UiWorldSelfCheck.T037)
                && line.Contains(UiStrings.UiWorldSelfCheck.T038));

            _nextStatus = 0;
            _nextBody = string.Empty;
            flow.CrossRegion(UiStrings.UiWorldSelfCheck.T024);
            Report("map_cross_network",
                !flow.RegionReply.Ok
                && flow.RegionLine().Contains(UiStrings.UiWorldSelfCheck.T026)
                && flow.RegionId == UiStrings.UiWorldSelfCheck.T022);

            CheckRegionOpen(flow);
        }

        // ------------------------------------------------------------------ N14 地点表的开放

        static void CheckRegionOpen(UiWorldFlow flow)
        {
            // 还没读过地点表：屏上退回配置静态，状态取不到回包。
            Report("regions_before_reply", !flow.TryRegionState(UiStrings.UiWorldSelfCheck.T039, out _, out _));

            _nextStatus = 200;
            _nextBody = RegionsJson;
            flow.LoadRegions();

            // 带角色号去读：进度与路线都在服务端，客户端只带角色号。
            Report("regions_path",
                _lastPath.IndexOf("character_id=77", StringComparison.Ordinal) >= 0);

            // 静态 open=false 的旧天城区，回包说开就是开——屏上不再按配置标死。
            UiWorldCodex.Region codexOld;
            var staticLocked = UiWorldCodex.FindRegion(UiStrings.UiWorldSelfCheck.T035, out codexOld) && !codexOld.Open;
            Report("regions_from_reply",
                staticLocked
                && flow.TryRegionState(UiStrings.UiWorldSelfCheck.T035, out var oldOpen, out var oldText)
                && oldOpen && oldText == UiStrings.UiWorldSelfCheck.T040);

            // 路线不匹配是「这条路不开」，不是「还没到」，两种回包写两种字。
            Report("regions_route_mismatch",
                flow.TryRegionState(UiStrings.UiWorldSelfCheck.T039, out var jyOpen, out var jyText)
                && !jyOpen && jyText == UiStrings.UiWorldSelfCheck.T041);
            Report("regions_locked_stage",
                WorldEntry.RegionStateText(new WorldRegionRow
                {
                    Open = false,
                    Reason = "region_locked",
                    UnlockStage = 11,
                }) == UiStrings.UiWorldSelfCheck.T042);

            // 屏上：卡片小字与底部说明都跟着回包改，写死第 11 场那句不再出现。
            flow.ClearViews();
            var shell = UiShell.Ensure();
            shell.ShowPage(UiStrings.UiWorldSelfCheck.T002, s => UiWorldMapScreen.Build(s, flow));
            var copy = Text(shell);
            Report("regions_screen_from_reply",
                copy.Contains(UiStrings.UiWorldSelfCheck.T035) && copy.Contains(UiStrings.UiWorldSelfCheck.T040)
                && copy.Contains(UiStrings.UiWorldSelfCheck.T041)
                && copy.Contains(UiStrings.UiWorldSelfCheck.T043)
                && !copy.Contains(UiStrings.UiWorldSelfCheck.T044)
                && !copy.Contains(UiStrings.UiWorldSelfCheck.T045));
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
                qiao.Name == UiStrings.UiWorldSelfCheck.T046
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
                items.Add("{\"node_id\":\"ore_mock_" + i + UiStrings.UiWorldSelfCheck.T047
                    + "\"item_id\":\"ore_iron\",\"count\":2,\"bag_slot\":1,\"respawn_s\":2}");
            }

            for (var i = 1; i <= 8; i++)
            {
                items.Add("{\"node_id\":\"herb_mock_" + i + UiStrings.UiWorldSelfCheck.T048
                    + "\"item_id\":\"herb_ling\",\"count\":3,\"bag_slot\":2,\"respawn_s\":2}");
            }

            items.Add(UiStrings.UiWorldSelfCheck.T049
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
            UiShell.Ensure().ShowPage(UiStrings.UiWorldSelfCheck.T004, s => UiGatherScreen.Build(s, flow, UiStrings.UiWorldSelfCheck.T005));
            Report("gather_initial", flow.GatherLine().Contains(UiStrings.UiWorldSelfCheck.T050));

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
                && flow.GatherLine().Contains(UiStrings.UiWorldSelfCheck.T051));

            _nextStatus = 200;
            _nextBody = "{\"ok\":false,\"reason\":\"spirit_stone_forbidden\",\"node_id\":\"spirit_qiyun_1\","
                + "\"kind\":\"spirit_stone\",\"item_id\":\"\",\"count\":0,\"bag_slot\":0,"
                + "\"txn_id\":\"\",\"duplicate\":false,\"respawn_s\":0}";
            flow.Gather("spirit_qiyun_1");
            Report("gather_refused",
                !flow.GatherReply.Ok && flow.GatherLine().Contains(UiStrings.UiWorldSelfCheck.T052));
        }

        // ------------------------------------------------------------------ B05

        static void CheckFly(int regions, UiWorldFlow flow)
        {
            Report("fly_regions", regions == 8 && UiWorldCodex.Realms.Length == 3);
            Report("fly_initial", !flow.HasFly && flow.FlyLine().Contains(UiStrings.UiWorldSelfCheck.T053));

            // 没权限：回包的 y 是拉回后的地面高度，画面高度跟它走。
            _nextStatus = 200;
            _nextBody = "{\"ok\":false,\"reason\":\"flight_forbidden\",\"flying\":false,"
                + UiStrings.UiWorldSelfCheck.T054;
            flow.Fly(true);
            Report("fly_body", _lastPath == "/world/fly" && _lastBody == "{\"want_fly\":true}");
            Report("fly_denied_pullback",
                !flow.FlyReply.Ok
                && flow.ScreenY == 0f
                && !flow.TrailAllowed
                && flow.FlyLine().Contains(UiStrings.UiWorldSelfCheck.T055)
                && flow.FlyLine().Contains(UiStrings.UiWorldSelfCheck.T056));

            // 起飞成功：高度取回包的 y，拖尾跟着 flying 走。
            _nextStatus = 200;
            _nextBody = "{\"ok\":true,\"reason\":\"\",\"flying\":true,\"can_fly\":true,"
                + UiStrings.UiWorldSelfCheck.T057;
            flow.Fly(true);
            Report("fly_ok", flow.FlyReply.Ok
                && Mathf.Abs(flow.ScreenY - 12.5f) < 0.001f
                && flow.TrailAllowed
                && flow.FlyLine().Contains(UiStrings.UiWorldSelfCheck.T058));

            // 落地：flying=false 时拖尾立刻关。
            _nextStatus = 200;
            _nextBody = "{\"ok\":true,\"reason\":\"\",\"flying\":false,\"can_fly\":true,"
                + UiStrings.UiWorldSelfCheck.T059;
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
            shell.ShowPage(UiStrings.UiWorldSelfCheck.T001, s => UiWorldHudScreen.Build(s, flow));
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
            UiStrings.UiWorldSelfCheck.T060 +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":0,\"main_stage\":0}," +
            UiStrings.UiWorldSelfCheck.T061 +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":0,\"main_stage\":0}," +
            UiStrings.UiWorldSelfCheck.T062 +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":0,\"main_stage\":0}," +
            UiStrings.UiWorldSelfCheck.T063 +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":0,\"main_stage\":0}," +
            UiStrings.UiWorldSelfCheck.T064 +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":0,\"main_stage\":0}," +
            UiStrings.UiWorldSelfCheck.T065 +
            "\"dialogue_ids\":[],\"open\":true,\"reason\":\"\",\"unlock_stage\":11,\"main_stage\":11}," +
            UiStrings.UiWorldSelfCheck.T066 +
            "\"dialogue_ids\":[],\"open\":false,\"reason\":\"region_route_mismatch\"," +
            "\"unlock_stage\":11,\"main_stage\":11}," +
            UiStrings.UiWorldSelfCheck.T067 +
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
