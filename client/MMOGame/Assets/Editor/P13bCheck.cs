using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Mmo.Net;
using Debug = UnityEngine.Debug;

// P1-3b 编辑器核对：采集数值只在配置里、请求只有节点编号、成功与否只认服务端回包，
// 灵石矿脉不许配产物。全程不进 playmode，不连服务端，也不重打 AB。
public static class P13bCheck
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // 1. 配置：矿给矿石、药给药材、灵石矿脉没有产物；产量与刷新都注明待定
        var cfg = File.ReadAllText(Path.Combine(root, "config/gather.json"));
        var spirit = Regex.Match(cfg, @"\{[^{}]*""spirit_stone""[^{}]*\}", RegexOptions.Singleline);
        var configOk = cfg.Contains("数值待定")
                       && cfg.Contains("\"item_id\": \"ore_iron\"")
                       && cfg.Contains("\"item_id\": \"herb_ling\"")
                       && cfg.Contains("\"respawn_s\"")
                       && spirit.Success
                       && !spirit.Value.Contains("item_id")
                       && !spirit.Value.Contains("bag_slot");
        Debug.Log("p13b_config=" + (configOk ? "ok" : "fail")
                  + " spirit_node=" + spirit.Success);
        failed |= !configOk;

        // 2. 契约：客户端带不进产物、数量、格子或事务号；灵石矿脉由服务端拒
        var proto = File.ReadAllText(Path.Combine(root, "proto/mmo/world.proto"));
        var request = Regex.Match(proto, @"message GatherRequest \{(.*?)\}", RegexOptions.Singleline);
        var requestBody = request.Success ? request.Groups[1].Value : string.Empty;
        // 按字段名逐个比对，不用裸子串：account 里也含 count 这几个字母。
        var fields = new System.Collections.Generic.HashSet<string>();
        foreach (Match field in Regex.Matches(requestBody, @"(?:string|uint64|int32|uint32|bool)\s+(\w+)\s*="))
        {
            fields.Add(field.Groups[1].Value);
        }
        var protoOk = request.Success
                      && fields.Contains("node_id")
                      && fields.Contains("account")
                      && fields.Contains("character_id")
                      && !fields.Contains("item_id")
                      && !fields.Contains("count")
                      && !fields.Contains("bag_slot")
                      && !fields.Contains("txn_id")
                      && Regex.IsMatch(proto, @"rpc Gather\(GatherRequest\) returns \(GatherResponse\)");
        Debug.Log("p13b_proto=" + (protoOk ? "ok" : "fail")
                  + " request=" + request.Success);
        failed |= !protoOk;

        // 3. 客户端只发节点编号、只按回包展示，不自行判定采集成功
        var body = WorldGather.RequestBody("ore_kulan_1");
        var bodyOk = body.Contains("node_id")
                     && body.Contains("ore_kulan_1")
                     && !body.Contains("item_id")
                     && !body.Contains("count")
                     && !body.Contains("account")
                     && !body.Contains("txn_id");

        const string okReply = "{\"ok\":true,\"reason\":\"\",\"node_id\":\"ore_kulan_1\","
                               + "\"kind\":\"ore\",\"item_id\":\"ore_iron\",\"count\":2,"
                               + "\"bag_slot\":1,\"txn_id\":\"gather:abc\",\"duplicate\":false,\"respawn_s\":2}";
        const string deniedReply = "{\"ok\":false,\"reason\":\"spirit_stone_forbidden\","
                                   + "\"node_id\":\"spirit_qiyun_1\",\"kind\":\"\","
                                   + "\"item_id\":\"\",\"count\":0,\"bag_slot\":0,"
                                   + "\"txn_id\":\"\",\"duplicate\":false,\"respawn_s\":0}";
        var ok = WorldGather.Parse(okReply);
        var denied = WorldGather.Parse(deniedReply);
        var okOk = ok.Ok && ok.ItemId == "ore_iron" && ok.Count == 2 && ok.BagSlot == 1
                   && ok.RespawnSeconds == 2 && ok.TxnId == "gather:abc"
                   && ok.DisplayText.Contains("ore_iron");
        var deniedOk = !denied.Ok && denied.Reason == "spirit_stone_forbidden"
                       && denied.ItemId == string.Empty && denied.Count == 0
                       && denied.DisplayText.Contains("spirit_stone_forbidden");
        var bad = WorldGather.Parse("");
        var wireOk = bodyOk && okOk && deniedOk && !bad.Ok;
        Debug.Log("p13b_wire=" + (wireOk ? "ok" : "fail")
                  + " body=" + bodyOk + " ok_reply=" + okOk + " denied=" + deniedOk);
        failed |= !wireOk;

        // 4. 边界：判定、占用与发奖都在服务端，世界服不写角色库
        var svc = File.ReadAllText(Path.Combine(root, "src/World/WorldGrpcService.cs"));
        var grant = File.ReadAllText(Path.Combine(root, "src/World/GatherGrant.cs"));
        var table = File.ReadAllText(Path.Combine(root, "src/World/GatherTable.cs"));
        var api = File.ReadAllText(Path.Combine(root, "src/Gateway/WorldApi.cs"));
        var client = File.ReadAllText(Path.Combine(Application.dataPath, "Net/WorldGather.cs"));
        var boundaryOk = svc.Contains("spirit_stone_forbidden")
                         && svc.Contains("nodes.TryClaim")
                         && svc.Contains("grants.GrantAsync")
                         && grant.Contains("ApplyGrantRequest")
                         && !grant.Contains("Npgsql")
                         && table.Contains("不许配产物或刷新字段")
                         && api.Contains("public sealed record GatherBody(ulong? CharacterId, string? NodeId)")
                         && api.Contains("OwnsAsync")
                         && !client.Contains("GatherTable")
                         && !client.Contains("ApplyGrant")
                         && !client.Contains("Math.Sqrt");
        Debug.Log("p13b_boundary=" + (boundaryOk ? "ok" : "fail"));
        failed |= !boundaryOk;

        EditorApplication.Exit(failed ? 1 : 0);
    }
}
