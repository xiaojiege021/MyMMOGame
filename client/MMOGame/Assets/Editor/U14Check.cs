using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// U14–U17 编辑器核对：四屏只读各自回包（队伍七条口 / LootAssigned / deserter_remaining_s /
/// worldboss），客户端不掷点、不算时间、不折时区、不读配置、不动 proto。
/// 实例断言在 HotUpdate 程序集里（autoReferenced:false，这里引不到），用反射调它。
/// </summary>
public static class U14Check
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
        CheckParty(root);
        CheckLoot(root);
        CheckReconnect(root);
        CheckBoss(root);
        CheckSelf();

        Debug.Log("u14_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/U14 四屏自检")]
    static void RunFromMenu() => CheckAll();

    static void Report(string key, bool ok, string detail)
    {
        Debug.Log("u14_" + key + "=" + (ok ? "ok" : "fail")
            + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    static void CheckFiles(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Path.Combine(root, "client/MMOGame/Assets/Net");
        var missing = new List<string>();
        foreach (var name in new[]
        {
            "UiPartyRoomScreen.cs", "UiPartyFlow.cs", "UiPartyCodex.cs",
            "UiLootScreen.cs", "UiLootFlow.cs", "UiLootCodex.cs",
            "UiReconnectScreen.cs", "UiReconnectFlow.cs", "UiReconnectCodex.cs",
            "UiWorldBossScreen.cs", "UiU14SelfCheck.cs",
        })
        {
            if (!File.Exists(Path.Combine(ui, name)))
            {
                missing.Add(name);
            }
        }

        if (!File.Exists(Path.Combine(net, "CharacterParty.cs")))
        {
            missing.Add("CharacterParty.cs");
        }

        Report("files", missing.Count == 0, string.Join(",", missing.ToArray()));

        var hub = Read(Path.Combine(ui, "UiEntryHub.cs"));
        Report("wired_entries",
            hub.Contains("UiPartyRoomScreen.Build") && hub.Contains("UiLootScreen.Build")
            && hub.Contains("UiReconnectScreen.Build") && hub.Contains("UiWorldBossScreen.Build"),
            "四屏没挂进入口导航");
    }

    static void CheckParty(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var net = Read(Path.Combine(root, "client/MMOGame/Assets/Net/CharacterParty.cs"));
        var flow = Read(Path.Combine(ui, "UiPartyFlow.cs"));
        var screen = Read(Path.Combine(ui, "UiPartyRoomScreen.cs"));

        Report("party_paths",
            net.Contains("\"/party\"") && net.Contains("\"/party/invite\"")
            && net.Contains("\"/party/respond\"") && net.Contains("\"/party/ready/start\"")
            && net.Contains("\"/party/ready\"") && net.Contains("\"/party/leave\"")
            && net.Contains("\"/party/disband\""),
            "队伍七条口的路径不全");

        Report("party_no_txn", net.IndexOf("txn", StringComparison.OrdinalIgnoreCase) < 0,
            "请求体里出现了事务号");

        Report("party_flow_net",
            flow.Contains("CharacterParty.Parse") && flow.Contains("CharacterParty.DescribeQuery")
            && flow.Contains("CharacterParty.InviteBody") && flow.Contains("CharacterParty.RespondBody")
            && flow.Contains("CharacterParty.ReadyBody") && flow.Contains("CharacterParty.MemberBody"),
            "流程层没复用 Net 拆装");

        // 没回包不许自己凑一队：屏上只能读回包里的 Members，不能往里塞人。
        Report("party_no_self_member",
            screen.IndexOf("Members.Add", StringComparison.Ordinal) < 0
            && screen.IndexOf("Add(new PartyMemberFactView", StringComparison.Ordinal) < 0
            && screen.Contains("UiPartyCodex.EmptyList"),
            "屏上自己造了队员");

        var gateway = Read(Path.Combine(root, "src/Gateway/PartyApi.cs"));
        Report("party_gateway_loot_facts",
            gateway.Contains("loot_mode = reply.LootMode") && gateway.Contains("roll_max = reply.RollMax"),
            "网关 /party 回包没带分配口径（U14 要照抄这一格）");
    }

    static void CheckLoot(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var codex = Read(Path.Combine(ui, "UiLootCodex.cs"));
        var flow = Read(Path.Combine(ui, "UiLootFlow.cs"));
        var screen = Read(Path.Combine(ui, "UiLootScreen.cs"));
        var client = Read(Path.Combine(root, "client/MMOGame/Assets/HotUpdate/BattleClient.cs"));

        Report("loot_three_words",
            codex.Contains("\"need\"") && codex.Contains("\"greed\"") && codex.Contains("\"pass\"")
            && codex.Contains("Nobody"),
            "三个词或「没人要」不见了");

        // 点数不在客户端：不许随机、不许掷、不许算区间。
        var math = codex + flow + screen;
        Report("loot_no_roll_math",
            math.IndexOf("Random", StringComparison.Ordinal) < 0
            && math.IndexOf("Roll(", StringComparison.Ordinal) < 0
            && math.IndexOf("roll_", StringComparison.OrdinalIgnoreCase) < 0,
            "客户端出现了掷点 / 随机");

        Report("loot_send_path",
            flow.Contains("SendLootIntentAsync") && flow.Contains("UiLootCodex.Need")
            && flow.Contains("UiLootCodex.Greed") && flow.Contains("UiLootCodex.Pass"),
            "表态没走 SendLootIntentAsync 或没走三个词");

        Report("loot_feed",
            client.Contains("Ui.UiLootFlow.Feed(message.LootAssigned)"),
            "战斗客户端的 LootAssigned 没交给掷点屏");

        Report("loot_owner_from_reply",
            codex.Contains("WinnerCharacterId == 0") && codex.Contains("Nobody"),
            "归属没按 winner_character_id 判「没人要」");
    }

    static void CheckReconnect(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var codex = Read(Path.Combine(ui, "UiReconnectCodex.cs"));
        var flow = Read(Path.Combine(ui, "UiReconnectFlow.cs"));
        var screen = Read(Path.Combine(ui, "UiReconnectScreen.cs"));
        var net = Read(Path.Combine(root, "client/MMOGame/Assets/Net/DungeonEntry.cs"));

        Report("reconnect_fact_source",
            net.Contains("deserter_remaining_s") && flow.Contains("outcome.DeserterRemainingS")
            && codex.Contains("deserter_remaining_s"),
            "惩罚剩余秒数不是从 deserter_remaining_s 来的");

        // 本地只按秒走针：不许拿进场时刻 / 系统时钟自己减。
        var local = codex + flow + screen;
        Report("reconnect_no_clock",
            local.IndexOf("DateTime", StringComparison.Ordinal) < 0
            && local.IndexOf("UtcNow", StringComparison.Ordinal) < 0
            && local.IndexOf("starts_at", StringComparison.Ordinal) < 0
            && flow.Contains("Time.deltaTime"),
            "客户端拿时钟算了剩余时间");

        Report("reconnect_async",
            flow.Contains("ReconnectAsync") && screen.Contains("ReconnectNow"),
            "重连没走 BattleLink.ReconnectAsync");
    }

    static void CheckBoss(string root)
    {
        var ui = Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui");
        var screen = Read(Path.Combine(ui, "UiWorldBossScreen.cs"));
        var net = Read(Path.Combine(root, "client/MMOGame/Assets/Net/WorldBossEntry.cs"));

        Report("boss_read_only",
            screen.Contains("flow.LoadBoss") && screen.Contains("WorldBossEntry.OwnerLine"),
            "入口屏没走已有的只读入口");

        Report("boss_five_numbers",
            screen.Contains("facts.Current") && screen.Contains("facts.Cap")
            && screen.Contains("facts.Remaining") && screen.Contains("facts.HoldSeconds")
            && screen.Contains("facts.RenewSeconds") && screen.Contains("facts.LostSeconds"),
            "名额与三个时长没逐条照抄");

        // 窗口时刻不在客户端折时区：屏上不许出现本地时间换算。
        Report("boss_no_timezone",
            screen.IndexOf("DateTime", StringComparison.Ordinal) < 0
            && screen.IndexOf("TimeZone", StringComparison.Ordinal) < 0
            && net.Contains("SlotLine"),
            "入口屏自己折了时区");

        Report("boss_killed_state",
            screen.Contains("这一场已打掉") && screen.Contains("这一刻没开"),
            "开没开 / 打没打掉的文案不全");
    }

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiU14SelfCheck");
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
            Debug.LogError("u14_self_stack " + inner);
            return;
        }

        Report("self", ok, ok ? "four_screens" : "see_u14_self_logs");
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
