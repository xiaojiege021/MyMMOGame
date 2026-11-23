using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// U7 编辑器核对：五屏齐全、界面不读配置、不写死名册上限与席位数字、
/// 竞技场与战场不做本地战力乘法、流程复用 Mmo.Net 的拆装、这张卡不动 proto。
/// </summary>
public static class U7Check
{
    static bool failed;

    static readonly string[] Screens =
    {
        "UiSectScreen.cs", "UiSiegeScreen.cs", "UiArenaScreen.cs",
        "UiHomesteadScreen.cs", "UiHeroCodexScreen.cs",
    };

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
        CheckScreens(root);
        CheckFlow(root);
        CheckSelf();

        Debug.Log("u7_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/U7 社交与组织自检")]
    static void RunFromMenu()
    {
        CheckAll();
    }

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u7_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    // D13 之后客户端 Ui / 战斗 HUD / 表世界拆装层也按域分片：主文件 + 同域目录一起读
    // （与 tools/check 的 src_agg、lib_read.py 同口径，断言文本不变）。
    static string Read(string path) => SplitRead.Of(path);

    static void CheckFiles(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Path.Combine(root, "client/MMOGame/Assets/Net");

        var names = new List<string>(Screens);
        names.AddRange(new[]
        {
            "UiSocialFlow.cs", "UiSocialCodex.cs", "UiSocialSelfCheck.cs",
        });

        var missing = new List<string>();
        foreach (var name in names)
        {
            if (!File.Exists(Path.Combine(ui, name)))
            {
                missing.Add(name);
            }
        }

        foreach (var name in new[] { "SectGuild.cs", "WorldBossEntry.cs" })
        {
            if (!File.Exists(Path.Combine(net, name)))
            {
                missing.Add("Net/" + name);
            }
        }

        Report("files", missing.Count == 0, string.Join(",", missing.ToArray()));
    }

    static void CheckScreens(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");

        var missingLog = new List<string>();
        var readsConfig = new List<string>();
        var hardcodedCap = new List<string>();
        var powerMath = new List<string>();

        foreach (var name in Screens)
        {
            var text = Read(Path.Combine(ui, name));
            if (text.IndexOf("ReadyLog = \"u7_", StringComparison.Ordinal) < 0)
            {
                missingLog.Add(name);
            }

            // 界面不许自己去读 config/ 里的表，也不许自己拆 JSON。
            if (text.IndexOf("config/", StringComparison.Ordinal) >= 0
                || text.IndexOf("sect.json", StringComparison.Ordinal) >= 0
                || text.IndexOf("siege.json", StringComparison.Ordinal) >= 0
                || text.IndexOf("JsonField.Of", StringComparison.Ordinal) >= 0)
            {
                readsConfig.Add(name);
            }

            // 名册上限与席位数字只许从回包里来，不许在屏上赋值一个常量。
            if (Regex.IsMatch(text, @"(MemberCap|SeatCap|MemberCount|SeatCount|FullModelLimit)\s*=\s*\d"))
            {
                hardcodedCap.Add(name);
            }

            if (text.IndexOf("1.3", StringComparison.Ordinal) >= 0
                || text.IndexOf("0.7", StringComparison.Ordinal) >= 0)
            {
                powerMath.Add(name);
            }
        }

        Report("screens", missingLog.Count == 0, "缺 ReadyLog: " + string.Join(",", missingLog.ToArray()));
        Report("no_config", readsConfig.Count == 0, "读了配置或自己拆 JSON: " + string.Join(",", readsConfig.ToArray()));
        Report("no_hardcoded_cap", hardcodedCap.Count == 0,
            "写死了上限或席位: " + string.Join(",", hardcodedCap.ToArray()));
        Report("no_power_math", powerMath.Count == 0, "本地乘了平衡系数: " + string.Join(",", powerMath.ToArray()));

        var codex = Read(Path.Combine(ui, "UiSocialCodex.cs"));
        Report("codex_clean", codex.Length > 0
            && codex.IndexOf("config/", StringComparison.Ordinal) < 0
            && !Regex.IsMatch(codex, @"(MemberCap|SeatCap|MemberCount|SeatCount)\s*=\s*\d"),
            "文案里带了配置值");

        var sect = Read(Path.Combine(ui, "UiSectScreen.cs"));
        Report("sect_fields", sect.Contains(".MemberCap") && sect.Contains(".SeatCap")
            && sect.Contains(".MemberCount") && sect.Contains(".SeatCount"),
            "宗门屏没读回包字段");

        var siege = Read(Path.Combine(ui, "UiSiegeScreen.cs"));
        Report("siege_fields", siege.Contains(".SiegeFullCount") && siege.Contains(".SiegeFullLimit")
            && siege.Contains("Markers()"), "城战屏没读档位字段");

        var homestead = Read(Path.Combine(ui, "UiHomesteadScreen.cs"));
        var hero = Read(Path.Combine(ui, "UiHeroCodexScreen.cs"));
        var arenaScreen = Read(Path.Combine(ui, "UiArenaScreen.cs"));
        // U9 接了摆放口、U10 接了种植与家园币、U11 接了图鉴、U12 接了竞技场与战场进场。
        // 这里只认「接线后的入口在不在」，不写「已接 / 未接」这种会过期的话。
        Report("wired_entries",
            homestead.Contains("读取种植") && hero.Contains("读取图鉴")
            && arenaScreen.Contains("进竞技场") && arenaScreen.Contains("EnterBattlefield"),
            "种植块、图鉴块或进场口没接回包");
    }

    static void CheckFlow(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var flow = Read(Path.Combine(ui, "UiSocialFlow.cs"));
        Report("flow_net", flow.Contains("SectGuild.ParseSect") && flow.Contains("SectGuild.ParsePosition")
            && flow.Contains("SectGuild.ParseLeadership") && flow.Contains("SectGuild.ParseUpgrade")
            && flow.Contains("WorldBossEntry.Parse"), "流程没复用 Mmo.Net 的拆装");

        Report("flow_siege", flow.Contains("SiegeViewTiers.For"), "城战档位没走 SiegeViewTiers");

        var social = Read(Path.Combine(root, "proto/mmo/social.proto"));
        var battle = Read(Path.Combine(root, "proto/mmo/battle.proto"));
        Report("no_proto", social.IndexOf("u7", StringComparison.OrdinalIgnoreCase) < 0
            && battle.IndexOf("u7", StringComparison.OrdinalIgnoreCase) < 0, "这张卡不该动 proto");
    }

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiSocialSelfCheck");
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
            Debug.LogError("u7_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "five_screens" : "see_u7_self_logs");
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
