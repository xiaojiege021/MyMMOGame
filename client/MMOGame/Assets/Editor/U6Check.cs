using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// U6 编辑器核对：四屏齐全、界面不读配置、不本地算税与价、四屏都能建，
/// 扣款 / 税 / 净额 / 绑定 / 重放都只读回包。
/// </summary>
public static class U6Check
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
        CheckScreens(root);
        CheckFlow(root);
        CheckSelf();

        Debug.Log("u6_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/U6 四屏自检")]
    static void RunFromMenu()
    {
        CheckAll();
    }

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u6_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    static void CheckFiles(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Path.Combine(root, "client/MMOGame/Assets/Net");
        var names = new[]
        {
            "UiCommerceFlow.cs", "UiCommerceCodex.cs", "UiCommerceSelfCheck.cs",
            "UiShopScreen.cs", "UiAuctionScreen.cs", "UiCurrencyScreen.cs", "UiPassScreen.cs",
        };
        var missing = new List<string>();
        foreach (var name in names)
        {
            if (!File.Exists(Path.Combine(ui, name)))
            {
                missing.Add(name);
            }
        }

        foreach (var name in new[] { "CharacterShop.cs", "CharacterCurrency.cs", "CharacterRecharge.cs" })
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
        var screens = new[] { "UiShopScreen.cs", "UiAuctionScreen.cs", "UiCurrencyScreen.cs", "UiPassScreen.cs" };
        var missingLog = new List<string>();
        var readsConfig = new List<string>();
        var localMath = new List<string>();
        foreach (var name in screens)
        {
            var text = Read(Path.Combine(ui, name));
            if (text.IndexOf("ReadyLog = \"u6_", StringComparison.Ordinal) < 0)
            {
                missingLog.Add(name);
            }

            if (text.IndexOf("config/", StringComparison.Ordinal) >= 0
                || text.IndexOf("\"balances\"", StringComparison.Ordinal) >= 0)
            {
                readsConfig.Add(name);
            }

            // 本地算税的痕迹：税率字面量或乘税率。
            if (text.IndexOf("tax_rate", StringComparison.Ordinal) >= 0
                || text.IndexOf("0.05", StringComparison.Ordinal) >= 0
                || text.IndexOf("0.08", StringComparison.Ordinal) >= 0)
            {
                localMath.Add(name);
            }
        }

        Report("screens", missingLog.Count == 0, "缺 ReadyLog: " + string.Join(",", missingLog.ToArray()));
        Report("no_config", readsConfig.Count == 0, "读了配置或自己拆 JSON: " + string.Join(",", readsConfig.ToArray()));
        Report("no_tax_math", localMath.Count == 0, "本地算税: " + string.Join(",", localMath.ToArray()));

        var shop = Read(Path.Combine(ui, "UiShopScreen.cs"));
        var auction = Read(Path.Combine(ui, "UiAuctionScreen.cs"));
        var currency = Read(Path.Combine(ui, "UiCurrencyScreen.cs"));
        Report("shop_fields", shop.Contains(".ChargedYuanbao") && shop.Contains(".Delivered")
            && shop.Contains(".Bound"), "商城屏没读回包字段");
        Report("auction_fields", auction.Contains(".TaxGold") && auction.Contains(".NetGold"),
            "拍卖屏没读回包字段");
        Report("currency_fields", currency.Contains(".Balance") && currency.Contains(".Duplicate"),
            "货币屏没读回包字段");
    }

    static void CheckFlow(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var flow = Read(Path.Combine(ui, "UiCommerceFlow.cs"));
        Report("flow_net", flow.Contains("CharacterShop.Parse") && flow.Contains("CharacterCurrency.Parse")
            && flow.Contains("CharacterAuction.Parse") && flow.Contains("CharacterRecharge.ParseOrder")
            && flow.Contains("CharacterRecharge.ParseSettle"), "流程没复用 Mmo.Net 的拆装");

        var proto = Read(Path.Combine(root, "proto/mmo/character.proto"));
        Report("no_proto", proto.IndexOf("u6", StringComparison.OrdinalIgnoreCase) < 0,
            "这张卡不该动 proto");
    }

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiCommerceSelfCheck");
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
            Debug.LogError("u6_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "four_screens" : "see_u6_self_logs");
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
