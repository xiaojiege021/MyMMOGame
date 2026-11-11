using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// U3 编辑器核对：战斗与副本四屏的静态口径 + 四屏实例断言。
// 实例断言在 HotUpdate 程序集里（autoReferenced:false，这里引不到），用反射调它。
// 这一层只管「数字从哪来、有没有本地判定、既有 HUD 有没有被挪」；真网关那一段在 check-u3.sh。
public static class U3Check
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        CheckFiles(root);
        CheckCodex(root);
        CheckDungeonScope(root);
        CheckNoLocalVerdict(root);
        CheckHudAdditive(root);
        CheckSettleScope(root);
        CheckAssets(root);
        CheckSelf();

        Debug.Log("u3_all=" + (failed ? "fail" : "ok"));
        EditorApplication.Exit(failed ? 1 : 0);
    }

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u3_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
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
        var expected = new[]
        {
            "HotUpdate/Ui/UiBattleCodex.cs", "HotUpdate/Ui/UiDungeonFlow.cs",
            "HotUpdate/Ui/UiDungeonScreen.cs", "HotUpdate/Ui/UiSettleScreen.cs",
            "HotUpdate/Ui/UiPartyScreen.cs", "HotUpdate/Ui/UiBattleSelfCheck.cs",
            "Net/DungeonEntry.cs",
        };

        var missing = string.Empty;
        for (var i = 0; i < expected.Length; i++)
        {
            if (Client(root, expected[i]).Length == 0)
            {
                missing += (missing.Length > 0 ? "," : string.Empty) + Path.GetFileName(expected[i]);
            }
        }

        Report("files", missing.Length == 0, missing.Length == 0 ? expected.Length + " files" : "missing=" + missing);
    }

    // ---------------------------------------------------------------- 展示表对得上配置

    static void CheckCodex(string root)
    {
        var codex = Client(root, "HotUpdate/Ui/UiBattleCodex.cs");
        var dungeonCfg = Repo(root, "config/dungeons.json");
        var expCfg = Repo(root, "config/exp.json");

        // 三条副本：编号、人数上限、扣不扣疲劳、占不占周份额，四样都要与配置表一致。
        var main = dungeonCfg.Contains("\"id\": \"main\"") && dungeonCfg.Contains("\"max_players\": 1")
            && codex.Contains("Id = \"main\"") && codex.Contains("MaxPlayers = 1");
        var abyss = dungeonCfg.Contains("\"id\": \"abyss\"") && dungeonCfg.Contains("\"charges_fatigue\": true")
            && codex.Contains("Id = \"abyss\"") && codex.Contains("ChargesFatigue = true");
        var lair = dungeonCfg.Contains("\"id\": \"lair\"") && dungeonCfg.Contains("\"takes_weekly_share\": true")
            && codex.Contains("Id = \"lair\"") && codex.Contains("TakesWeeklyShare = true");

        Report("codex_dungeons", main && abyss && lair,
            "main=" + main + " abyss=" + abyss + " lair=" + lair);

        // 顶格值：日上限 6000、温泉 1500、周份额 12、组队加成 10%，两边各写一次，必须相等。
        var caps = dungeonCfg.Contains("\"fatigue_daily_cap\": 6000")
            && dungeonCfg.Contains("\"hot_spring_recovery\": 1500")
            && dungeonCfg.Contains("\"weekly_share_cap\": 12")
            && codex.Contains("FatigueDailyCap = 6000")
            && codex.Contains("HotSpringRecovery = 1500")
            && codex.Contains("WeeklyShareCap = 12");
        var exp = expCfg.Contains("\"party_bonus_pct\": 10") && codex.Contains("PartyBonusPct = 10")
            && expCfg.Contains("\"party_min_size\": 2") && codex.Contains("PartyMinSize = 2");
        Report("codex_caps", caps && exp, "caps=" + caps + " exp=" + exp);

        // 配置里没有的副本不许出现在表里（列了就是编数据）。
        var notYet = !codex.Contains("Id = \"dragon\"") && !codex.Contains("Id = \"world_boss\"")
            && !codex.Contains("Id = \"sect\"") && codex.Contains("NotYet");
        Report("codex_not_yet", notYet, "not_yet=" + notYet);
    }

    // ---------------------------------------------------------------- 请求体只有三样

    static void CheckDungeonScope(string root)
    {
        var entry = Client(root, "Net/DungeonEntry.cs");
        var plain = entry.Replace("\\\"", "\"");

        var fields = plain.Contains("\"character_id\":") && plain.Contains("\"dungeon_id\":")
            && plain.Contains("\"party_size\":");
        // 请求里不许带能左右结果的数值：疲劳、份额、经验、掉落都不发。
        var noAnswers = !plain.Contains("\"fatigue\":") && !plain.Contains("\"weekly_share\":")
            && !plain.Contains("\"exp\":") && !plain.Contains("\"damage\":");
        Report("dungeon_scope", fields && noAnswers, "fields=" + fields + " no_answers=" + noAnswers);

        // 进本结果里那一档「被拒」必须来自 allowed 字段，不是从状态码猜。
        var byFlag = entry.Contains("ReadBool(body, \"allowed\")");
        Report("dungeon_allowed_flag", byFlag, byFlag ? "allowed_flag" : "missing");
    }

    // ---------------------------------------------------------------- 没有本地判定

    static void CheckNoLocalVerdict(string root)
    {
        var flow = Client(root, "HotUpdate/Ui/UiDungeonFlow.cs");
        var hud = Client(root, "HotUpdate/BattleHud.cs");
        var party = Client(root, "HotUpdate/Ui/UiPartyScreen.cs");
        var settle = Client(root, "HotUpdate/Ui/UiSettleScreen.cs");

        // 客户端不掷点、不乘系数、不自己算疲劳与份额。
        var noChance = !flow.Contains("Random") && !settle.Contains("Random") && !party.Contains("Random");
        var noDerive = !flow.Contains("FatigueDailyCap -") && !flow.Contains("PartyBonusPct *")
            && !flow.Contains("WeeklyShareCap -") && !flow.Contains("Fatigue -");
        Report("no_local_verdict", noChance && noDerive, "chance=" + noChance + " derive=" + noDerive);

        // 血与蓝只从 BattleClient 的回包字段读，HUD 里没有赋值回写。
        var noWrite = !hud.Contains("BossHp =") && !hud.Contains("CasterHp =") && !hud.Contains("Mana =");
        var readsClient = hud.Contains("_client.BossHp") && hud.Contains("_client.CasterHp")
            && hud.Contains("_client.Mana");
        Report("hud_readonly", noWrite && readsClient, "write=" + noWrite + " read=" + readsClient);

        // 阶段只认广播：不许拿血线去猜阶段。
        var phaseByReply = hud.Contains("UiBattleCodex.PhaseLine(_client.PhaseIndex, _client.PhaseName)");
        var noHpGuess = !hud.Contains("BossHp <") && !hud.Contains("BossHp >") && !hud.Contains("BossHp <=");
        Report("phase_by_reply", phaseByReply && noHpGuess,
            "reply=" + phaseByReply + " hp_guess=" + !noHpGuess);

        // 结算屏不给经验翻倍、不加成：经验只显示回包那个数。
        var expFromReply = settle.Contains("ExpGain") || flow.Contains("ExpGain");
        // 只查乘除：拼文案时 `+ ExpGain +` 这种串联不算运算。
        var noExpMath = !flow.Contains("ExpGain *") && !flow.Contains("ExpGain /");
        Report("settle_exp_reply", expFromReply && noExpMath, "reply=" + expFromReply + " math=" + noExpMath);
    }

    // ---------------------------------------------------------------- 既有 HUD 只做加法

    static void CheckHudAdditive(string root)
    {
        var hud = Client(root, "HotUpdate/BattleHud.cs");

        // 三个按钮矩形一个数都不许动：P0-3c 的真机点选按的就是它们。
        var rects = hud.Contains("const int SkillButtonSize = 200;")
            && hud.Contains("const int SkillButtonRight = 60;")
            && hud.Contains("const int SkillButtonBottom = 60;")
            && hud.Contains("const int CancelButtonHeight = 90;")
            && hud.Contains("new Rect(40f, Screen.height - 170f, 260f, 84f)");
        Report("hud_rects_unchanged", rects, rects ? "rects_same" : "rects_moved");

        // 既有两条“建好了”的日志还在，真机核对靠它们认 HUD。
        var logs = hud.Contains("p02b_hud=ready") && hud.Contains("p03c_hud=ready")
            && hud.Contains("u3_hud=ready");
        Report("hud_logs", logs, logs ? "3 logs" : "log_missing");

        // 新加的三块不进 HitTest：判定里只认那三个按钮矩形。
        var hitStart = hud.IndexOf("public static bool HitTest", StringComparison.Ordinal);
        var hitEnd = hud.IndexOf("static Rect SkillButtonRect", StringComparison.Ordinal);
        var hitBody = hitStart >= 0 && hitEnd > hitStart
            ? hud.Substring(hitStart, hitEnd - hitStart)
            : string.Empty;
        // 判定里只认那三个按钮矩形：三处 .Contains 各对应一个，再多一处就是新块抢了点击。
        var hitClean = hitBody.Length > 0
            && !hitBody.Contains("Party")
            && !hitBody.Contains("_phaseText")
            && !hitBody.Contains("_logText")
            && Count(hitBody, ".Contains(") == 3
            && hitBody.Contains("ClassButtonRect()")
            && hitBody.Contains("SkillButtonRect(i)")
            && hitBody.Contains("CancelButtonRect()");
        Report("hud_hit_unchanged", hitClean, hitClean ? "3 rects only" : "hit_changed");
    }

    // ---------------------------------------------------------------- 结算屏口径

    static void CheckSettleScope(string root)
    {
        var settle = Client(root, "HotUpdate/Ui/UiSettleScreen.cs");
        var codex = Client(root, "HotUpdate/Ui/UiBattleCodex.cs");

        // 掉落不自己造：屏上写的是「以角色服背包回包为准」，不是一张编出来的掉落表。
        var dropByReply = settle.Contains("SettleDropNote") && codex.Contains("掉落以角色服背包回包为准");
        var noDropTable = !settle.Contains("铁矿") && !settle.Contains("强化石 ×");
        Report("settle_drop", dropByReply && noDropTable, "note=" + dropByReply + " table=" + !noDropTable);

        // 结算屏也不许自己写「通关」以外的判定条件。
        var noHpRule = !settle.Contains("BossHp") && !settle.Contains("ExpGain >");
        Report("settle_no_rule", noHpRule, noHpRule ? "no_local_rule" : "local_rule");
    }

    // ---------------------------------------------------------------- 资源与 meta

    static void CheckAssets(string root)
    {
        // 客户端不许同步加载资源，路径也不许硬编码进逻辑。字面量拼出来，免得这条断言自己撞上自己。
        var resources = "Resources" + ".Load";
        var hit = string.Empty;
        var uiDir = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var files = Directory.Exists(uiDir) ? Directory.GetFiles(uiDir, "*.cs") : new string[0];
        for (var i = 0; i < files.Length; i++)
        {
            if (File.ReadAllText(files[i]).Contains(resources))
            {
                hit += (hit.Length > 0 ? "," : string.Empty) + Path.GetFileName(files[i]);
            }
        }

        Report("no_resources_load", hit.Length == 0, hit.Length == 0 ? "clean" : "hit=" + hit);

        // 手写 meta 的 GUID 必须正好 32 位十六进制，多一位会整份忽略。
        var bad = string.Empty;
        var checkedCount = 0;
        for (var i = 0; i < files.Length; i++)
        {
            var meta = files[i] + ".meta";
            if (!File.Exists(meta))
            {
                continue;
            }

            checkedCount++;
            var match = Regex.Match(File.ReadAllText(meta), "^guid: ([0-9a-f]{32})$", RegexOptions.Multiline);
            if (!match.Success)
            {
                bad += (bad.Length > 0 ? "," : string.Empty) + Path.GetFileName(meta);
            }
        }

        Report("meta_guid", bad.Length == 0 && checkedCount >= 15,
            bad.Length == 0 ? checkedCount + " metas" : "bad=" + bad);
    }

    // ---------------------------------------------------------------- 实例自检

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiBattleSelfCheck");
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
            Debug.LogError("u3_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "four_screens" : "see_u3_self_logs");
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
