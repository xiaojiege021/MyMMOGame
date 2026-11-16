#if UNITY_EDITOR
using System.Collections.Generic;
using Mmo.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>N4 采集节点表自检：屏上节点只来自节点表回包。</summary>
    public static class UiGatherNodesSelfCheck
    {
        static bool _failed;

        static string _lastPath = string.Empty;

        static int _nextStatus;

        static string _nextBody = string.Empty;

        public static bool Run()
        {
            _failed = false;
            UiGatherScreen.ResetGatherWindows();

            var shell = UiShell.Ensure();
            var flow = UiWorldFlow.Ensure(shell);
            flow.SetSender((method, path, body, token, done) =>
            {
                _lastPath = path;
                done(_nextStatus, _nextBody);
            });
            flow.Configure("token-n4-self", 88);

            shell.ShowPage("采集", s => UiGatherScreen.Build(s, flow, "矿脉"));
            Report("pending", !flow.HasGatherNodes);

            _nextStatus = 200;
            _nextBody = MockNodesJson();
            flow.LoadGatherNodes(() => shell.ShowPage("采集", s => UiGatherScreen.Build(s, flow, "矿脉")));

            Report("path", _lastPath == "/world/gather/nodes");
            Report("count", flow.HasGatherNodes && flow.GatherNodesReply.Count == 18
                && flow.GatherNodesReply.Nodes.Length == 18);
            Report("ore_group", flow.NodesOfKind("ore").Count == 9);
            Report("herb_group", flow.NodesOfKind("herb").Count == 8);
            Report("spirit_group", flow.NodesOfKind("spirit_stone").Count == 1);

            var spirit = default(GatherNodeRow);
            var found = false;
            foreach (var row in flow.GatherNodesReply.Nodes)
            {
                if (row.NodeId == "spirit_qiyun_1")
                {
                    spirit = row;
                    found = true;
                    break;
                }
            }

            Report("spirit_forbidden", found && spirit.Forbidden);

            var listed = 0;
            shell.ShowPage("采集", s => { listed = UiGatherScreen.Build(s, flow, "矿脉"); });
            Report("page", listed == 9);
            UiGatherScreen.StepWindow("矿脉", 1, 9);
            var listedNext = 0;
            shell.ShowPage("采集", s => { listedNext = UiGatherScreen.Build(s, flow, "矿脉"); });
            Report("page_next", listedNext == 9);

            Debug.Log("n4_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        static string MockNodesJson()
        {
            var items = new List<string>();
            for (var i = 1; i <= 9; i++)
            {
                items.Add("{\"node_id\":\"ore_n4_" + i + "\",\"kind\":\"ore\",\"region_id\":\"枯岚道\","
                    + "\"item_id\":\"ore_iron\",\"count\":2,\"bag_slot\":1,\"respawn_s\":2}");
            }

            for (var i = 1; i <= 8; i++)
            {
                items.Add("{\"node_id\":\"herb_n4_" + i + "\",\"kind\":\"herb\",\"region_id\":\"听谣镇\","
                    + "\"item_id\":\"herb_ling\",\"count\":3,\"bag_slot\":2,\"respawn_s\":2}");
            }

            items.Add("{\"node_id\":\"spirit_qiyun_1\",\"kind\":\"spirit_stone\",\"region_id\":\"栖云城\","
                + "\"item_id\":\"\",\"count\":0,\"bag_slot\":0,\"respawn_s\":0}");
            return "{\"count\":18,\"nodes\":[" + string.Join(",", items.ToArray()) + "]}";
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("n4_self_" + key + "=" + (ok ? "ok" : "fail"));
            _failed |= !ok;
        }
    }
}
#endif