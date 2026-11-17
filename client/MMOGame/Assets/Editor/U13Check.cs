using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// U13 编辑器核对：U5 境界屏接 N13 的两条口（只读段内阶与被动、升一阶只发角色号），
/// 阶名与被动只认回包、门槛不本地判、客户端没有第二份阶名表，也不动 proto / 配置。
/// 实例断言在 HotUpdate 程序集里（autoReferenced:false，这里引不到），用反射调它。
/// </summary>
public static class U13Check
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
        CheckRealmWiring(root);
        CheckNoLocalGate(root);
        CheckNoProto(root);
        CheckSelf();

        Debug.Log("u13_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/U13 段内阶自检")]
    static void RunFromMenu() => CheckAll();

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u13_" + key + "=" + (ok ? "ok" : "fail")
            + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    static void CheckFiles(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Path.Combine(root, "client/MMOGame/Assets/Net");
        var missing = new List<string>();
        foreach (var name in new[] { "UiRealmScreen.cs", "UiUpgradeFlow.cs", "UiUpgradeSelfCheck.cs" })
        {
            if (!File.Exists(Path.Combine(ui, name)))
            {
                missing.Add(name);
            }
        }

        foreach (var name in new[] { "CharacterRealmPhase.cs", "CharacterBag.cs" })
        {
            if (!File.Exists(Path.Combine(net, name)))
            {
                missing.Add(name);
            }
        }

        Report("files", missing.Count == 0, string.Join(",", missing.ToArray()));

        var netText = Read(Path.Combine(net, "CharacterRealmPhase.cs"));
        Report("net_parse",
            netText.Contains("ReadQuery") && netText.Contains("PromoteBody")
            && netText.Contains("Parse") && netText.Contains("PhaseLine")
            && netText.Contains("PromoteLine") && netText.Contains("Unescape"),
            "Net 层缺路径 / 拆装 / 排版件");
        Report("net_paths",
            netText.Contains("\"/realm/phase\"") && netText.Contains("\"/realm/phase/promote\""),
            "两条口的路径不是 N13 的那两条");
        Report("net_body_no_txn",
            netText.IndexOf("txn", StringComparison.OrdinalIgnoreCase) < 0,
            "请求体里出现了事务号");
    }

    static void CheckRealmWiring(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var screen = Read(Path.Combine(ui, "UiRealmScreen.cs"));
        var flow = Read(Path.Combine(ui, "UiUpgradeFlow.cs"));
        var codex = Read(Path.Combine(ui, "UiUpgradeCodex.cs"));

        Report("flow_actions",
            flow.Contains("LoadRealmPhase") && flow.Contains("PromoteRealmPhase")
            && flow.Contains("CharacterRealmPhase.Parse")
            && flow.Contains("CharacterRealmPhase.ReadQuery")
            && flow.Contains("CharacterRealmPhase.PromoteBody"),
            "流程层没接两条口或没复用 Net 拆装");

        Report("screen_wired",
            screen.Contains("CharacterRealmPhase.PhaseLine")
            && screen.Contains("CharacterRealmPhase.PromoteLine")
            && screen.Contains("CharacterRealmPhase.RequiredLine")
            && screen.Contains("LoadRealmPhase") && screen.Contains("PromoteRealmPhase"),
            "境界屏没读回包或没按钮");

        // U5 原来那张写死 11 个境界名的阶梯表已撤，改由回包给。
        Report("no_static_ladder",
            !codex.Contains("Realms") && !screen.Contains("UiUpgradeCodex.Realms"),
            "静态境界阶梯表还在");

        // 大境界突破仍是后面的卡：屏上不许冒出一个「突破」按钮去调不存在的口。
        Report("no_advance_button",
            !screen.Contains("\"尝试突破\"") && !screen.Contains("AdvanceRealm"),
            "本卡不该做下一大境界突破");
    }

    static void CheckNoLocalGate(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Path.Combine(root, "client/MMOGame/Assets/Net");
        var names = new[]
        {
            Path.Combine(ui, "UiRealmScreen.cs"), Path.Combine(ui, "UiUpgradeFlow.cs"),
            Path.Combine(ui, "UiUpgradeSelfCheck.cs"),
            Path.Combine(net, "CharacterRealmPhase.cs"),
        };

        var bad = new List<string>();
        foreach (var path in names)
        {
            var text = Read(path);
            if (text.IndexOf("config/", StringComparison.Ordinal) >= 0
                || text.IndexOf("sub_phases", StringComparison.Ordinal) >= 0
                || text.IndexOf("promote_min_level", StringComparison.Ordinal) >= 0
                || text.IndexOf("realms.json", StringComparison.Ordinal) >= 0)
            {
                bad.Add(Path.GetFileName(path));
            }
        }

        // UiUpgradeCodex 里留着 U5 强化档位的旧注释（提到 enhance 那份配置），
        // 这里只查境界配置的键，不拿「config/」这个词误伤它。
        var codex = Read(Path.Combine(ui, "UiUpgradeCodex.cs"));
        if (codex.IndexOf("sub_phases", StringComparison.Ordinal) >= 0
            || codex.IndexOf("promote_min_level", StringComparison.Ordinal) >= 0
            || codex.IndexOf("realms.json", StringComparison.Ordinal) >= 0)
        {
            bad.Add("UiUpgradeCodex.cs");
        }

        Report("no_config", bad.Count == 0, string.Join(",", bad.ToArray()));

        // 客户端不许有第二份阶名表：两个四阶名与炼体九阶名一律不写。
        var hot = Read(Path.Combine(ui, "UiRealmScreen.cs"))
            + Read(Path.Combine(ui, "UiUpgradeFlow.cs"))
            + Read(Path.Combine(ui, "UiUpgradeCodex.cs"))
            + Read(Path.Combine(ui, "UiUpgradeSelfCheck.cs"))
            + Read(Path.Combine(net, "CharacterRealmPhase.cs"));
        Report("no_phase_table",
            hot.IndexOf("大圆满", StringComparison.Ordinal) < 0
            && hot.IndexOf("初期", StringComparison.Ordinal) < 0
            && hot.IndexOf("九阶", StringComparison.Ordinal) < 0,
            "客户端出现了阶名表");
    }

    static void CheckNoProto(string root)
    {
        var proto = Read(Path.Combine(root, "proto/mmo/character.proto"));
        Report("no_proto",
            proto.Contains("realm_phase")
            && proto.IndexOf("u13", StringComparison.OrdinalIgnoreCase) < 0,
            "这张卡不该动 proto");
    }

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiUpgradeSelfCheck");
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
            Debug.LogError("u13_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "realm_phase_promote" : "see_u5_self_logs");
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
