using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// U9 编辑器核对：F04 接 C3 `/homestead*`，种植仍标尚未接服务端，不动 proto / config。
/// </summary>
public static class U9Check
{
    static bool failed;

    public static void Run()
    {
        if (!CheckAll())
        {
            EditorApplication.Exit(1);
        }
    }

    public static bool CheckAll()
    {
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        CheckFiles(root);
        CheckWiring(root);
        CheckSelf();

        Debug.Log("u9_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/U9 洞府摆放屏自检")]
    static void RunFromMenu() => CheckAll();

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u9_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    static void CheckFiles(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Path.Combine(root, "client/MMOGame/Assets/Net");
        var missing = new List<string>();
        foreach (var name in new[]
                 {
                     "UiHomesteadScreen.cs", "UiHomesteadSelfCheck.cs", "UiSocialFlow.cs",
                     "UiSocialCodex.cs",
                 })
        {
            if (!File.Exists(Path.Combine(ui, name))) missing.Add(name);
        }

        if (!File.Exists(Path.Combine(net, "HomesteadEntry.cs"))) missing.Add("Net/HomesteadEntry.cs");
        if (!File.Exists(Path.Combine(root, "client/MMOGame/Assets/Editor/U9Check.cs")))
        {
            missing.Add("Editor/U9Check.cs");
        }

        Report("files", missing.Count == 0, string.Join(",", missing.ToArray()));
    }

    static void CheckWiring(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Path.Combine(root, "client/MMOGame/Assets/Net");
        var home = Read(Path.Combine(ui, "UiHomesteadScreen.cs"));
        var flow = Read(Path.Combine(ui, "UiSocialFlow.cs"));
        var codex = Read(Path.Combine(ui, "UiSocialCodex.cs"));
        var entry = Read(Path.Combine(net, "HomesteadEntry.cs"));

        Report("net_parse", entry.Contains("HomesteadEntry") && entry.Contains("DescribePath")
            && entry.Contains("PlaceBody") && entry.Contains("ParsePlacements"),
            "Net 层缺拆装");

        Report("flow_homestead", flow.Contains("HomesteadEntry.Parse")
            && flow.Contains("LoadHomestead") && flow.Contains("PlaceDecor")
            && flow.Contains("RemoveDecor") && flow.Contains("LoadHomesteadBag"),
            "Flow 没接 homestead");

        Report("screen_wired", home.Contains("u9_homestead_wired=ready")
            && home.Contains("HomesteadEntry.ItemInSlot")
            && !home.Contains("无接口"),
            "屏没接摆放或仍写无接口");

        Report("codex_slots", codex.Contains("HomesteadSlots") && codex.Contains("DecorItemIds")
            && codex.IndexOf("config/", StringComparison.Ordinal) < 0,
            "Codex 缺格位或读了 config");

        Report("no_config", home.IndexOf("config/", StringComparison.Ordinal) < 0
            && flow.IndexOf("config/", StringComparison.Ordinal) < 0
            && entry.IndexOf("config/", StringComparison.Ordinal) < 0,
            "入口卡读了配置");

        var social = Read(Path.Combine(root, "proto/mmo/social.proto"));
        Report("no_proto", social.IndexOf("u9", StringComparison.OrdinalIgnoreCase) < 0,
            "这张卡不该动 proto");
    }

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiHomesteadSelfCheck");
        if (type == null)
        {
            Report("self", false, "type_missing");
            return;
        }

        var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
        if (method == null)
        {
            Report("self", false, "method_missing");
            return;
        }

        bool ok;
        try
        {
            ok = (bool)method.Invoke(null, null);
        }
        catch (Exception error)
        {
            var inner = error.InnerException ?? error;
            Report("self", false, inner.GetType().Name + ":" + inner.Message);
            Debug.LogError("u9_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "homestead" : "see_u9_self_logs");
    }

    static Type FindType(string name)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (var i = 0; i < assemblies.Length; i++)
        {
            var type = assemblies[i].GetType(name);
            if (type != null) return type;
        }

        return null;
    }
}
