using System;
using System.IO;
using System.Reflection;
using Mmo.Proto.Battle;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// P0-2a 编辑器核对：热更 dll 能装进运行时、点选能切换目标、
// 目标编号真的进了技能请求、Launch 包内容检查认新资源。
// 全程不进 playmode，不连战斗进程。
public static class P02aCheck
{
    public static void Run()
    {
        var failed = false;

        // 1. 编热更 dll 并复制成 Launch 原生资源
        E6Build.PrepareHotDll(BuildTarget.Android);
        var dllPath = Path.Combine(E6Build.HotDllDir, "HotUpdate.bytes");
        var bytes = File.ReadAllBytes(dllPath);

        // 2. 装进运行时（一份独立副本，和 E7Check 同一套加载方式）
        Assembly asm;
        try
        {
            asm = Assembly.Load(bytes);
        }
        catch (Exception e)
        {
            Debug.Log("p02a_check=fail reason=asm_load " + e.GetType().Name + " " + e.Message);
            EditorApplication.Exit(1);
            return;
        }

        // 3. 点选与切换目标
        var selector = asm.GetType("HotUpdate.TargetSelector");
        var select = selector.GetMethod("Select", BindingFlags.Public | BindingFlags.Static);
        var current = selector.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
        var buildReq = selector.GetMethod("BuildRequest", BindingFlags.Public | BindingFlags.Static);
        if (select == null || current == null || buildReq == null)
        {
            Debug.Log("p02a_check=fail reason=selector_members");
            EditorApplication.Exit(1);
            return;
        }

        select.Invoke(null, new object[] { 9001u, "试炼石像" });
        var first = (uint)current.GetValue(null);
        select.Invoke(null, new object[] { 9002u, "训练木桩" });
        var second = (uint)current.GetValue(null);
        var times = (int)selector.GetProperty("SelectCount", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        var switchOk = first == 9001u && second == 9002u && times == 2;
        Debug.Log("p02a_select=" + (switchOk ? "ok" : "fail")
            + " first=" + first + " second=" + second + " times=" + times);
        failed |= !switchOk;

        // 4. 目标编号放进技能请求
        var request = (SkillRequest)buildReq.Invoke(null, new object[] { 1001u });
        var requestOk = request.SkillId == 1001u && request.TargetId == 9002u && request.CastTimeMs > 0;
        Debug.Log("p02a_request=" + (requestOk ? "ok" : "fail")
            + " skill=" + request.SkillId + " target=" + request.TargetId
            + " cast_ms=" + request.CastTimeMs);
        failed |= !requestOk;

        // 5. 场景在包的收集范围里
        var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Scenes/BossBattle.unity");
        var sceneOk = scene != null;
        Debug.Log("p02a_scene=" + (sceneOk ? "ok" : "fail") + " path=Assets/Scenes/BossBattle.unity");
        failed |= !sceneOk;

        // 6. Launch 包内容检查：认新资源、仍拒绝后期境界
        var checkAssets = typeof(AOT.LaunchSession).GetMethod("CheckAssets", BindingFlags.Public | BindingFlags.Static);
        if (checkAssets == null)
        {
            Debug.Log("p02a_check=fail reason=no_check_assets");
            EditorApplication.Exit(1);
            return;
        }

        var good = new System.Collections.Generic.List<string>
        {
            "Assets/Art/Launch/marker.txt",
            "Assets/Scenes/BossBattle.unity",
            "Assets/HotUpdateDlls/HotUpdate.bytes",
        };
        var goodArgs = new object[] { good, "" };
        var goodOk = (bool)checkAssets.Invoke(null, goodArgs);
        Debug.Log("p02a_assets=" + (goodOk ? "ok" : "fail") + " detail=" + goodArgs[1]);
        failed |= !goodOk;

        var bad = new System.Collections.Generic.List<string> { "Assets/Art/Launch/元婴/secret.txt" };
        var badArgs = new object[] { bad, "" };
        var badOk = !(bool)checkAssets.Invoke(null, badArgs);
        Debug.Log("p02a_realm_reject=" + (badOk ? "ok" : "fail") + " detail=" + badArgs[1]);
        failed |= !badOk;

        // 7. 原来的进门还在
        var entry = asm.GetType("HotUpdate.Entry");
        var ping = entry.GetMethod("Ping", BindingFlags.Public | BindingFlags.Static);
        var pingOk = "hotupdate-ok".Equals(ping.Invoke(null, null));
        Debug.Log("p02a_entry=" + (pingOk ? "ok" : "fail") + " ping=" + ping.Invoke(null, null));
        failed |= !pingOk;

        Debug.Log(failed ? "p02a_check=fail" : "p02a_check=ok items=7");
        EditorApplication.Exit(failed ? 1 : 0);
    }
}
