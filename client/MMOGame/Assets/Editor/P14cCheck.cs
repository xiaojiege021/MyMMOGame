using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Mmo.Net;
using Debug = UnityEngine.Debug;

// P1-4c 编辑器核对：方案 9.3 的写定规则只在配置与角色服里、转移请求只带两个格子、
// 来源是覆盖不是相加、绑定规则跟着回包走、客户端不在本地算等级或绑定。
// 全程不进 playmode，不连服务端，不重打 AB。
public static class P14cCheck
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // 1. 配置与物品表：一枚转移石、费用按等级严格递增、非绑装备样本存在
        var cfg = File.ReadAllText(Path.Combine(root, "config/transfer.json"));
        var items = File.ReadAllText(Path.Combine(root, "config/items.json"));
        var costs = new List<int>();
        foreach (Match m in Regex.Matches(cfg, @"""gold_cost_by_level""\s*:\s*\[(.*?)\]", RegexOptions.Singleline))
        {
            foreach (Match n in Regex.Matches(m.Groups[1].Value, @"\d+"))
            {
                costs.Add(int.Parse(n.Value));
            }
        }

        var rising = costs.Count == 12;
        for (var i = 1; i < costs.Count; i++)
        {
            rising &= costs[i] > costs[i - 1];
        }

        var stoneBlock = Regex.Match(items, @"\{\s*""id""\s*:\s*""stone_transfer""[\s\S]*?\}");
        var tradeBlock = Regex.Match(items, @"\{\s*""id""\s*:\s*""sword_trade""[\s\S]*?\}");
        var configOk = rising
                       && Regex.IsMatch(cfg, @"""stone_item_id""\s*:\s*""stone_transfer""")
                       && Regex.IsMatch(cfg, @"""stone_cost""\s*:\s*1")
                       && Regex.IsMatch(cfg, @"""gold_kind""\s*:\s*""gold""")
                       && cfg.Contains("数值待定")
                       && stoneBlock.Success
                       && Regex.IsMatch(stoneBlock.Value, @"""kind""\s*:\s*""material""")
                       && tradeBlock.Success
                       && Regex.IsMatch(tradeBlock.Value, @"""kind""\s*:\s*""equipment""")
                       && Regex.IsMatch(tradeBlock.Value, @"""bind""\s*:\s*""none""")
                       && !cfg.Contains("success_rate")
                       && !cfg.Contains("must_succeed_rate");
        Debug.Log("p14c_config=" + (configOk ? "ok" : "fail"));
        failed |= !configOk;

        // 2. 契约：请求只带角色与两个格子，结果和绑定只在回包里
        var proto = File.ReadAllText(Path.Combine(root, "proto/mmo/character.proto"));
        var requestFields = Fields(Body(proto, "TransferEnhanceRequest"));
        var responseFields = Fields(Body(proto, "TransferEnhanceResponse"));
        var protoOk = requestFields.SetEquals(
                          new[] { "account", "character_id", "from_slot", "to_slot", "txn_id" })
                      && !requestFields.Contains("from_level")
                      && !requestFields.Contains("to_level")
                      && !requestFields.Contains("bound")
                      && !requestFields.Contains("gold_cost")
                      && !requestFields.Contains("stone_item")
                      && responseFields.Contains("from_level")
                      && responseFields.Contains("to_level")
                      && responseFields.Contains("target_bound_before")
                      && responseFields.Contains("target_bound")
                      && responseFields.Contains("gold_cost")
                      && responseFields.Contains("duplicate")
                      && Regex.IsMatch(proto,
                          @"rpc TransferEnhance\(TransferEnhanceRequest\) returns \(TransferEnhanceResponse\)");
        Debug.Log("p14c_proto=" + (protoOk ? "ok" : "fail"));
        failed |= !protoOk;

        // 3. 拆装：覆盖成功、来源归 0、来源绑定时目标变绑定；拒绝与重复都不生效
        var request = CharacterTransfer.RequestBody(42UL, 1U, 2U);
        const string okReply = "{\"ok\":true,\"reason\":\"\",\"from_level_before\":5,\"to_level_before\":3,"
            + "\"from_level\":0,\"to_level\":5,\"max_level\":12,\"gold_cost\":300,\"gold\":9700,"
            + "\"stone_item\":\"stone_transfer\",\"stone_cost\":1,\"stone_left\":9,"
            + "\"target_bound_before\":false,\"target_bound\":true,\"duplicate\":false}";
        const string rejectReply = "{\"ok\":false,\"reason\":\"source_not_higher\",\"from_level_before\":0,"
            + "\"to_level_before\":0,\"from_level\":0,\"to_level\":0,\"max_level\":12,"
            + "\"gold_cost\":0,\"gold\":0,\"stone_item\":\"\",\"stone_cost\":0,\"stone_left\":0,"
            + "\"target_bound_before\":false,\"target_bound\":false,\"duplicate\":false}";
        const string duplicateReply = "{\"ok\":false,\"reason\":\"duplicate\",\"from_level_before\":0,"
            + "\"to_level_before\":0,\"from_level\":0,\"to_level\":0,\"max_level\":12,"
            + "\"gold_cost\":0,\"gold\":0,\"stone_item\":\"\",\"stone_cost\":0,\"stone_left\":0,"
            + "\"target_bound_before\":false,\"target_bound\":false,\"duplicate\":true}";
        var moved = CharacterTransfer.Parse(okReply);
        var denied = CharacterTransfer.Parse(rejectReply);
        var repeated = CharacterTransfer.Parse(duplicateReply);
        var broken = CharacterTransfer.Parse(string.Empty);
        var wireOk = request.Contains("\"character_id\":42")
                     && request.Contains("\"from_slot\":1")
                     && request.Contains("\"to_slot\":2")
                     && !request.Contains("from_level") && !request.Contains("to_level")
                     && !request.Contains("bound") && !request.Contains("gold")
                     && !request.Contains("stone") && !request.Contains("txn_id")
                     && !request.Contains("account")
                     && moved.Ok && moved.FromLevelBefore == 5 && moved.ToLevelBefore == 3
                     && moved.FromLevel == 0 && moved.ToLevel == 5
                     && moved.TargetBoundBefore == false && moved.TargetBound
                     && moved.GoldCost == 300 && moved.StoneLeft == 9
                     && moved.DisplayText.Contains("转移成功") && moved.DisplayText.Contains("目标已绑定")
                     && !denied.Ok && denied.Reason == "source_not_higher"
                     && denied.FromLevel == 0 && denied.ToLevel == 0 && denied.GoldCost == 0
                     && !repeated.Ok && repeated.Duplicate
                     && repeated.DisplayText.Contains("重复提交")
                     && !broken.Ok && broken.Reason == "bad_reply";
        Debug.Log("p14c_wire=" + (wireOk ? "ok" : "fail"));
        failed |= !wireOk;

        // 4. 边界：来源判定与绑定只在角色服、同一笔事务、客户端不算等级与绑定
        var repo = File.ReadAllText(Path.Combine(root, "src/Character/TransferRepository.cs"));
        var table = File.ReadAllText(Path.Combine(root, "src/Character/TransferTable.cs"));
        var api = File.ReadAllText(Path.Combine(root, "src/Gateway/CharacterApi.cs"));
        var process = File.ReadAllText(Path.Combine(root, "src/Character/CharacterProcess.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src/Character/Program.cs"));
        var client = File.ReadAllText(Path.Combine(Application.dataPath, "Net/CharacterTransfer.cs"));
        var boundaryOk = repo.Contains("if (fromLevelBefore <= toLevelBefore)")
                         && repo.Contains("source_not_higher")
                         && repo.Contains("(fromSlot, 0), (toSlot, fromLevelBefore)")
                         && repo.Contains("bound = true, bound_at = now()")
                         && repo.Contains("var targetBound = targetBoundBefore || bounds[fromSlot]")
                         && repo.Contains("BeginTransactionAsync")
                         && repo.Contains("for update")
                         && repo.Contains("stone_insufficient")
                         && repo.Contains("gold_insufficient")
                         && !repo.Contains("Random")
                         && table.Contains("费用随转出的强化等级上升")
                         && table.Contains("必须是方案写定的 1")
                         && api.Contains("public sealed record TransferBody(ulong? CharacterId, uint? FromSlot, uint? ToSlot)")
                         && api.Contains("MapPost(\"/character/transfer\"")
                         && api.Contains("OwnsAsync") && api.Contains("Guid.NewGuid()")
                         && process.Contains("transfer_gold=")
                         && program.Contains("case \"--transfer-state\"")
                         && program.Contains("case \"--transfer-level\"")
                         && !client.Contains("TransferTable")
                         && !client.Contains("Random")
                         && !client.Contains("JsonUtility")
                         && !client.Contains("transfer.json")
                         && !client.Contains("from_level_before\":");
        Debug.Log("p14c_boundary=" + (boundaryOk ? "ok" : "fail"));
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
