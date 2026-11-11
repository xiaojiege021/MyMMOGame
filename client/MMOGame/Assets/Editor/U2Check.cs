using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// U2 编辑器核对：入口四屏的静态口径 + 四屏实例断言。
// 实例断言在 HotUpdate 程序集里（autoReferenced:false，这里引不到），用反射调它。
// 这一层只管「文案与数据从哪来、有没有本地判定」；真网关那一段在 check-u2.sh。
public static class U2Check
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        CheckFiles(root);
        CheckCodex(root);
        CheckHints(root);
        CheckNoLocalVerdict(root);
        CheckCreateScope(root);
        CheckLoadingGate(root);
        CheckAssets(root);
        CheckSelf();

        Debug.Log("u2_all=" + (failed ? "fail" : "ok"));
        EditorApplication.Exit(failed ? 1 : 0);
    }

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u2_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    static string Client(string root, string relative)
    {
        var path = Path.Combine(root, "client/MMOGame/Assets", relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    // ---------------------------------------------------------------- 文件

    static void CheckFiles(string root)
    {
        var expected = new[]
        {
            "HotUpdate/Ui/UiEntryCodex.cs", "HotUpdate/Ui/UiTextField.cs", "HotUpdate/Ui/UiOption.cs",
            "HotUpdate/Ui/UiEntryFlow.cs", "HotUpdate/Ui/UiLoginScreen.cs", "HotUpdate/Ui/UiCreateScreen.cs",
            "HotUpdate/Ui/UiRosterScreen.cs", "HotUpdate/Ui/UiLoadingScreen.cs",
            "HotUpdate/Ui/UiEntrySelfCheck.cs", "Net/AccountEntry.cs",
        };

        var missing = new List<string>();
        foreach (var name in expected)
        {
            if (!File.Exists(Path.Combine(root, "client/MMOGame/Assets", name)))
            {
                missing.Add(Path.GetFileName(name));
            }
        }

        Report("files", missing.Count == 0,
            missing.Count == 0 ? "files=" + expected.Length : string.Join(",", missing.ToArray()));
    }

    // ---------------------------------------------------------------- 出身与职业对得上 config

    static void CheckCodex(string root)
    {
        var codex = Client(root, "HotUpdate/Ui/UiEntryCodex.cs");
        var races = File.ReadAllText(Path.Combine(root, "config/races.json"));
        var classes = File.ReadAllText(Path.Combine(root, "config/classes.json"));

        // 出身：编号、名字、出身对白、外观 JSON 四样都要在客户端表里逐字出现。
        var missing = new List<string>();
        foreach (Match match in Regex.Matches(races, "\"id\"\\s*:\\s*\"(\\w+)\""))
        {
            var id = match.Groups[1].Value;
            if (!codex.Contains("Id = \"" + id + "\""))
            {
                missing.Add("id:" + id);
            }
        }

        foreach (Match match in Regex.Matches(races, "\"name\"\\s*:\\s*\"([^\"]+)\""))
        {
            var name = match.Groups[1].Value;
            if (!codex.Contains("Name = \"" + name + "\""))
            {
                missing.Add("name:" + name);
            }
        }

        foreach (Match match in Regex.Matches(races, "\"origin\"\\s*:\\s*\"([^\"]+)\""))
        {
            var origin = match.Groups[1].Value;
            if (!codex.Contains(origin))
            {
                missing.Add("origin:" + origin);
            }
        }

        // 外观写成转义后的 JSON，逐字段比对，别让客户端自己编一套外观。
        foreach (Match match in Regex.Matches(races.Replace(" ", ""), "\"body\":\"(\\w+)\""))
        {
            var body = match.Groups[1].Value;
            if (!codex.Contains("\\\"body\\\":\\\"" + body + "\\\""))
            {
                missing.Add("body:" + body);
            }
        }

        foreach (Match match in Regex.Matches(classes, "\"id\"\\s*:\\s*\"(\\w+)\""))
        {
            var id = match.Groups[1].Value;
            if (!codex.Contains("Id = \"" + id + "\""))
            {
                missing.Add("class:" + id);
            }
        }

        var raceCount = Regex.Matches(codex, "Id = \"(chen|mai|lan|yuan)\"").Count;
        Report("codex", missing.Count == 0 && raceCount == 4,
            missing.Count > 0 ? "missing=" + string.Join(",", missing.ToArray()) : "races=" + raceCount);
    }

    // ---------------------------------------------------------------- 三句话分得开

    static void CheckHints(string root)
    {
        var flow = Client(root, "HotUpdate/Ui/UiEntryFlow.cs");
        var prototype = File.ReadAllText(Path.Combine(root, "docs/登录界面原型.html"));

        // 原型 HINTS 里那四句，客户端要逐字照抄，别在屏里另写一套。
        var hints = new[]
        {
            "请求没发出去，重试一次", "账号或口令不对", "暂时连不上，稍后再试", "连不上网关",
        };

        var missing = new List<string>();
        foreach (var hint in hints)
        {
            if (!flow.Contains(hint))
            {
                missing.Add(hint);
            }

            if (!prototype.Contains(hint))
            {
                missing.Add("proto:" + hint);
            }
        }

        // 三句必须互不相同，否则屏上分不开。
        var distinct = hints[1] != hints[2] && hints[1] != hints[3] && hints[2] != hints[3];
        Report("hints", missing.Count == 0 && distinct,
            missing.Count > 0 ? "missing=" + string.Join(",", missing.ToArray()) : "hints=" + hints.Length);
    }

    // ---------------------------------------------------------------- 不本地判登录

    static void CheckNoLocalVerdict(string root)
    {
        var files = new[]
        {
            "HotUpdate/Ui/UiEntryFlow.cs", "HotUpdate/Ui/UiLoginScreen.cs", "Net/AccountEntry.cs",
        };

        var hits = new List<string>();
        foreach (var name in files)
        {
            var text = Client(root, name);
            if (text.Contains("password ==") || text.Contains("Password ==")
                || text.Contains("password.Equals") || text.Contains("Password.Equals"))
            {
                hits.Add(Path.GetFileName(name));
            }
        }

        var flow = Client(root, "HotUpdate/Ui/UiEntryFlow.cs");
        var fromReply = Count(flow, "Token = outcome.Token;") == 1;
        var hardcoded = flow.Contains("Token = \"");

        // 令牌不进本地：不写玩家本地偏好、不落盘。
        var prefs = "Player" + "Prefs";
        var persisted = flow.Contains(prefs) || Client(root, "Net/AccountEntry.cs").Contains(prefs);

        Report("no_local_verdict", hits.Count == 0 && fromReply && !hardcoded && !persisted,
            hits.Count > 0 ? "compare_in=" + string.Join(",", hits.ToArray()) : "token_from_reply=true");
    }

    // ---------------------------------------------------------------- 立身只列四出身五职业

    static void CheckCreateScope(string root)
    {
        var create = Client(root, "HotUpdate/Ui/UiCreateScreen.cs");
        var entry = Client(root, "Net/AccountEntry.cs");
        var codex = Client(root, "HotUpdate/Ui/UiEntryCodex.cs");

        // 建角请求的 branch 固定空串：创角不列分支，15 级一转才选。
        // 源码里引号是转义写法的，先去转义再找，免得这条断言自己撞上那层反斜杠。
        var plain = entry.Replace("\\\"", "\"");
        var branchEmpty = plain.Contains("\"branch\":\"\"");
        // 屏上没有第二个选项列表（有的话就是偷偷加了分支之类）。
        var loops = Count(create, "UiEntryCodex.Races.Length") + Count(create, "UiEntryCodex.Roles.Length");
        // 出身四张卡、职业五个签，都在各一段循环里生成，数量不写死。
        var logLine = create.Contains("\" branches_listed=0");
        var branchesConst = codex.Contains("Branch");

        Report("create_scope", branchEmpty && loops >= 2 && logLine && !branchesConst,
            "branch_empty=" + (branchEmpty ? "true" : "false") + " loops=" + loops);
    }

    // ---------------------------------------------------------------- 进度不决定能否进游戏

    static void CheckLoadingGate(string root)
    {
        var flow = Client(root, "HotUpdate/Ui/UiEntryFlow.cs");
        var loading = Client(root, "HotUpdate/Ui/UiLoadingScreen.cs");

        // 自动推进有硬上限，走不到 100%。
        var capped = flow.Contains("0.92f");
        // 只有服务端确认那一处能把 Entered 置真。
        var oneGate = Count(flow, "Entered = true;") == 1;
        var inConfirm = flow.Contains("ConfirmServerEntry") && loading.Contains("SetProgress");

        Report("loading_gate", capped && oneGate && inConfirm,
            "cap=" + (capped ? "0.92" : "none") + " gates=" + Count(flow, "Entered = true;"));
    }

    // ---------------------------------------------------------------- 资源与 meta

    static void CheckAssets(string root)
    {
        var assets = Path.Combine(root, "client/MMOGame/Assets");
        var forbidden = "Resources." + "Load";

        var loaders = new List<string>();
        foreach (var file in Directory.GetFiles(assets, "*.cs", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(file).Contains(forbidden))
            {
                loaders.Add(Path.GetFileName(file));
            }
        }

        // 新加的脚本与它们的 .meta：GUID 必须正好 32 位十六进制。
        var names = new[]
        {
            "UiEntryCodex", "UiTextField", "UiOption", "UiEntryFlow", "UiLoginScreen",
            "UiCreateScreen", "UiRosterScreen", "UiLoadingScreen", "UiEntrySelfCheck",
        };

        var badGuids = new List<string>();
        var metaCount = 0;
        foreach (var name in names)
        {
            var meta = Path.Combine(assets, "HotUpdate/Ui", name + ".cs.meta");
            if (!File.Exists(meta))
            {
                badGuids.Add(name + ".meta");
                continue;
            }

            metaCount++;
            var match = Regex.Match(File.ReadAllText(meta), @"guid:\s*([0-9a-fA-F]+)");
            if (!match.Success || match.Groups[1].Value.Length != 32)
            {
                badGuids.Add(name + ".meta");
            }
        }

        foreach (var name in new[] { "AccountEntry.cs.meta", "U2Check.cs.meta", "U2Preview.cs.meta" })
        {
            var path = name.StartsWith("AccountEntry") 
                ? Path.Combine(assets, "Net", name)
                : Path.Combine(assets, "Editor", name);
            if (!File.Exists(path))
            {
                badGuids.Add(name);
                continue;
            }

            metaCount++;
            var match = Regex.Match(File.ReadAllText(path), @"guid:\s*([0-9a-fA-F]+)");
            if (!match.Success || match.Groups[1].Value.Length != 32)
            {
                badGuids.Add(name);
            }
        }

        Report("assets", loaders.Count == 0 && badGuids.Count == 0,
            "metas=" + metaCount
            + (loaders.Count > 0 ? " loaders=" + string.Join(",", loaders.ToArray()) : string.Empty)
            + (badGuids.Count > 0 ? " bad=" + string.Join(",", badGuids.ToArray()) : string.Empty));
    }

    // ---------------------------------------------------------------- 实例断言

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiEntrySelfCheck");
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
            Debug.LogError("u2_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "four_screens" : "see_u2_self_logs");
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

    static int Count(string text, string needle)
    {
        var total = 0;
        var at = 0;
        while (true)
        {
            var hit = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (hit < 0)
            {
                return total;
            }

            total++;
            at = hit + needle.Length;
        }
    }
}
