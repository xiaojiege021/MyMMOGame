using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Mmo.Net;
using Debug = UnityEngine.Debug;

// P1-4b 编辑器核对：保护券只有一种且到账即绑、券只挡掉级不提高成功率、
// 客户端只多带一个 use_coupon 开关、券编号 / 数量 / 来源与结果都只认服务端回包。
// 全程不进 playmode，不连服务端，不重打 AB。
public static class P14bCheck
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // 1. 配置与物品表：唯一一种券，到账即绑；配置里没有第二套「带券概率」
        var cfg = File.ReadAllText(Path.Combine(root, "config/enhance.json"));
        var items = File.ReadAllText(Path.Combine(root, "config/items.json"));
        var couponBlock = Regex.Match(
            items, @"\{\s*""id""\s*:\s*""ticket_protect""[\s\S]*?\}");
        var configOk = Regex.IsMatch(cfg, @"""protect_item_id""\s*:\s*""ticket_protect""")
                       && cfg.Contains("数值待定")
                       && cfg.Contains("\"success_rate\"")
                       && Regex.Matches(cfg, @"""success_rate""").Count == 3
                       && couponBlock.Success
                       && Regex.IsMatch(couponBlock.Value, @"""kind""\s*:\s*""voucher""")
                       && Regex.IsMatch(couponBlock.Value, @"""bind""\s*:\s*""always""")
                       && !cfg.Contains("protect_rate")
                       && !cfg.Contains("coupon_rate");
        Debug.Log("p14b_config=" + (configOk ? "ok" : "fail"));
        failed |= !configOk;

        // 2. 契约：请求只多一个 use_coupon，券编号 / 数量 / 来源 / 结果都带不进
        var proto = File.ReadAllText(Path.Combine(root, "proto/mmo/character.proto"));
        var requestFields = Fields(Body(proto, "EnhanceRequest"));
        var responseFields = Fields(Body(proto, "EnhanceResponse"));
        var protoOk = requestFields.SetEquals(
                          new[] { "account", "character_id", "slot", "stone_id", "txn_id", "use_coupon" })
                      && !requestFields.Contains("coupon_id")
                      && !requestFields.Contains("coupon_count")
                      && !requestFields.Contains("coupon_source")
                      && !requestFields.Contains("success_rate")
                      && !requestFields.Contains("protected_level")
                      && !requestFields.Contains("level")
                      && responseFields.Contains("success_rate")
                      && responseFields.Contains("protected_level")
                      && responseFields.Contains("coupon_used")
                      && responseFields.Contains("coupon_left")
                      && responseFields.Contains("duplicate")
                      && Regex.IsMatch(proto, @"rpc Enhance\(EnhanceRequest\) returns \(EnhanceResponse\)");
        Debug.Log("p14b_proto=" + (protoOk ? "ok" : "fail"));
        failed |= !protoOk;

        // 3. 拆装：带券失败等级不变、券只在挡掉级时用掉、普通失败仍掉 1 级
        var plain = CharacterEnhance.RequestBody(42UL, 3U, "stone_enhance_low");
        var withCoupon = CharacterEnhance.RequestBody(42UL, 3U, "stone_enhance_low", true);
        const string protectedReply = "{\"ok\":true,\"reason\":\"\",\"level_before\":4,\"level\":4,"
            + "\"max_level\":12,\"success\":false,\"tier\":\"low\","
            + "\"required_stone\":\"stone_enhance_low\",\"stone_cost\":1,\"gold_cost\":100,"
            + "\"gold\":9900,\"stone_left\":9,\"duplicate\":false,\"success_rate\":0,"
            + "\"protected_level\":true,\"coupon_used\":1,\"coupon_left\":2}";
        const string plainFailReply = "{\"ok\":true,\"reason\":\"\",\"level_before\":4,\"level\":3,"
            + "\"max_level\":12,\"success\":false,\"tier\":\"low\","
            + "\"required_stone\":\"stone_enhance_low\",\"stone_cost\":1,\"gold_cost\":100,"
            + "\"gold\":9800,\"stone_left\":8,\"duplicate\":false,\"success_rate\":0,"
            + "\"protected_level\":false,\"coupon_used\":0,\"coupon_left\":3}";
        const string couponWinReply = "{\"ok\":true,\"reason\":\"\",\"level_before\":4,\"level\":5,"
            + "\"max_level\":12,\"success\":true,\"tier\":\"low\","
            + "\"required_stone\":\"stone_enhance_low\",\"stone_cost\":1,\"gold_cost\":100,"
            + "\"gold\":9700,\"stone_left\":7,\"duplicate\":false,\"success_rate\":1,"
            + "\"protected_level\":false,\"coupon_used\":0,\"coupon_left\":3}";
        const string rejectReply = "{\"ok\":false,\"reason\":\"coupon_insufficient\",\"level_before\":0,"
            + "\"level\":0,\"max_level\":12,\"success\":false,\"tier\":\"\","
            + "\"required_stone\":\"\",\"stone_cost\":0,\"gold_cost\":0,"
            + "\"gold\":0,\"stone_left\":0,\"duplicate\":false,\"success_rate\":0,"
            + "\"protected_level\":false,\"coupon_used\":0,\"coupon_left\":0}";
        var held = CharacterEnhance.Parse(protectedReply);
        var dropped = CharacterEnhance.Parse(plainFailReply);
        var won = CharacterEnhance.Parse(couponWinReply);
        var denied = CharacterEnhance.Parse(rejectReply);
        var bad = CharacterEnhance.Parse(string.Empty);
        var wireOk = plain.Contains("\"character_id\":42")
                     && plain.Contains("\"slot\":3")
                     && !plain.Contains("use_coupon")
                     && withCoupon.Contains("\"use_coupon\":true")
                     && !withCoupon.Contains("ticket_protect")
                     && !withCoupon.Contains("coupon_id")
                     && !withCoupon.Contains("coupon_count")
                     && !withCoupon.Contains("coupon_source")
                     && !withCoupon.Contains("success_rate")
                     && !withCoupon.Contains("txn_id")
                     && held.Ok && !held.Success && held.LevelBefore == 4 && held.Level == 4
                     && held.ProtectedLevel && held.CouponUsed == 1 && held.CouponLeft == 2
                     && held.DisplayText.Contains("保护券") && held.DisplayText.Contains("等级不变")
                     && dropped.Ok && !dropped.Success && dropped.Level == 3
                     && !dropped.ProtectedLevel && dropped.CouponUsed == 0
                     && dropped.DisplayText.Contains("失败") && !dropped.DisplayText.Contains("保护券")
                     && won.Ok && won.Success && won.Level == 5
                     && !won.ProtectedLevel && won.CouponUsed == 0 && won.CouponLeft == 3
                     && !denied.Ok && denied.Reason == "coupon_insufficient"
                     && denied.CouponUsed == 0 && denied.ProtectedLevel == false
                     && !bad.Ok && bad.Reason == "bad_reply";
        Debug.Log("p14b_wire=" + (wireOk ? "ok" : "fail"));
        failed |= !wireOk;

        // 4. 边界：券定义只在角色服加载，掷点只有一行且带券与不带券共用同一档概率
        var table = File.ReadAllText(Path.Combine(root, "src/Character/EnhanceTable.cs"));
        var repo = File.ReadAllText(Path.Combine(root, "src/Character/EnhanceRepository.cs"));
        var api = File.ReadAllText(Path.Combine(root, "src/Gateway/CharacterApi.cs"));
        var process = File.ReadAllText(Path.Combine(root, "src/Character/CharacterProcess.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src/Character/Program.cs"));
        var client = File.ReadAllText(Path.Combine(Application.dataPath, "Net/CharacterEnhance.cs"));
        var boundaryOk = table.Contains("protectItem.Bind != BindRule.Always")
                         && table.Contains("enhance.json 缺 protect_item_id")
                         && table.Contains("不能和强化石是同一种")
                         && Regex.Matches(repo, @"Random\.Shared\.NextDouble\(\) < tier\.SuccessRate").Count == 1
                         && repo.Contains("var protectedLevel = !success && useCoupon")
                         && repo.Contains("table.ProtectItemId")
                         && repo.Contains("coupon_insufficient")
                         && repo.Contains("Math.Max(0, levelBefore - 1)")
                         && repo.Contains("BeginTransactionAsync")
                         && !repo.Contains("protect_rate") && !repo.Contains("0.8")
                         && api.Contains("bool? UseCoupon")
                         && api.Contains("UseCoupon = body.UseCoupon ?? false")
                         && api.Contains("OwnsAsync") && api.Contains("Guid.NewGuid()")
                         && process.Contains("coupon_bind=always")
                         && program.Contains("coupon_bind=always")
                         && !client.Contains("EnhanceTable")
                         && !client.Contains("Random")
                         && !client.Contains("JsonUtility")
                         && !client.Contains("success_rate")
                         && !client.Contains("ticket_protect")
                         && !client.Contains("0.8");
        Debug.Log("p14b_boundary=" + (boundaryOk ? "ok" : "fail"));
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
            body, @"(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*="))
        {
            fields.Add(field.Groups[1].Value);
        }

        return fields;
    }
}
