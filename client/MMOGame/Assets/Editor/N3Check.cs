using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// N3 编辑器核对：F01 的逐条名单只接 GET /sect/roster，没有回包仍写「尚未请求」。
/// </summary>
public static class N3Check
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

        Debug.Log("n3_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/N3 名册接到 F01")]
    static void RunFromMenu() => CheckAll();

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("n3_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
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
        foreach (var name in new[] { "UiSectScreen.cs", "UiRosterSelfCheck.cs", "UiSocialFlow.cs" })
        {
            if (!File.Exists(Path.Combine(ui, name))) missing.Add(name);
        }

        if (!File.Exists(Path.Combine(net, "SectGuild.cs"))) missing.Add("Net/SectGuild.cs");
        if (!File.Exists(Path.Combine(root, "client/MMOGame/Assets/Editor/N3Check.cs")))
        {
            missing.Add("Editor/N3Check.cs");
        }

        Report("files", missing.Count == 0, string.Join(",", missing.ToArray()));
    }

    static void CheckWiring(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Path.Combine(root, "client/MMOGame/Assets/Net");
        var sect = Read(Path.Combine(ui, "UiSectScreen.cs"));
        var flow = Read(Path.Combine(ui, "UiSocialFlow.cs"));
        var guild = Read(Path.Combine(net, "SectGuild.cs"));

        var parse = guild.Contains("ParseRoster")
            ? guild.Split(new[] { "ParseRoster" }, StringSplitOptions.None)[1]
            : string.Empty;
        var parseBody = parse.Contains("static string U(")
            ? parse.Split(new[] { "static string U(" }, StringSplitOptions.None)[0]
            : parse;

        Report("parse", guild.Contains("struct RosterMember")
            && guild.Contains("RosterLine")
            && parseBody.IndexOf("member_cap", StringComparison.Ordinal) < 0
            && parseBody.IndexOf("seat_cap", StringComparison.Ordinal) < 0
            && parseBody.Contains("character_id")
            && parseBody.Contains("position"),
            "名册拆装读了上限，或没读角色号和职位");

        Report("flow", flow.Contains("SectGuild.ParseRoster")
            && flow.Contains("\"/sect/roster?sect_id=\"")
            && flow.Contains("LoadRoster")
            && flow.Contains("HasRoster"),
            "流程没接名册只读");

        Report("screen", sect.Contains("n3_roster_screen=ready")
            && UiTextRef.In(root, sect, "UiSectScreen", "尚未请求")
            && UiTextRef.In(root, sect, "UiSectScreen", "读取名册")
            && sect.Contains("SectGuild.RosterLine")
            && sect.Contains("flow.Appoint")
            && sect.IndexOf("config/", StringComparison.Ordinal) < 0
            && sect.IndexOf("RosterGap", StringComparison.Ordinal) < 0,
            "屏没接名单，或仍写没有读接口");

        Report("no_config", sect.IndexOf("config/", StringComparison.Ordinal) < 0
            && flow.IndexOf("config/", StringComparison.Ordinal) < 0
            && parseBody.IndexOf("config/", StringComparison.Ordinal) < 0,
            "名册接线读了配置");

        var social = Read(Path.Combine(root, "proto/mmo/social.proto"));
        Report("no_proto", social.IndexOf("n3", StringComparison.OrdinalIgnoreCase) < 0
            && social.Contains("DescribeSectRoster"),
            "这张卡不该改 proto");
    }

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiRosterSelfCheck");
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
            Debug.LogError("n3_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "roster" : "see_n3_self_logs");
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
