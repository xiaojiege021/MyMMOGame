using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// U4 编辑器核对：表世界五屏的静态口径 + 五屏实例断言。
// 实例断言在 HotUpdate 程序集里（autoReferenced:false，这里引不到），用反射调它。
// 这一层只管「数字从哪来、有没有本地判定、战斗 HUD 有没有被挪」；真网关那一段在 check-u4.sh。
public static class U4Check
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        CheckFiles(root);
        CheckCodexAgainstConfig(root);
        CheckTiers(root);
        CheckNoLocalVerdict(root);
        CheckFlightPullback(root);
        CheckBattleHudUntouched(root);
        CheckSelf();

        Debug.Log("u4_all=" + (failed ? "fail" : "ok"));
        EditorApplication.Exit(failed ? 1 : 0);
    }

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u4_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    static string Client(string root, string relative)
    {
        var path = Path.Combine(root, "client/MMOGame/Assets", relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    static string Repo(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    static int Count(string text, string needle)
    {
        var total = 0;
        var at = 0;
        while (true)
        {
            var index = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (index < 0)
            {
                return total;
            }

            total++;
            at = index + needle.Length;
        }
    }

    // ---------------------------------------------------------------- 文件

    static void CheckFiles(string root)
    {
        var files = new[]
        {
            "Net/WorldEntry.cs",
            "HotUpdate/Ui/UiWorldCodex.cs",
            "HotUpdate/Ui/UiWorldFlow.cs",
            "HotUpdate/Ui/UiWorldHudScreen.cs",
            "HotUpdate/Ui/UiWorldMapScreen.cs",
            "HotUpdate/Ui/UiDialogueScreen.cs",
            "HotUpdate/Ui/UiGatherScreen.cs",
            "HotUpdate/Ui/UiFlightScreen.cs",
            "HotUpdate/Ui/UiWorldSelfCheck.cs",
            "Editor/U4Check.cs",
            "Editor/U4Preview.cs",
        };

        var missing = string.Empty;
        for (var i = 0; i < files.Length; i++)
        {
            if (Client(root, files[i]).Length == 0)
            {
                missing += files[i] + ",";
            }
        }

        Report("files", missing.Length == 0, missing.Length > 0 ? "missing=" + missing : "count=" + files.Length);

        // 手写 meta 的 GUID 必须正好 32 位十六进制，多一位整个文件夹会被忽略。
        var guids = new[]
        {
            "Net/WorldEntry.cs.meta",
            "HotUpdate/Ui/UiWorldCodex.cs.meta",
            "HotUpdate/Ui/UiWorldFlow.cs.meta",
            "HotUpdate/Ui/UiWorldHudScreen.cs.meta",
            "HotUpdate/Ui/UiWorldMapScreen.cs.meta",
            "HotUpdate/Ui/UiDialogueScreen.cs.meta",
            "HotUpdate/Ui/UiGatherScreen.cs.meta",
            "HotUpdate/Ui/UiFlightScreen.cs.meta",
            "HotUpdate/Ui/UiWorldSelfCheck.cs.meta",
            "Editor/U4Check.cs.meta",
            "Editor/U4Preview.cs.meta",
        };

        var bad = string.Empty;
        for (var i = 0; i < guids.Length; i++)
        {
            var text = Client(root, guids[i]);
            var match = Regex.Match(text, "guid: ([0-9a-f]+)");
            if (!match.Success || match.Groups[1].Value.Length != 32)
            {
                bad += guids[i] + ",";
            }
        }

        Report("meta", bad.Length == 0, bad.Length > 0 ? "bad=" + bad : "count=" + guids.Length);
    }

    // ---------------------------------------------------------------- 展示表对配置

    static void CheckCodexAgainstConfig(string root)
    {
        var codex = Client(root, "HotUpdate/Ui/UiWorldCodex.cs");
        var world = Repo(root, "config/world.json");
        var gather = Repo(root, "config/gather.json");
        var flight = Repo(root, "config/flight.json");

        if (codex.Length == 0 || world.Length == 0 || gather.Length == 0 || flight.Length == 0)
        {
            Report("codex_source", false, "file_missing");
            return;
        }

        // 八块地表：每一块都得在 world.json 的 regions 列表里，且都在展示表里。
        var regions = new[]
        {
            "枯岚道", "承露门", "听谣镇", "外环隙", "栖云城", "旧天城区", "焦原", "龙巢门口",
        };
        var regionOk = true;
        for (var i = 0; i < regions.Length; i++)
        {
            regionOk &= world.Contains("\"" + regions[i] + "\"") && codex.Contains("Id = \"" + regions[i] + "\"");
        }

        var regionFiles = Directory.Exists(Path.Combine(root, "config/region"))
            ? Directory.GetFiles(Path.Combine(root, "config/region"), "*.json").Length
            : 0;
        Report("codex_regions", regionOk && regionFiles == regions.Length,
            "regions=" + regions.Length + " files=" + regionFiles);

        // 九位开口的人：顺序与 world.json 的 speakers 一致，名字一个不少。
        var speakers = new[]
        {
            "乔小满", "苏晚织", "阮青", "阿弦", "闻嫂", "孟七", "澄川", "灰生", "周晚棠",
        };
        var speakerOk = true;
        for (var i = 0; i < speakers.Length; i++)
        {
            speakerOk &= world.Contains("\"" + speakers[i] + "\"") && codex.Contains("Name = \"" + speakers[i] + "\"");
        }

        var dialogueFiles = Directory.Exists(Path.Combine(root, "config/dialogue"))
            ? Directory.GetFiles(Path.Combine(root, "config/dialogue"), "*.json").Length
            : 0;
        Report("codex_speakers", speakerOk && dialogueFiles == speakers.Length,
            "speakers=" + speakers.Length + " files=" + dialogueFiles);

        // 四个采集点：编号与配置同数，灵石那个必须标成禁止个人采集。
        var nodes = new[] { "ore_kulan_1", "ore_kulan_2", "herb_tingyao_1", "spirit_qiyun_1" };
        var nodeOk = true;
        for (var i = 0; i < nodes.Length; i++)
        {
            nodeOk &= gather.Contains(nodes[i]) && codex.Contains("Id = \"" + nodes[i] + "\"");
        }

        var spiritForbidden = codex.Contains("Id = \"spirit_qiyun_1\"")
            && Regex.IsMatch(codex, "Id = \"spirit_qiyun_1\"[\\s\\S]{0,200}Forbidden = true");
        Report("codex_nodes", nodeOk && spiritForbidden,
            "nodes=" + nodes.Length + " spirit_forbidden=" + spiritForbidden);

        // 三段境界与能飞的那一段，照 flight.json 抄，不另起名字。
        var realms = new[] { "炼体", "筑基", "结丹" };
        var realmOk = codex.Contains("public const string FlightRealm = \"筑基\"");
        for (var i = 0; i < realms.Length; i++)
        {
            realmOk &= flight.Contains("\"" + realms[i] + "\"" ) && codex.Contains("\"" + realms[i] + "\"");
        }

        Report("codex_realms", realmOk, "realms=" + realms.Length + " flight_realm=" + codex.Contains("筑基"));

        // 第二场文本只许出现在乔小满那一条：别人的第二场不能拿他的台词顶上。
        var secondCount = Regex.Matches(codex, "SecondText = \"[^\"]+\"").Count;
        var secondNextToQiao = Regex.IsMatch(codex, "Name = \"乔小满\"[\\s\\S]{0,400}?SecondText = \"");
        Report("codex_second_line", secondCount == 1 && secondNextToQiao,
            "second_lines=" + secondCount);
    }

    // ---------------------------------------------------------------- 档位

    static void CheckTiers(string root)
    {
        var flow = Client(root, "HotUpdate/Ui/UiWorldFlow.cs");
        var hud = Client(root, "HotUpdate/Ui/UiWorldHudScreen.cs");
        var tiers = Client(root, "Net/WorldPeerTiers.cs");
        var all = flow + hud;

        // 正面：档位只经 WorldPeerTiers 翻译。
        var translated = all.Contains("WorldPeerTiers.For") && all.Contains("AppearanceTier");
        Report("tier_translate", translated, "peer_tiers=" + tiers.Length);

        // 反面：客户端不许自己按距离算档，也不许读两档阈值。
        var noDistance = !all.Contains("Vector3.Distance") && !all.Contains("Mathf.Distance")
            && !all.Contains("tier_near_distance") && !all.Contains("tier_far_distance");
        var noManualCompare = !Regex.IsMatch(all, "Tier\\s*[<>=]\\s*\\d");
        Report("tier_no_distance", noDistance && noManualCompare,
            "distance=" + noDistance + " compare=" + noManualCompare);

        // 三档的名字只有一份，写在展示表里。
        var codex = Client(root, "HotUpdate/Ui/UiWorldCodex.cs");
        var labels = codex.Contains("\"完整模型\"") && codex.Contains("\"简模\"") && codex.Contains("\"只留名字\"");
        Report("tier_labels", labels, "labels=3");
    }

    // ---------------------------------------------------------------- 本地判定

    static void CheckNoLocalVerdict(string root)
    {
        var files = new[]
        {
            "HotUpdate/Ui/UiWorldFlow.cs",
            "HotUpdate/Ui/UiWorldHudScreen.cs",
            "HotUpdate/Ui/UiWorldMapScreen.cs",
            "HotUpdate/Ui/UiDialogueScreen.cs",
            "HotUpdate/Ui/UiGatherScreen.cs",
            "HotUpdate/Ui/UiFlightScreen.cs",
        };

        var all = string.Empty;
        for (var i = 0; i < files.Length; i++)
        {
            all += Client(root, files[i]);
        }

        // 客户端不读配置文件、不掷点：屏上的数只能来自回包。
        // 屏上写「config/flight.json 的 flight_realm」这类注脚只是文案，不算读配置，
        // 所以这里查的是读写 API，不是查 config 这个词。
        var clean = !all.Contains("File.ReadAllText") && !all.Contains("File.Exists")
            && !all.Contains("Directory.GetFiles") && !all.Contains("Directory.Exists")
            && !all.Contains("Random.");
        Report("no_config_read", clean, "chars=" + all.Length);

        // 不许同步加载资源、不许写死资源路径。
        // 这个词拼开写：U1 / U2 的核对按全仓裸词扫，写成字面量会把它们一起打挂（踩过一次）。
        var noResources = !all.Contains("Resources" + ".Load");
        Report("no_resources_load", noResources, string.Empty);

        // 采集与跨区的结果文案只能从回包字段拼出来。
        var gatherFromReply = all.Contains("GatherReply.Ok") && all.Contains("GatherReply.ItemId");
        var regionFromReply = all.Contains("RegionReply.Ok") && all.Contains("RegionReply.RegionId");
        Report("verdict_from_reply", gatherFromReply && regionFromReply,
            "gather=" + gatherFromReply + " region=" + regionFromReply);
    }

    // ---------------------------------------------------------------- 飞行拉回

    static void CheckFlightPullback(string root)
    {
        var flow = Client(root, "HotUpdate/Ui/UiWorldFlow.cs");

        // 画面高度只认回包里的 y：被拒时那是拉回后的地面高度，不许保留本地攒的高度。
        var byReply = flow.Contains("public float ScreenY => HasFly ? FlyReply.Y : 0f");
        var trailByReply = flow.Contains("public bool TrailAllowed => HasFly && FlyReply.TrailAllowed");
        Report("fly_height_from_reply", byReply, "screen_y=" + byReply);
        Report("fly_trail_from_reply", trailByReply, "trail=" + trailByReply);

        // 请求只带 want_fly，客户端带不进 realm / can_fly / 账号。
        var body = flow.Contains("WorldFlight.FlyRequestBody(wantFly)");
        var noForge = !flow.Contains("\"realm\":") && !flow.Contains("\"can_fly\":");
        Report("fly_body_scope", body && noForge, "body=" + body + " forge_free=" + noForge);
    }

    // ---------------------------------------------------------------- 战斗 HUD 不动

    static void CheckBattleHudUntouched(string root)
    {
        var hud = Client(root, "HotUpdate/BattleHud.cs");
        var scene = Client(root, "HotUpdate/BattleScene.cs");

        if (hud.Length == 0)
        {
            Report("battle_hud_untouched", false, "file_missing");
            return;
        }

        // P0-3c 靠这三个矩形与两条日志定位，U4 一行都不许碰。
        var rects = hud.Contains("static Rect ClassButtonRect()")
            && hud.Contains("static Rect SkillButtonRect(int index)")
            && hud.Contains("static Rect CancelButtonRect()");
        var logs = hud.Contains("p02b_hud=") && hud.Contains("p03c_hud=");
        var noWorldUi = !hud.Contains("UiWorld") && !scene.Contains("UiWorld");
        Report("battle_hud_untouched", rects && logs && noWorldUi,
            "rects=" + rects + " logs=" + logs + " no_u4=" + noWorldUi);
    }

    // ---------------------------------------------------------------- 实例断言

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiWorldSelfCheck");
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
            Debug.LogError("u4_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "five_screens" : "see_u4_self_logs");
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
