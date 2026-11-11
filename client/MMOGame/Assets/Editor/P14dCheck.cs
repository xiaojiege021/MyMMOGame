using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Mmo.Net;
using Debug = UnityEngine.Debug;

// P1-4d 编辑器核对：方案第 9 章第 3 层写定的三类与部位限定只在配置与角色服里、
// 装上请求只带三个格位、拆下只带两个、结果跟着回包走、客户端不在本地算类别或部位。
// 全程不进 playmode，不连服务端，不重打 AB。
public static class P14dCheck
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // 1. 配置与物品表：写定的三类、部位限定、可拆复用；没写定的数值注明待定
        var cfg = File.ReadAllText(Path.Combine(root, "config/crystal.json"));
        var items = File.ReadAllText(Path.Combine(root, "config/items.json"));
        var categoryParts = Regex.Match(cfg, @"""category_parts""\s*:\s*\{([\s\S]*?)\}");
        var equipmentParts = Regex.Match(cfg, @"""equipment_parts""\s*:\s*\{([\s\S]*?)\}");
        var crystals = Regex.Match(cfg, @"""crystals""\s*:\s*\[([\s\S]*?)\]");
        var attack = Regex.Match(items, @"\{\s*""id""\s*:\s*""crystal_attack""[\s\S]*?\}");
        var robe = Regex.Match(items, @"\{\s*""id""\s*:\s*""robe_trial""[\s\S]*?\}");
        var ring = Regex.Match(items, @"\{\s*""id""\s*:\s*""ring_trial""[\s\S]*?\}");
        var configOk = Regex.IsMatch(cfg,
                           @"""categories""\s*:\s*\[\s*""attack""\s*,\s*""defense""\s*,\s*""utility""\s*\]")
                       && Regex.IsMatch(cfg, @"""socket_count""\s*:\s*[1-9]")
                       && cfg.Contains("待定")
                       && cfg.Contains("可拆卸复用")
                       && !cfg.Contains("success_rate")
                       && categoryParts.Success
                       && Regex.IsMatch(categoryParts.Groups[1].Value, @"""attack""\s*:\s*\[""weapon""\]")
                       && Regex.IsMatch(categoryParts.Groups[1].Value, @"""defense""\s*:\s*\[""armor""\]")
                       && Regex.IsMatch(categoryParts.Groups[1].Value, @"""utility""\s*:\s*\[""accessory""\]")
                       && equipmentParts.Success
                       && Regex.IsMatch(equipmentParts.Groups[1].Value, @"""sword_trial""\s*:\s*""weapon""")
                       && Regex.IsMatch(equipmentParts.Groups[1].Value, @"""robe_trial""\s*:\s*""armor""")
                       && Regex.IsMatch(equipmentParts.Groups[1].Value, @"""ring_trial""\s*:\s*""accessory""")
                       && crystals.Success
                       && Regex.IsMatch(crystals.Groups[1].Value, @"""id""\s*:\s*""crystal_attack""\s*,\s*""category""\s*:\s*""attack""")
                       && Regex.IsMatch(crystals.Groups[1].Value, @"""id""\s*:\s*""crystal_defense""\s*,\s*""category""\s*:\s*""defense""")
                       && Regex.IsMatch(crystals.Groups[1].Value, @"""id""\s*:\s*""crystal_utility""\s*,\s*""category""\s*:\s*""utility""")
                       && attack.Success
                       && Regex.IsMatch(attack.Value, @"""kind""\s*:\s*""crystal""")
                       && robe.Success
                       && Regex.IsMatch(robe.Value, @"""kind""\s*:\s*""equipment""")
                       && ring.Success
                       && Regex.IsMatch(ring.Value, @"""kind""\s*:\s*""equipment""");
        Debug.Log("p14d_config=" + (configOk ? "ok" : "fail"));
        failed |= !configOk;

        // 2. 契约：装上只带三个格位、拆下只带两个；类别、部位、结果只在回包里
        var proto = File.ReadAllText(Path.Combine(root, "proto/mmo/character.proto"));
        var installFields = Fields(Body(proto, "InstallCrystalRequest"));
        var removeFields = Fields(Body(proto, "RemoveCrystalRequest"));
        var responseFields = Fields(Body(proto, "CrystalResponse"));
        var protoOk = installFields.SetEquals(
                          new[] { "account", "character_id", "slot", "crystal_slot", "socket", "txn_id" })
                      && removeFields.SetEquals(
                          new[] { "account", "character_id", "slot", "socket", "txn_id" })
                      && !installFields.Contains("category")
                      && !installFields.Contains("part")
                      && !installFields.Contains("crystal_item")
                      && !installFields.Contains("fixed_value")
                      && responseFields.Contains("category")
                      && responseFields.Contains("part")
                      && responseFields.Contains("socket_count")
                      && responseFields.Contains("filled")
                      && responseFields.Contains("duplicate")
                      && Regex.IsMatch(proto,
                          @"rpc InstallCrystal\(InstallCrystalRequest\) returns \(CrystalResponse\)")
                      && Regex.IsMatch(proto,
                          @"rpc RemoveCrystal\(RemoveCrystalRequest\) returns \(CrystalResponse\)");
        Debug.Log("p14d_proto=" + (protoOk ? "ok" : "fail"));
        failed |= !protoOk;

        // 3. 拆装：装上、拆下、部位不对、重复提交、坏回包
        var installBody = CharacterCrystal.InstallBody(42UL, 1U, 3U, 2U);
        var removeBody = CharacterCrystal.RemoveBody(42UL, 1U, 2U);
        const string okInstall = "{\"ok\":true,\"reason\":\"\",\"installed\":true,\"slot\":1,\"socket\":2,"
            + "\"crystal_item\":\"crystal_attack\",\"category\":\"attack\",\"part\":\"weapon\","
            + "\"socket_count\":2,\"filled_before\":0,\"filled\":1,\"crystal_left\":4,"
            + "\"gold\":1000,\"enhance_level\":3,\"bound\":true,\"duplicate\":false}";
        const string okRemove = "{\"ok\":true,\"reason\":\"\",\"installed\":false,\"slot\":1,\"socket\":2,"
            + "\"crystal_item\":\"crystal_attack\",\"category\":\"attack\",\"part\":\"weapon\","
            + "\"socket_count\":2,\"filled_before\":1,\"filled\":0,\"crystal_left\":5,"
            + "\"gold\":1000,\"enhance_level\":3,\"bound\":true,\"duplicate\":false}";
        const string wrongPart = "{\"ok\":false,\"reason\":\"wrong_part\",\"installed\":true,"
            + "\"slot\":1,\"socket\":2,\"crystal_item\":\"\",\"category\":\"\",\"part\":\"\","
            + "\"socket_count\":2,\"filled_before\":0,\"filled\":0,\"crystal_left\":0,"
            + "\"gold\":0,\"enhance_level\":0,\"bound\":false,\"duplicate\":false}";
        const string duplicate = "{\"ok\":false,\"reason\":\"duplicate\",\"installed\":false,"
            + "\"slot\":1,\"socket\":2,\"crystal_item\":\"\",\"category\":\"\",\"part\":\"\","
            + "\"socket_count\":2,\"filled_before\":0,\"filled\":0,\"crystal_left\":0,"
            + "\"gold\":0,\"enhance_level\":0,\"bound\":false,\"duplicate\":true}";
        var put = CharacterCrystal.Parse(okInstall);
        var take = CharacterCrystal.Parse(okRemove);
        var denied = CharacterCrystal.Parse(wrongPart);
        var repeated = CharacterCrystal.Parse(duplicate);
        var broken = CharacterCrystal.Parse(string.Empty);
        var wireOk = installBody.Contains("\"character_id\":42")
                     && installBody.Contains("\"slot\":1")
                     && installBody.Contains("\"crystal_slot\":3")
                     && installBody.Contains("\"socket\":2")
                     && !installBody.Contains("category") && !installBody.Contains("part")
                     && !installBody.Contains("crystal_item") && !installBody.Contains("txn_id")
                     && !installBody.Contains("account") && !installBody.Contains("fixed_value")
                     && removeBody.Contains("\"character_id\":42")
                     && removeBody.Contains("\"slot\":1")
                     && removeBody.Contains("\"socket\":2")
                     && !removeBody.Contains("crystal_slot")
                     && !removeBody.Contains("txn_id") && !removeBody.Contains("category")
                     && put.Ok && put.Installed && put.Socket == 2U
                     && put.Category == "attack" && put.Part == "weapon"
                     && put.CrystalItem == "crystal_attack" && put.Filled == 1
                     && put.CrystalLeft == 4 && put.Gold == 1000 && put.EnhanceLevel == 3 && put.Bound
                     && put.DisplayText.Contains("镶嵌成功")
                     && take.Ok && !take.Installed && take.Filled == 0 && take.CrystalLeft == 5
                     && take.DisplayText.Contains("拆卸成功")
                     && !denied.Ok && denied.Reason == "wrong_part"
                     && denied.DisplayText.Contains("部位不对")
                     && denied.Filled == 0 && denied.CrystalLeft == 0 && denied.Gold == 0
                     && !repeated.Ok && repeated.Duplicate
                     && repeated.DisplayText.Contains("重复提交")
                     && !broken.Ok && broken.Reason == "bad_reply";
        Debug.Log("p14d_wire=" + (wireOk ? "ok" : "fail"));
        failed |= !wireOk;

        // 4. 边界：类别与部位只在角色服、装上拆下同一笔事务、客户端不算结果
        var repo = File.ReadAllText(Path.Combine(root, "src/Character/CrystalRepository.cs"));
        var table = File.ReadAllText(Path.Combine(root, "src/Character/CrystalTable.cs"));
        var api = File.ReadAllText(Path.Combine(root, "src/Gateway/CharacterApi.cs"));
        var process = File.ReadAllText(Path.Combine(root, "src/Character/CharacterProcess.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src/Character/Program.cs"));
        var sql = File.ReadAllText(Path.Combine(root, "db/character/crystal.sql"));
        var client = File.ReadAllText(Path.Combine(Application.dataPath, "Net/CharacterCrystal.cs"));
        var boundaryOk = repo.Contains("wrong_part")
                         && repo.Contains("socket_occupied")
                         && repo.Contains("socket_empty")
                         && repo.Contains("BeginTransactionAsync")
                         && repo.Contains("for update")
                         && repo.Contains("拆卸带走")
                         // C# 源码里 SQL 的引号是转义写法，按裸引号查会落空。
                         && repo.Contains("delete from") && repo.Contains(".crystal_socket")
                         && repo.Contains("set count = count + 1")
                         && !repo.Contains("Random")
                         && table.Contains("必须正好是方案写定的 attack / defense / utility")
                         && table.Contains("equipment_parts 漏了装备部位")
                         && api.Contains("public sealed record InstallCrystalBody(")
                         && api.Contains("public sealed record RemoveCrystalBody(")
                         && api.Contains("MapPost(\"/character/crystal/install\"")
                         && api.Contains("MapPost(\"/character/crystal/remove\"")
                         && api.Contains("OwnsAsync") && api.Contains("Guid.NewGuid()")
                         && process.Contains("crystal_table_version=")
                         && program.Contains("case \"--crystal-state\"")
                         && program.Contains("case \"--crystal-sockets\"")
                         && sql.Contains("crystal_socket")
                         && sql.Contains("crystal_txn")
                         && !client.Contains("CrystalTable")
                         && !client.Contains("Random")
                         && !client.Contains("JsonUtility")
                         && !client.Contains("crystal.json");
        Debug.Log("p14d_boundary=" + (boundaryOk ? "ok" : "fail"));
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
