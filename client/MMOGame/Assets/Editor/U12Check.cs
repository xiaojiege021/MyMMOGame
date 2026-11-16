using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// U12 编辑器核对：F03 接 N5 的 `POST /versus/arena/enter` 与 `POST /versus/battlefield/enter`，
/// 进场路径与模式事实只认回包，缺口文案已去掉，不本地算战力、不做匹配、不读配置表、不动 proto。
/// </summary>
public static class U12Check
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

        Debug.Log("u12_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/U12 竞技场与战场进场自检")]
    static void RunFromMenu() => CheckAll();

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u12_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
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
                     "UiArenaScreen.cs", "UiArenaSelfCheck.cs", "UiSocialFlow.cs", "UiSocialCodex.cs",
                 })
        {
            if (!File.Exists(Path.Combine(ui, name))) missing.Add(name);
        }

        if (!File.Exists(Path.Combine(net, "VersusEntry.cs"))) missing.Add("Net/VersusEntry.cs");
        if (!File.Exists(Path.Combine(root, "client/MMOGame/Assets/Editor/U12Check.cs")))
        {
            missing.Add("Editor/U12Check.cs");
        }

        Report("files", missing.Count == 0, string.Join(",", missing.ToArray()));
    }

    static void CheckWiring(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Path.Combine(root, "client/MMOGame/Assets/Net");
        var screen = Read(Path.Combine(ui, "UiArenaScreen.cs"));
        var selfCheck = Read(Path.Combine(ui, "UiArenaSelfCheck.cs"));
        var flow = Read(Path.Combine(ui, "UiSocialFlow.cs"));
        var codex = Read(Path.Combine(ui, "UiSocialCodex.cs"));
        var entry = Read(Path.Combine(net, "VersusEntry.cs"));

        Report("net_parse", entry.Contains("VersusEntry") && entry.Contains("ArenaPath")
            && entry.Contains("BattlefieldPath") && entry.Contains("Parse")
            && entry.Contains("SummaryLine") && entry.Contains("ArenaBody")
            && entry.Contains("BattlefieldBody"),
            "Net 层缺进场路径、请求体或拆装");

        Report("flow_versus", flow.Contains("VersusEntry.ArenaPath")
            && flow.Contains("VersusEntry.BattlefieldPath")
            && flow.Contains("VersusEntry.Parse")
            && flow.Contains("EnterArena") && flow.Contains("EnterBattlefield"),
            "Flow 没接 /versus/*");

        Report("screen_wired", screen.Contains("u12_versus_wired=ready")
            && screen.Contains("VersusEntry.SummaryLine")
            && screen.Contains("EnterArena") && screen.Contains("EnterBattlefield")
            && screen.IndexOf("VersusGap", StringComparison.Ordinal) < 0
            && screen.IndexOf("无匹配入口", StringComparison.Ordinal) < 0,
            "屏没接进场回包或缺口文案还在");

        Report("codex_wired", codex.Contains("VersusEnterNote")
            && codex.Contains("ArenaModeIds")
            && codex.IndexOf("VersusGap", StringComparison.Ordinal) < 0,
            "Codex 里还留着缺口文案");

        // 请求参数只放模式 / 场次 / 队伍；战力、系数、匹配分组一律不进客户端。
        Report("no_power_math", screen.IndexOf("1.3", StringComparison.Ordinal) < 0
            && screen.IndexOf("0.7", StringComparison.Ordinal) < 0
            && screen.IndexOf("Matchmaking", StringComparison.Ordinal) < 0
            && selfCheck.IndexOf("1.3", StringComparison.Ordinal) < 0,
            "屏上自己算了战力或匹配");

        Report("no_config", screen.IndexOf("config/", StringComparison.Ordinal) < 0
            && selfCheck.IndexOf("config/", StringComparison.Ordinal) < 0
            && flow.IndexOf("config/", StringComparison.Ordinal) < 0
            && codex.IndexOf("config/", StringComparison.Ordinal) < 0
            && entry.IndexOf("config/", StringComparison.Ordinal) < 0
            && entry.IndexOf("File.ReadAllText", StringComparison.Ordinal) < 0,
            "这张卡读了配置表");

        var character = Read(Path.Combine(root, "proto/mmo/character.proto"));
        var battle = Read(Path.Combine(root, "proto/mmo/battle.proto"));
        Report("no_proto",
            character.IndexOf("u12", StringComparison.OrdinalIgnoreCase) < 0
            && battle.IndexOf("u12", StringComparison.OrdinalIgnoreCase) < 0,
            "这张卡不该动 proto");
    }

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiArenaSelfCheck");
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
            Debug.LogError("u12_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "versus" : "see_u12_self_logs");
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
