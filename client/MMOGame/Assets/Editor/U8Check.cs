using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// U8 编辑器核对：界面入口接到已有屏、不读 config、不动 proto、
/// HUD 七格不再 Toast 占位、二级入口与返回表世界走 Hub。
/// </summary>
public static class U8Check
{
    static bool failed;

    public static void Run()
    {
        var ok = CheckAll();
        if (!ok)
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
        CheckNoConfig(root);
        CheckSelf();

        Debug.Log("u8_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/U8 界面入口自检")]
    static void RunFromMenu()
    {
        CheckAll();
    }

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u8_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    static void CheckFiles(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var missing = new List<string>();
        foreach (var name in new[]
                 {
                     "UiEntryHub.cs", "UiEntryHubSelfCheck.cs", "UiWorldHudScreen.cs",
                     "UiEntryFlow.cs", "UiSettleScreen.cs",
                 })
        {
            if (!File.Exists(Path.Combine(ui, name)))
            {
                missing.Add(name);
            }
        }

        if (!File.Exists(Path.Combine(root, "client/MMOGame/Assets/Editor/U8Check.cs")))
        {
            missing.Add("Editor/U8Check.cs");
        }

        Report("files", missing.Count == 0, string.Join(",", missing.ToArray()));
    }

    static void CheckWiring(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var hud = Read(Path.Combine(ui, "UiWorldHudScreen.cs"));
        var hub = Read(Path.Combine(ui, "UiEntryHub.cs"));
        var entry = Read(Path.Combine(ui, "UiEntryFlow.cs"));
        var settle = Read(Path.Combine(ui, "UiSettleScreen.cs"));
        var world = Read(Path.Combine(ui, "UiWorldFlow.cs"));

        Report("hud_hub", hud.Contains("UiEntryHub.OpenHudFunction")
            && !hud.Contains("这一格留给后续卡"), "HUD 没接到 Hub 或仍是占位 Toast");

        Report("hub_ready", hub.Contains("ReadyLog = \"u8_entry_hub=ready\"")
            && hub.Contains("OpenUpgrade") && hub.Contains("OpenCommerce")
            && hub.Contains("OpenSocial") && hub.Contains("OpenDungeon")
            && hub.Contains("BackToWorld"), "Hub 缺二级入口或返回");

        Report("entry_gate", entry.Contains("UiEntryHub.GateFromLoading")
            && entry.Contains("UiEntryHub.EnterWorld")
            && entry.Contains("Application.isPlaying")
            && Regex.Matches(entry, @"Entered = true;").Count == 1,
            "载入出口没接到 Hub");

        Report("settle_back", settle.Contains("UiEntryHub.BackToWorld")
            && !settle.Contains("表世界入口等 U4 接上"), "结算返回仍是占位 Toast");

        Report("enter_done", world.Contains("SubmitEnter(string regionId, Action done = null)"),
            "进分线没给载入闸门回调");

        Report("no_toast_placeholder", !hub.Contains("这一格留给后续卡")
            && !hub.Contains("表世界入口等 U4 接上"), "Hub 里不该再有旧占位文案");
    }

    static void CheckNoConfig(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var hub = Read(Path.Combine(ui, "UiEntryHub.cs"));
        var self = Read(Path.Combine(ui, "UiEntryHubSelfCheck.cs"));
        Report("no_config", hub.IndexOf("config/", StringComparison.Ordinal) < 0
            && self.IndexOf("config/", StringComparison.Ordinal) < 0
            && hub.IndexOf("JsonField.Of", StringComparison.Ordinal) < 0,
            "入口卡读了配置或自己拆 JSON");

        var social = Read(Path.Combine(root, "proto/mmo/social.proto"));
        var character = Read(Path.Combine(root, "proto/mmo/character.proto"));
        var world = Read(Path.Combine(root, "proto/mmo/world.proto"));
        Report("no_proto", social.IndexOf("u8", StringComparison.OrdinalIgnoreCase) < 0
            && character.IndexOf("u8", StringComparison.OrdinalIgnoreCase) < 0
            && world.IndexOf("u8", StringComparison.OrdinalIgnoreCase) < 0,
            "这张卡不该动 proto");
    }

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiEntryHubSelfCheck");
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
            Debug.LogError("u8_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "entry_hub" : "see_u8_self_logs");
    }

    static Type FindType(string name)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (var i = 0; i < assemblies.Length; i++)
        {
            var type = assemblies[i].GetType(name);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }
}
