using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// U11 编辑器核对：F05 接 N12 的 `GET /hero-codex`，九格名字、收集度与被动 bps 只认回包，
/// 图鉴缺口文案已去掉，不编条目、不读配置表、不动 proto。
/// </summary>
public static class U11Check
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

        Debug.Log("u11_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/U11 英灵图鉴自检")]
    static void RunFromMenu() => CheckAll();

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u11_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    // D13 之后客户端 Ui / 战斗 HUD / 表世界拆装层也按域分片：主文件 + 同域目录一起读
    // （与 tools/check 的 src_agg、lib_read.py 同口径，断言文本不变）。
    static string Read(string path) => SplitRead.Of(path);

    static void CheckFiles(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Path.Combine(root, "client/MMOGame/Assets/Net");
        var missing = new List<string>();
        foreach (var name in new[]
                 {
                     "UiHeroCodexScreen.cs", "UiHeroCodexSelfCheck.cs", "UiSocialFlow.cs",
                     "UiSocialCodex.cs",
                 })
        {
            if (!File.Exists(Path.Combine(ui, name))) missing.Add(name);
        }

        if (!File.Exists(Path.Combine(net, "HeroCodexEntry.cs"))) missing.Add("Net/HeroCodexEntry.cs");
        if (!File.Exists(Path.Combine(root, "client/MMOGame/Assets/Editor/U11Check.cs")))
        {
            missing.Add("Editor/U11Check.cs");
        }

        Report("files", missing.Count == 0, string.Join(",", missing.ToArray()));
    }

    static void CheckWiring(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Path.Combine(root, "client/MMOGame/Assets/Net");
        var screen = Read(Path.Combine(ui, "UiHeroCodexScreen.cs"));
        var selfCheck = Read(Path.Combine(ui, "UiHeroCodexSelfCheck.cs"));
        var flow = Read(Path.Combine(ui, "UiSocialFlow.cs"));
        var codex = Read(Path.Combine(ui, "UiSocialCodex.cs"));
        var entry = Read(Path.Combine(net, "HeroCodexEntry.cs"));

        Report("net_parse", entry.Contains("HeroCodexEntry") && entry.Contains("RequestPath")
            && entry.Contains("Parse") && entry.Contains("EntryLine") && entry.Contains("SummaryLine"),
            "Net 层缺图鉴路径或拆装");

        Report("flow_codex", flow.Contains("HeroCodexEntry.RequestPath")
            && flow.Contains("HeroCodexEntry.Parse") && flow.Contains("LoadHeroCodex"),
            "Flow 没接 /hero-codex");

        Report("screen_wired", screen.Contains("u11_codex_wired=ready")
            && screen.Contains("HeroCodexEntry.EntryAt")
            && screen.Contains("HeroCodexEntry.SummaryLine")
            && screen.IndexOf("CodexGap", StringComparison.Ordinal) < 0
            && screen.IndexOf("尚未接服务端", StringComparison.Ordinal) < 0,
            "屏没接图鉴回包或缺口文案还在");

        Report("codex_wired", codex.Contains("CodexNote")
            && codex.IndexOf("CodexGap", StringComparison.Ordinal) < 0
            && codex.IndexOf("CodexNames", StringComparison.Ordinal) < 0,
            "Codex 里还留着缺口文案或本地编的九位");

        // 九位名字不许写进屏：回包里没有的条目不能由客户端补一条。
        // D9 之后自检里的假回包也走文案表，改成查表 + 查引用。
        Report("no_names", screen.IndexOf("乔小满", StringComparison.Ordinal) < 0
            && UiTextRef.In(root, selfCheck, "UiHeroCodexSelfCheck", "乔小满"),
            "屏里写死了名字（或自检没备假回包）");

        Report("no_config", screen.IndexOf("config/", StringComparison.Ordinal) < 0
            && selfCheck.IndexOf("config/", StringComparison.Ordinal) < 0
            && flow.IndexOf("config/", StringComparison.Ordinal) < 0
            && entry.IndexOf("config/", StringComparison.Ordinal) < 0
            && screen.IndexOf("hero_codex", StringComparison.Ordinal) < 0,
            "这张卡读了配置表");

        var character = Read(Path.Combine(root, "proto/mmo/character.proto"));
        var social = Read(Path.Combine(root, "proto/mmo/social.proto"));
        Report("no_proto",
            character.IndexOf("u11", StringComparison.OrdinalIgnoreCase) < 0
            && social.IndexOf("u11", StringComparison.OrdinalIgnoreCase) < 0,
            "这张卡不该动 proto");
    }

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiHeroCodexSelfCheck");
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
            Debug.LogError("u11_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "codex" : "see_u11_self_logs");
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
