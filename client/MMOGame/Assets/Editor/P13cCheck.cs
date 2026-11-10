using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Mmo.Net;
using Debug = UnityEngine.Debug;

// P1-3c 编辑器核对：境界只在角色服、飞行与跨区门槛只在配置里、
// 客户端请求带不进权限、被拉回只认服务端回包。全程不进 playmode，不连服务端，不重打 AB。
public static class P13cCheck
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // 1. 配置：三段境界照方案 8.1，门槛与飞行境界都注明待定，地面高度不另写一套
        var cfg = File.ReadAllText(Path.Combine(root, "config/flight.json"));
        var configOk = cfg.Contains("数值待定")
                       && cfg.Contains("\"realms\"")
                       && cfg.Contains("炼体") && cfg.Contains("筑基") && cfg.Contains("结丹")
                       && cfg.Contains("\"flight_realm\"")
                       && cfg.Contains("\"region_realms\"")
                       && cfg.Contains("spawn_y")
                       && cfg.Contains("max_move_speed");
        Debug.Log("p13c_config=" + (configOk ? "ok" : "fail"));
        failed |= !configOk;

        // 2. 契约：客户端请求只有开关 / 目标地点，境界、权限、账号与当前区域都带不进
        var proto = File.ReadAllText(Path.Combine(root, "proto/mmo/world.proto"));
        var characterProto = File.ReadAllText(Path.Combine(root, "proto/mmo/character.proto"));
        var flyBody = Body(proto, "FlyRequest");
        var regionBody = Body(proto, "CrossRegionRequest");
        var flyFields = Fields(flyBody);
        var regionFields = Fields(regionBody);
        var protoOk = flyBody.Length > 0
                      && flyFields.Contains("account")
                      && flyFields.Contains("character_id")
                      && flyFields.Contains("want_fly")
                      && !flyFields.Contains("realm")
                      && !flyFields.Contains("can_fly")
                      && !flyFields.Contains("flying")
                      && !flyFields.Contains("required_realm")
                      && regionBody.Length > 0
                      && regionFields.Contains("target_region")
                      && !regionFields.Contains("realm")
                      && !regionFields.Contains("region_id")
                      && !regionFields.Contains("line_id")
                      && Regex.IsMatch(proto, @"rpc Fly\(FlyRequest\) returns \(FlyResponse\)")
                      && Regex.IsMatch(proto, @"rpc CrossRegion\(CrossRegionRequest\) returns \(CrossRegionResponse\)")
                      && Regex.IsMatch(proto, @"bool flying = 9;")
                      && Regex.IsMatch(proto, @"bool can_fly = 10;")
                      && Regex.IsMatch(proto, @"bool self_flying = 7;")
                      && Regex.IsMatch(characterProto, @"string realm = 4;");
        Debug.Log("p13c_proto=" + (protoOk ? "ok" : "fail")
                  + " fly=" + flyBody.Length + " region=" + regionBody.Length);
        failed |= !protoOk;

        // 3. 客户端只装请求与展示，不自行判权限；拖尾只跟服务端 flying
        var flyRequest = WorldFlight.FlyRequestBody(true);
        var regionRequest = WorldFlight.RegionRequestBody("龙巢门口");
        const string forbidden = "{\"ok\":false,\"reason\":\"flight_forbidden\",\"flying\":false,"
                                 + "\"can_fly\":false,\"realm\":\"炼体\",\"required_realm\":\"筑基\",\"y\":0}";
        const string allowed = "{\"ok\":true,\"reason\":\"\",\"flying\":true,"
                               + "\"can_fly\":true,\"realm\":\"筑基\",\"required_realm\":\"筑基\",\"y\":4.5}";
        const string regionDenied = "{\"ok\":false,\"reason\":\"region_realm_locked\","
                                    + "\"region_id\":\"栖云城\",\"line_id\":\"栖云城#1\",\"line_no\":1,"
                                    + "\"realm\":\"炼体\",\"required_realm\":\"结丹\"}";
        const string regionOk = "{\"ok\":true,\"reason\":\"\",\"region_id\":\"龙巢门口\","
                                + "\"line_id\":\"龙巢门口#1\",\"line_no\":1,"
                                + "\"realm\":\"结丹\",\"required_realm\":\"结丹\"}";
        var deniedFly = WorldFlight.ParseFly(forbidden);
        var allowedFly = WorldFlight.ParseFly(allowed);
        var deniedRegion = WorldFlight.ParseRegion(regionDenied);
        var okRegion = WorldFlight.ParseRegion(regionOk);
        var bad = WorldFlight.ParseFly(string.Empty);
        var wireOk = flyRequest.Contains("want_fly")
                     && !flyRequest.Contains("realm")
                     && !flyRequest.Contains("can_fly")
                     && !flyRequest.Contains("account")
                     && regionRequest.Contains("龙巢门口")
                     && !regionRequest.Contains("realm")
                     && !regionRequest.Contains("line_id")
                     && !deniedFly.Ok && deniedFly.Reason == "flight_forbidden"
                     && !deniedFly.Flying && !deniedFly.CanFly && !deniedFly.TrailAllowed
                     && deniedFly.Y == 0f && deniedFly.RequiredRealm == "筑基"
                     && deniedFly.DisplayText.Contains("flight_forbidden")
                     && allowedFly.Ok && allowedFly.Flying && allowedFly.TrailAllowed
                     && allowedFly.CanFly && allowedFly.Y > 4f
                     && !deniedRegion.Ok && deniedRegion.Reason == "region_realm_locked"
                     && deniedRegion.RegionId == "栖云城" && deniedRegion.LineId == "栖云城#1"
                     && deniedRegion.LineNo == 1 && deniedRegion.RequiredRealm == "结丹"
                     && okRegion.Ok && okRegion.RegionId == "龙巢门口"
                     && okRegion.DisplayText.Contains("龙巢门口")
                     && !bad.Ok;
        Debug.Log("p13c_wire=" + (wireOk ? "ok" : "fail"));
        failed |= !wireOk;

        // 4. 边界：判定只在世界服，境界只在角色服，网关复核归属，客户端不算门槛
        var gate = File.ReadAllText(Path.Combine(root, "src/World/FlightGate.cs"));
        var table = File.ReadAllText(Path.Combine(root, "src/World/FlightTable.cs"));
        var reader = File.ReadAllText(Path.Combine(root, "src/World/RealmReader.cs"));
        var api = File.ReadAllText(Path.Combine(root, "src/Gateway/WorldApi.cs"));
        var client = File.ReadAllText(Path.Combine(Application.dataPath, "Net/WorldFlight.cs"));
        var boundaryOk = gate.Contains("realms.ReadAsync")
                         && gate.Contains("registry.SetFlight")
                         && gate.Contains("region_realm_locked")
                         && gate.Contains("region_locked")
                         && table.Contains("方案 8.1")
                         && table.Contains("缺地点")
                         && reader.Contains("ListInventoryAsync")
                         && !reader.Contains("Npgsql")
                         && !gate.Contains("Npgsql")
                         && api.Contains("public sealed record FlyBody(ulong? CharacterId, bool? WantFly)")
                         && api.Contains("public sealed record CrossRegionBody(ulong? CharacterId, string? TargetRegion)")
                         && api.Contains("OwnsAsync")
                         && !client.Contains("FlightTable")
                         && !client.Contains("FlightGate")
                         && !client.Contains("ListInventory")
                         && !client.Contains("Math.Sqrt");
        Debug.Log("p13c_boundary=" + (boundaryOk ? "ok" : "fail"));
        failed |= !boundaryOk;

        EditorApplication.Exit(failed ? 1 : 0);
    }

    static string Body(string proto, string message)
    {
        var match = Regex.Match(proto, @"message " + message + @" \{(.*?)\}", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    // 按字段名逐个比对，不用裸子串：account 里也含 count 这几个字母。
    static HashSet<string> Fields(string body)
    {
        var fields = new HashSet<string>();
        foreach (Match field in Regex.Matches(body, @"(?:string|uint64|int32|uint32|bool)\s+(\w+)\s*="))
        {
            fields.Add(field.Groups[1].Value);
        }

        return fields;
    }
}
