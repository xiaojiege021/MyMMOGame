using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// U10 编辑器核对：F04 接 N11 的种植与家园币，格子与成熟只认回包，缺口文案已去掉，
/// 不动 proto / 不读配置表。
/// </summary>
public static class U10Check
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

        Debug.Log("u10_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/U10 洞府种植与家园币自检")]
    static void RunFromMenu() => CheckAll();

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u10_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
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
                     "UiHomesteadScreen.cs", "UiHomesteadPlantSelfCheck.cs", "UiSocialFlow.cs",
                     "UiSocialCodex.cs",
                 })
        {
            if (!File.Exists(Path.Combine(ui, name))) missing.Add(name);
        }

        foreach (var name in new[] { "HomesteadPlantEntry.cs", "HomeCoinShopEntry.cs" })
        {
            if (!File.Exists(Path.Combine(net, name))) missing.Add("Net/" + name);
        }

        if (!File.Exists(Path.Combine(root, "client/MMOGame/Assets/Editor/U10Check.cs")))
        {
            missing.Add("Editor/U10Check.cs");
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
        var plant = Read(Path.Combine(net, "HomesteadPlantEntry.cs"));
        var coin = Read(Path.Combine(net, "HomeCoinShopEntry.cs"));

        Report("net_parse", plant.Contains("HomesteadPlantEntry") && plant.Contains("DescribePath")
            && plant.Contains("PlantBody") && plant.Contains("HarvestBody")
            && coin.Contains("HomeCoinShopEntry") && coin.Contains("BuyBody"),
            "Net 层缺种植或商城拆装");

        Report("flow_plants", flow.Contains("HomesteadPlantEntry.Parse")
            && flow.Contains("LoadPlants") && flow.Contains("PlantCrop")
            && flow.Contains("HarvestCrop") && flow.Contains("HomeCoinShopEntry.Parse")
            && flow.Contains("BuyHomeCoinItem"),
            "Flow 没接种植与家园币");

        Report("screen_wired", home.Contains("u10_homestead_plant=ready")
            && home.Contains("HomesteadPlantEntry.PlantInSlot")
            && home.Contains("PlantingNote")
            && home.IndexOf("尚未接服务端", StringComparison.Ordinal) < 0,
            "屏没接种植或缺口文案还在");

        Report("codex_plants", codex.Contains("PlantSlots") && codex.Contains("CropIds")
            && codex.Contains("HomeCoinShopItemIds") && codex.Contains("PlantingNote")
            && codex.IndexOf("PlantingGap", StringComparison.Ordinal) < 0,
            "Codex 缺种植格位 / 作物 / 商城编号");

        Report("no_config", home.IndexOf("config/", StringComparison.Ordinal) < 0
            && flow.IndexOf("config/", StringComparison.Ordinal) < 0
            && plant.IndexOf("config/", StringComparison.Ordinal) < 0
            && coin.IndexOf("config/", StringComparison.Ordinal) < 0,
            "这张卡读了配置表");

        var social = Read(Path.Combine(root, "proto/mmo/social.proto"));
        var character = Read(Path.Combine(root, "proto/mmo/character.proto"));
        Report("no_proto",
            social.IndexOf("u10", StringComparison.OrdinalIgnoreCase) < 0
            && character.IndexOf("u10", StringComparison.OrdinalIgnoreCase) < 0,
            "这张卡不该动 proto");
    }

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiHomesteadPlantSelfCheck");
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
            Debug.LogError("u10_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "plant" : "see_u10_self_logs");
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
