using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Mmo.Net;
using Debug = UnityEngine.Debug;

// P1-4a 编辑器核对：强化三档只在配置里、请求带不进等级与成功率、
// 失败只掉 1 级只认服务端回包、判定与落库只在角色服。全程不进 playmode，不连服务端，不重打 AB。
public static class P14aCheck
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // 1. 配置：+12 与三档分界是方案 9.2 写定，概率和消耗注明数值待定
        var cfg = File.ReadAllText(Path.Combine(root, "config/enhance.json"));
        var configOk = cfg.Contains("数值待定")
                       && Regex.IsMatch(cfg, "\"max_level\"\\s*:\\s*12")
                       && cfg.Contains("\"success_rate\"")
                       && cfg.Contains("\"gold_cost\"")
                       && cfg.Contains("\"stone_cost\"")
                       && cfg.Contains("\"from_level\"")
                       && cfg.Contains("stone_enhance_low")
                       && cfg.Contains("stone_enhance_mid")
                       && cfg.Contains("stone_enhance_high")
                       && Regex.IsMatch(cfg, "\"id\"\\s*:\\s*\"low\"")
                       && Regex.IsMatch(cfg, "\"id\"\\s*:\\s*\"mid\"")
                       && Regex.IsMatch(cfg, "\"id\"\\s*:\\s*\"high\"");
        Debug.Log("p14a_config=" + (configOk ? "ok" : "fail"));
        failed |= !configOk;

        // 2. 契约：客户端只给格子与石头，等级、成功率、结果、余额和事务号都带不进
        var proto = File.ReadAllText(Path.Combine(root, "proto/mmo/character.proto"));
        var request = Body(proto, "EnhanceRequest");
        var response = Body(proto, "EnhanceResponse");
        var requestFields = Fields(request);
        var responseFields = Fields(response);
        var protoOk = request.Length > 0
                      // P1-4b 起多一个 use_coupon 开关；老用例不带它，行为不变。
                      && requestFields.SetEquals(new[] { "account", "character_id", "slot", "stone_id", "txn_id", "use_coupon" })
                      && !requestFields.Contains("level")
                      && !requestFields.Contains("success_rate")
                      && !requestFields.Contains("success")
                      && !requestFields.Contains("gold")
                      && responseFields.Contains("ok")
                      && responseFields.Contains("reason")
                      && responseFields.Contains("level")
                      && responseFields.Contains("max_level")
                      && responseFields.Contains("duplicate")
                      && Regex.IsMatch(proto, @"rpc Enhance\(EnhanceRequest\) returns \(EnhanceResponse\)")
                      && Regex.IsMatch(proto, @"bool ok = 1;")
                      && Regex.IsMatch(proto, @"int32 level = 4;");
        Debug.Log("p14a_proto=" + (protoOk ? "ok" : "fail")
                  + " request=" + request.Length + " response=" + response.Length);
        failed |= !protoOk;

        // 3. 回包拆装：成功 +1、失败只掉 1 级、业务拒绝未生效字段都是 0
        var body = CharacterEnhance.RequestBody(42UL, 3U, "stone_enhance_low");
        const string successReply = "{\"ok\":true,\"reason\":\"\",\"level_before\":6,\"level\":7,"
            + "\"max_level\":12,\"success\":true,\"tier\":\"mid\","
            + "\"required_stone\":\"stone_enhance_mid\",\"stone_cost\":1,\"gold_cost\":200,"
            + "\"gold\":800,\"stone_left\":4,\"duplicate\":false}";
        const string failReply = "{\"ok\":true,\"reason\":\"\",\"level_before\":7,\"level\":6,"
            + "\"max_level\":12,\"success\":false,\"tier\":\"mid\","
            + "\"required_stone\":\"stone_enhance_mid\",\"stone_cost\":1,\"gold_cost\":200,"
            + "\"gold\":600,\"stone_left\":3,\"duplicate\":false}";
        const string rejectReply = "{\"ok\":false,\"reason\":\"wrong_stone\",\"level_before\":0,"
            + "\"level\":0,\"max_level\":12,\"success\":false,\"tier\":\"\","
            + "\"required_stone\":\"\",\"stone_cost\":0,\"gold_cost\":0,"
            + "\"gold\":0,\"stone_left\":0,\"duplicate\":false}";
        var up = CharacterEnhance.Parse(successReply);
        var down = CharacterEnhance.Parse(failReply);
        var denied = CharacterEnhance.Parse(rejectReply);
        var bad = CharacterEnhance.Parse(string.Empty);
        var wireOk = body.Contains("\"character_id\":42")
                     && body.Contains("\"slot\":3")
                     && body.Contains("stone_enhance_low")
                     && !body.Contains("level")
                     && !body.Contains("success_rate")
                     && !body.Contains("txn_id")
                     && !body.Contains("account")
                     && !body.Contains("gold")
                     && up.Ok && up.Success && up.LevelBefore == 6 && up.Level == 7
                     && up.MaxLevel == 12 && up.Tier == "mid" && up.RequiredStone == "stone_enhance_mid"
                     && up.GoldCost == 200 && up.Gold == 800 && up.StoneLeft == 4
                     && up.DisplayText.Contains("成功")
                     && down.Ok && !down.Success && down.LevelBefore == 7 && down.Level == 6
                     && down.DisplayText.Contains("失败")
                     && !denied.Ok && denied.Reason == "wrong_stone"
                     && denied.Level == 0 && denied.GoldCost == 0 && denied.StoneCost == 0
                     && !denied.Success && !denied.Duplicate
                     && !bad.Ok && bad.Reason == "bad_reply";
        Debug.Log("p14a_wire=" + (wireOk ? "ok" : "fail"));
        failed |= !wireOk;

        // 4. 边界：读表与落库只在角色服，网关复核归属并生成事务号，客户端不算概率
        var table = File.ReadAllText(Path.Combine(root, "src/Character/EnhanceTable.cs"));
        var repo = File.ReadAllText(Path.Combine(root, "src/Character/EnhanceRepository.cs"));
        var api = File.ReadAllText(Path.Combine(root, "src/Gateway/CharacterApi.cs"));
        var process = File.ReadAllText(Path.Combine(root, "src/Character/CharacterProcess.cs"));
        var client = File.ReadAllText(Path.Combine(Application.dataPath, "Net/CharacterEnhance.cs"));
        var boundaryOk = table.Contains("WrittenMaxLevel")
                         && table.Contains("WrittenTierIds")
                         && table.Contains("缺 stone_id")
                         && repo.Contains("BeginTransactionAsync")
                         && repo.Contains("wrong_stone")
                         && repo.Contains("at_cap")
                         && repo.Contains("Math.Max(0, levelBefore - 1)")
                         && repo.Contains("没有失败累计必成")
                         // P1-4b 起多一个用券开关，老用例不带它行为不变。
                         && api.Contains("public sealed record EnhanceBody(")
                         && api.Contains("bool? UseCoupon")
                         && api.Contains("MapPost(\"/character/enhance\"")
                         && api.Contains("OwnsAsync")
                         && api.Contains("Guid.NewGuid()")
                         && process.Contains("EnhanceTable.Load")
                         && !client.Contains("EnhanceTable")
                         && !client.Contains("Random")
                         && !client.Contains("JsonUtility")
                         && !client.Contains("success_rate")
                         && !client.Contains("0.8");
        Debug.Log("p14a_boundary=" + (boundaryOk ? "ok" : "fail"));
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
        foreach (Match field in Regex.Matches(
            body, @"(?:string|uint64|uint32|int64|int32|bool)\s+(\w+)\s*="))
        {
            fields.Add(field.Groups[1].Value);
        }

        return fields;
    }
}
