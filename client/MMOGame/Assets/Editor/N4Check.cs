using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>N4 编辑器核对：采集屏节点只接 GET /world/gather/nodes。</summary>
public static class N4Check
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

        Debug.Log("n4_all=" + (failed ? "fail" : "ok"));
        return !failed;
    }

    [MenuItem("Tools/核对/N4 采集节点进表世界屏")]
    static void RunFromMenu() => CheckAll();

    static void Report(string key, bool ok, string detail = "")
    {
        Debug.Log("n4_" + key + "=" + (ok ? "ok" : "fail") + (detail.Length > 0 ? " " + detail : string.Empty));
        failed |= !ok;
    }

    // D13 之后客户端 Ui / 战斗 HUD / 表世界拆装层也按域分片：主文件 + 同域目录一起读
    // （与 tools/check 的 src_agg、lib_read.py 同口径，断言文本不变）。
    static string Read(string path) => SplitRead.Of(path);

    // D3d 之后网关的 HTTP 注册体按域分片（主文件 + Api/<域>/）；只读主文件会漏掉搬走的路由。
    // D3d/D3h 之后源码按域分片：主文件 + 同域分片目录一起读（与 tools/check 的 src_agg 同口径）。
    static string ReadSplit(string path)
    {
        var name = Path.GetFileName(path);
        var sub = name switch
        {
            "CharacterApi.cs" => "Api/Character",
            "SectApi.cs" => "Api/Sect",
            "WorldApi.cs" => "Api/World",
            "SectRepository.cs" => "Sect",
            "SocialGrpcService.cs" => "Grpc",
            "WorldGrpcService.cs" => "Grpc",
            "Program.cs" => "Cli",
            _ => string.Empty,
        };
        var text = Read(path);
        if (sub.Length == 0)
        {
            return text;
        }
        var dir = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, sub);
        if (!Directory.Exists(dir))
        {
            return text;
        }
        var files = new List<string>(Directory.GetFiles(dir, "*.cs"));
        files.Sort(StringComparer.Ordinal);
        foreach (var f in files)
        {
            text += Read(f);
        }
        return text;
    }

    static void CheckFiles(string root)
    {
        var missing = new List<string>();
        foreach (var rel in new[]
                 {
                     "client/MMOGame/Assets/Net/WorldGather.cs",
                     "client/MMOGame/Assets/HotUpdate/Ui/UiGatherScreen.cs",
                     "client/MMOGame/Assets/HotUpdate/Ui/UiGatherNodesSelfCheck.cs",
                     "client/MMOGame/Assets/Editor/N4Check.cs",
                     "tools/check/check-n4.sh",
                 })
        {
            if (!File.Exists(Path.Combine(root, rel)))
            {
                missing.Add(rel);
            }
        }

        Report("files", missing.Count == 0, string.Join(",", missing.ToArray()));
    }

    static void CheckWiring(string root)
    {
        var flow = Read(Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui/UiWorldFlow.cs"));
        var gatherUi = Read(Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui/UiGatherScreen.cs"));
        var codex = Read(Path.Combine(root, "client/MMOGame/Assets/HotUpdate/Ui/UiWorldCodex.cs"));
        var net = Read(Path.Combine(root, "client/MMOGame/Assets/Net/WorldGather.cs"));
        var proto = Read(Path.Combine(root, "proto/mmo/world.proto"));
        var gateway = ReadSplit(Path.Combine(root, "src/Gateway/WorldApi.cs"));
        var world = ReadSplit(Path.Combine(root, "src/World/WorldGrpcService.cs"));

        Report("proto", proto.Contains("ListGatherNodes") && proto.Contains("GatherNodeInfo"));
        Report("gateway", gateway.Contains("/world/gather/nodes") && gateway.Contains("ListGatherNodesAsync"));
        Report("world_svc", world.Contains("ListGatherNodes"));
        Report("parse", net.Contains("ParseList") && net.Contains("GatherNodeRow"));
        Report("flow", flow.Contains("LoadGatherNodes") && flow.Contains("NodesOfKind"));
        Report("screen", gatherUi.Contains("n4_gather_screen=ready")
            && gatherUi.Contains("NoGatherNodesYet")
            && UiTextRef.In(root, gatherUi, "UiGatherScreen", "读取节点表"));
        Report("no_config", !codex.Contains("static readonly GatherNode[] Nodes")
            && !gatherUi.Contains("config/gather.json"));
    }

    static void CheckSelf()
    {
        var type = FindType("HotUpdate.Ui.UiGatherNodesSelfCheck");
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

        try
        {
            Report("self", (bool)method.Invoke(null, null));
        }
        catch (Exception error)
        {
            Report("self", false, error.Message);
        }
    }

    static Type FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }
}