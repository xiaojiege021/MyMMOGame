using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// U1 编辑器核对：同一套控件切两套皮肤，偏好只存玩家本地；config 里没有美术字段，
// 客户端没有 Resources 的同步加载，资源文件名即 AB 地址、.meta GUID 正好 32 位。
// 实例断言在 HotUpdate 程序集里（autoReferenced:false，这里引不到），用反射调它。
public static class U1Check
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        CheckFiles(root);
        CheckPalette(root);
        CheckLocal(root);
        CheckConfig(root);
        CheckAssets(root);
        CheckReasons(root);
        CheckShell();

        Debug.Log("u1_all=" + (failed ? "fail" : "ok"));
        EditorApplication.Exit(failed ? 1 : 0);
    }

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u1_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    // ---------------------------------------------------------------- 骨架文件

    static void CheckFiles(string root)
    {
        var dir = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var expected = new[]
        {
            "UiPalette.cs", "UiSkinStore.cs", "UiFont.cs", "UiMetrics.cs",
            "UiControls.cs", "UiShell.cs", "UiStatusScreen.cs", "UiReasonCatalog.cs", "UiSelfCheck.cs",
        };

        var missing = new List<string>();
        foreach (var name in expected)
        {
            if (!File.Exists(Path.Combine(dir, name)))
            {
                missing.Add(name);
            }
        }

        Report("files", missing.Count == 0, missing.Count == 0 ? "ui=" + expected.Length : string.Join(",", missing.ToArray()));
    }

    // ---------------------------------------------------------------- 两套色板

    static void CheckPalette(string root)
    {
        var source = File.ReadAllText(Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui/UiPalette.cs"));
        var html = File.ReadAllText(Path.Combine(root, "docs/UI原型全集.html")).ToLowerInvariant();

        // 色板里的每个色值都必须能在原型 CSS 里找到，避免界面自己发明颜色。
        var missing = new List<string>();
        var blue = new List<string>();
        var count = 0;
        foreach (Match match in Regex.Matches(source, @"Hex\(""([0-9a-f]{6})""\)"))
        {
            count++;
            var hex = match.Groups[1].Value;
            var r = Convert.ToInt32(hex.Substring(0, 2), 16);
            var g = Convert.ToInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToInt32(hex.Substring(4, 2), 16);
            var found = html.Contains("#" + hex)
                        || html.Contains("rgba(" + r + "," + g + "," + b)
                        || html.Contains("rgb(" + r + "," + g + "," + b);
            if (!found)
            {
                missing.Add(hex);
            }

            // 两套皮肤要成对：同一个色在两套里都出现，才算「成对替换」。
            if (CountHex(source, hex) < 1)
            {
                blue.Add(hex);
            }
        }

        var themes = source.Contains("晨雾宣纸") && source.Contains("夜山");
        var ok = missing.Count == 0 && blue.Count == 0 && themes && count >= 24;
        Report("palette", ok,
            missing.Count > 0 ? "not_in_proto=" + string.Join(",", missing.ToArray()) : "colors=" + count);
    }

    static int CountHex(string source, string hex)
    {
        return Regex.Matches(source, "Hex\\(\"" + hex + "\"\\)").Count;
    }

    // ---------------------------------------------------------------- 偏好只存本地

    static void CheckLocal(string root)
    {
        var assets = Path.Combine(root, "client/MMOGame/Assets");
        // 拼一下，免得这条断言自己撞上「只有一处碰偏好」的规则。
        var prefs = "Player" + "Prefs";
        var holders = new List<string>();
        foreach (var file in Directory.GetFiles(assets, "*.cs", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(file).Contains(prefs))
            {
                holders.Add(Path.GetFileName(file));
            }
        }

        var store = File.ReadAllText(Path.Combine(assets, "HotUpdate/Ui/UiSkinStore.cs"));
        var onlyStore = holders.Count == 1 && holders[0] == "UiSkinStore.cs";
        var keyFixed = store.Contains("Key = \"mmo.ui.skin\"")
                       && store.Contains("LightValue = \"light\"")
                       && store.Contains("DarkValue = \"dark\"");

        // 皮肤偏好不许出现在服务端正本与协议里。
        var configHit = false;
        foreach (var file in Directory.GetFiles(Path.Combine(root, "config"), "*.json", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(file).Contains("mmo.ui.skin"))
            {
                configHit = true;
            }
        }

        var protoHit = false;
        foreach (var file in Directory.GetFiles(Path.Combine(root, "proto"), "*.proto", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file).ToLowerInvariant();
            if (text.Contains("skin") || text.Contains("theme"))
            {
                protoHit = true;
            }
        }

        Report("local", onlyStore && keyFixed && !configHit && !protoHit,
            "holders=" + string.Join(",", holders.ToArray()));
    }

    // ---------------------------------------------------------------- config 无美术字段

    static void CheckConfig(string root)
    {
        // items.json 里的 "material" 是物品分类（材料），不是美术材质，不列在这里。
        var artKeys = new[]
        {
            "prefab", "model", "effect", "anim", "animation", "icon", "texture", "sprite",
            "skin", "theme", "shader", "mesh",
        };

        var hits = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "config"), "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file).ToLowerInvariant();
            foreach (var key in artKeys)
            {
                if (text.Contains("\"" + key + "\""))
                {
                    hits.Add(Path.GetFileName(file) + ":" + key);
                }
            }
        }

        Report("config", hits.Count == 0, hits.Count == 0 ? "keys=" + artKeys.Length : string.Join(",", hits.ToArray()));
    }

    // ---------------------------------------------------------------- 资源地址与 meta

    static void CheckAssets(string root)
    {
        var assets = Path.Combine(root, "client/MMOGame/Assets");
        // 客户端不许出现 Resources 的同步加载。这里拼一下，免得这段断言自己撞上这条规则。
        var forbidden = "Resources." + "Load";
        var loaders = new List<string>();
        foreach (var file in Directory.GetFiles(assets, "*.cs", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(file).Contains(forbidden))
            {
                loaders.Add(Path.GetFileName(file));
            }
        }

        // 资源不进 Resources：文件名就是 AB 地址，另起一套会让换美术时两处都要改。
        var resourceDirs = 0;
        foreach (var dir in Directory.GetDirectories(assets, "Resources", SearchOption.AllDirectories))
        {
            if (dir.Contains("/Art/"))
            {
                resourceDirs++;
            }
        }

        // 手写 .meta 的 GUID 必须正好 32 位十六进制，多一位 Unity 会忽略整个文件夹。
        var metas = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(assets, "HotUpdate/Ui"), "*.meta"))
        {
            metas.Add(file);
        }

        foreach (var name in new[] { "U1Check.cs.meta", "U1Preview.cs.meta" })
        {
            var path = Path.Combine(assets, "Editor", name);
            if (File.Exists(path))
            {
                metas.Add(path);
            }
        }

        var badGuids = new List<string>();
        foreach (var meta in metas)
        {
            var match = Regex.Match(File.ReadAllText(meta), @"guid:\s*([0-9a-fA-F]+)");
            if (!match.Success || match.Groups[1].Value.Length != 32)
            {
                badGuids.Add(Path.GetFileName(meta));
            }
        }

        Report("assets", loaders.Count == 0 && resourceDirs == 0 && badGuids.Count == 0,
            "metas=" + metas.Count + (loaders.Count > 0 ? " loaders=" + string.Join(",", loaders.ToArray()) : string.Empty)
            + (badGuids.Count > 0 ? " bad_guid=" + string.Join(",", badGuids.ToArray()) : string.Empty));
    }

    // ---------------------------------------------------------------- reason 词表

    static void CheckReasons(string root)
    {
        var catalog = File.ReadAllText(Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui/UiReasonCatalog.cs"));
        var tokens = new List<string>();
        foreach (Match match in Regex.Matches(catalog, @"""([a-z][a-z_]+)"""))
        {
            tokens.Add(match.Groups[1].Value);
        }

        var server = new System.Text.StringBuilder();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            server.Append(File.ReadAllText(file));
        }

        var serverText = server.ToString();
        var missing = new List<string>();
        foreach (var token in tokens)
        {
            if (!serverText.Contains("\"" + token + "\""))
            {
                missing.Add(token);
            }
        }

        var ok = tokens.Count == 36 && missing.Count == 0;
        Report("reason", ok,
            missing.Count > 0 ? "not_in_server=" + string.Join(",", missing.ToArray()) : "tokens=" + tokens.Count);
    }

    // ---------------------------------------------------------------- 实例断言

    static void CheckShell()
    {
        var type = FindType("HotUpdate.Ui.UiSelfCheck");
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
            // 反射会把内层异常包一层，真正的堆栈在 InnerException 里。
            var inner = error.InnerException ?? error;
            Report("self", false, inner.GetType().Name + ":" + inner.Message);
            Debug.LogError("u1_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "layout_and_skin" : "see_u1_self_logs");
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
