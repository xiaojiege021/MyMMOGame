using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// U5 编辑器核对：七屏文件齐全、界面不读配置、不本地算概率 / 费用、七屏都能建，
/// 回包字段原样上屏（被拒时未生效的数值是 0）。实例断言在 HotUpdate 程序集里
/// （autoReferenced:false，这里引不到），用反射调它。
/// </summary>
public static class U5Check
{
    static bool failed;

    public static void Run()
    {
        var ok = CheckAll();

        // 通过就交给批处理的 -quit；失败才显式退出。
        if (!ok)
        {
            EditorApplication.Exit(1);
        }
    }

    /// <summary>跑完全部检查并返回是否全过，不退出编辑器。</summary>
    public static bool CheckAll()
    {
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        CheckFiles(root);
        CheckScreens(root);
        CheckFlow(root);
        CheckNoLocalMath(root);
        CheckSelf();

        Debug.Log("u5_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/U5 七屏自检")]
    static void RunFromMenu()
    {
        CheckAll();
    }

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u5_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    static void CheckFiles(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var names = new[]
        {
            "UiUpgradeFlow.cs", "UiUpgradeCodex.cs", "UiUpgradeViews.cs", "UiUpgradeSelfCheck.cs",
            "UiCharacterScreen.cs", "UiBagScreen.cs", "UiEnhanceScreen.cs", "UiTransferScreen.cs",
            "UiCrystalScreen.cs", "UiSkillScreen.cs", "UiRealmScreen.cs",
        };
        var missing = new List<string>();
        foreach (var name in names)
        {
            if (!File.Exists(Path.Combine(ui, name)))
            {
                missing.Add(name);
            }
        }

        var bag = Path.Combine(root, "client/MMOGame/Assets/Net/CharacterBag.cs");
        if (!File.Exists(bag))
        {
            missing.Add("Net/CharacterBag.cs");
        }

        Report("files", missing.Count == 0, string.Join(",", missing.ToArray()));
    }

    static void CheckScreens(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var screens = new[]
        {
            "UiCharacterScreen.cs", "UiBagScreen.cs", "UiEnhanceScreen.cs", "UiTransferScreen.cs",
            "UiCrystalScreen.cs", "UiSkillScreen.cs", "UiRealmScreen.cs",
        };

        var missingLog = new List<string>();
        var readsConfig = new List<string>();
        foreach (var name in screens)
        {
            var text = Read(Path.Combine(ui, name));
            if (text.IndexOf("ReadyLog = \"u5_", StringComparison.Ordinal) < 0)
            {
                missingLog.Add(name);
            }

            if (text.IndexOf("config/", StringComparison.Ordinal) >= 0
                || text.IndexOf("\"slots\"", StringComparison.Ordinal) >= 0)
            {
                readsConfig.Add(name);
            }
        }

        Report("screens", missingLog.Count == 0, "缺 ReadyLog: " + string.Join(",", missingLog.ToArray()));
        Report("no_config", readsConfig.Count == 0, "读了配置或自己拆 JSON: " + string.Join(",", readsConfig.ToArray()));

        // 三个结果屏必须把回包字段原样上屏，证明不是本地算的。
        var enhance = Read(Path.Combine(ui, "UiEnhanceScreen.cs"));
        var transfer = Read(Path.Combine(ui, "UiTransferScreen.cs"));
        var crystal = Read(Path.Combine(ui, "UiCrystalScreen.cs"));
        Report("enhance_fields", enhance.Contains(".LevelBefore") && enhance.Contains(".GoldCost")
            && enhance.Contains(".StoneCost"), "强化屏没读回包字段");
        Report("transfer_fields", transfer.Contains(".FromLevel") && transfer.Contains(".GoldCost"),
            "转移屏没读回包字段");
        Report("crystal_fields", crystal.Contains(".Filled") && crystal.Contains(".EnhanceLevel")
            && crystal.Contains(".Bound"), "结晶屏没读回包字段");
    }

    static void CheckFlow(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var flow = Read(Path.Combine(ui, "UiUpgradeFlow.cs"));
        Report("flow_net",
            flow.Contains("CharacterBag.Parse") && flow.Contains("CharacterEnhance.Parse")
            && flow.Contains("CharacterTransfer.Parse") && flow.Contains("CharacterCrystal.Parse"),
            "流程没复用 Mmo.Net 的拆装");

        var proto = Read(Path.Combine(root, "proto/mmo/character.proto"));
        Report("no_proto", proto.IndexOf("unbind", StringComparison.OrdinalIgnoreCase) >= 0
            && proto.IndexOf("u5", StringComparison.OrdinalIgnoreCase) < 0,
            "这张卡不该动 proto");
    }

    static void CheckNoLocalMath(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var names = new[]
        {
            "UiCharacterScreen.cs", "UiBagScreen.cs", "UiEnhanceScreen.cs", "UiTransferScreen.cs",
            "UiCrystalScreen.cs", "UiSkillScreen.cs", "UiRealmScreen.cs",
        };
        var bad = new List<string>();
        foreach (var name in names)
        {
            var text = Read(Path.Combine(ui, name));
            // 只看「配置键」与「成功率」，别拿普遍存在的 * 0.5f 这类排版常量误伤。
            if (text.IndexOf("success_rate", StringComparison.Ordinal) >= 0
                || text.IndexOf("gold_cost", StringComparison.Ordinal) >= 0)
            {
                bad.Add(name);
            }
        }

        Report("no_local_math", bad.Count == 0, string.Join(",", bad.ToArray()));
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
            Debug.LogError("u5_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "seven_screens" : "see_u5_self_logs");
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
