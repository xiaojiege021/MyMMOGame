using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// P1-3a1 编辑器核对：八块首发地表在同一组 Launch AB 里，
// 地点编号与配置一致，对白只在 config/dialogue/，后期境界的资源仍被拒。
// 全程不进 playmode，不连服务端。
public static class P13a1Check
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        var configDir = Path.Combine(root, "config");
        var regionDir = Path.Combine(configDir, "region");
        var dialogueDir = Path.Combine(configDir, "dialogue");
        var artDir = Path.Combine(Application.dataPath, "Art/Launch/Region");

        // 新加的地表资源与 proto 生成物要让 Unity 导入，YooAsset 才收得到。
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // 1. config/region/ 文件名用地名，且与客户端的地址清单逐字相同
        var regionFiles = Directory.GetFiles(regionDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var names = AOT.LaunchSession.RegionNames;
        var regionOk = regionFiles.Length == names.Length && regionFiles.SequenceEqual(names.OrderBy(x => x, StringComparer.Ordinal));
        Debug.Log("p13a1_config=" + (regionOk ? "ok" : "fail")
            + " regions=" + regionFiles.Length + " names=" + names.Length);
        failed |= !regionOk;

        // 2. 资源进的还是 Assets/Art/Launch 这一处，八块各一份
        var resourcesOk = true;
        foreach (var name in names)
        {
            var path = Path.Combine(artDir, name + ".txt");
            if (!File.Exists(path) || !File.ReadAllText(path).Contains("region=" + name))
            {
                resourcesOk = false;
            }
        }
        Debug.Log("p13a1_resources=" + (resourcesOk ? "ok" : "fail") + " count=" + names.Length);
        failed |= !resourcesOk;

        // 3. 对白只在配置里：定词不能出现在场景、AOT、热更脚本或地表资源里
        var texts = new List<string>();
        foreach (var file in Directory.GetFiles(dialogueDir, "*.json"))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"text\"\\s*:\\s*\"([^\"]+)\""))
            {
                texts.Add(match.Groups[1].Value);
            }
        }

        var forbidden = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Application.dataPath, "Scenes"), "*.unity")
                     .Concat(Directory.GetFiles(Path.Combine(Application.dataPath, "AOT"), "*.cs"))
                     .Concat(Directory.GetFiles(Path.Combine(Application.dataPath, "HotUpdate"), "*.cs"))
                     .Concat(Directory.GetFiles(artDir, "*.txt")))
        {
            var body = File.ReadAllText(file);
            if (texts.Any(body.Contains))
            {
                forbidden.Add(file);
            }
        }

        var dialogueOk = texts.Count > 0 && forbidden.Count == 0;
        Debug.Log("p13a1_dialogue=" + (dialogueOk ? "ok" : "fail")
            + " texts=" + texts.Count + " leaked=" + forbidden.Count);
        failed |= !dialogueOk;

        // 4. 还是同一个包、同一个组，地表没有另打一套 AB
        var setting = File.ReadAllText(Path.Combine(Application.dataPath, "AssetBundleCollectorSetting.asset"));
        var packages = Regex.Matches(setting, "PackageName: Launch").Count;
        var groups = Regex.Matches(setting, "GroupName: Launch").Count;
        var collectorOk = packages == 1 && groups == 1
            && setting.Contains("Assets/Art/Launch") && setting.Contains("BossBattle") && setting.Contains("HotUpdateDlls");
        Debug.Log("p13a1_collector=" + (collectorOk ? "ok" : "fail")
            + " packages=" + packages + " groups=" + groups);
        failed |= !collectorOk;

        // 5. 包内容检查：认八块地表、缺一块返回假、后期境界仍被拒
        var checkRegions = typeof(AOT.LaunchSession).GetMethod("CheckRegions",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var good = new List<string> { AOT.LaunchSession.MarkerPath, "Assets/Scenes/BossBattle.unity", "Assets/HotUpdateDlls/HotUpdate.bytes" };
        foreach (var name in names)
        {
            good.Add(AOT.LaunchSession.RegionRoot + "/" + name + ".txt");
        }
        var missing = new List<string>(good);
        missing.RemoveAt(missing.Count - 1);
        var later = new List<string>(good) { "Assets/Art/Launch/Region/元婴/secret.txt" };
        var argsGood = new object[] { good, "" };
        var argsMissing = new object[] { missing, "" };
        var argsLater = new object[] { later, "" };
        var goodOk = checkRegions != null && (bool)checkRegions.Invoke(null, argsGood);
        var missingOk = checkRegions != null && !(bool)checkRegions.Invoke(null, argsMissing);
        var laterOk = checkRegions != null && !(bool)checkRegions.Invoke(null, argsLater);
        Debug.Log("p13a1_check_regions=" + (goodOk && missingOk && laterOk ? "ok" : "fail")
            + " good=" + goodOk + " missing_reject=" + missingOk + " later_reject=" + laterOk);
        failed |= !(goodOk && missingOk && laterOk);

        // 6. 真的重打一次 Launch（Android），manifest 里要有八块地表
        E6Build.PrepareHotDll(BuildTarget.Android);
        var built = E6Build.BuildAndroidAb();
        if (!built.Success)
        {
            Debug.Log("p13a1_manifest=fail build=" + built.ErrorInfo);
            failed = true;
        }
        else
        {
            var manifestPath = Path.Combine(built.OutputPackageDirectory, "Launch_1.json");
            var manifest = E6Build.DecodeUnicode(File.ReadAllText(manifestPath));
            var manifestOk = E6Build.AllRegionsIn(manifest) && !texts.Any(manifest.Contains);
            Debug.Log("p13a1_manifest=" + (manifestOk ? "ok" : "fail")
                + " regions=" + names.Length + " path=" + manifestPath);
            failed |= !manifestOk;
        }

        Debug.Log("p13a1_check=" + (failed ? "fail" : "ok"));
        EditorApplication.Exit(failed ? 1 : 0);
    }
}
