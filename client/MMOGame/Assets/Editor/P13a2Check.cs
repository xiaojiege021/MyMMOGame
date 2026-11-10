using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Mmo.Net;
using Mmo.Proto.World;
using Debug = UnityEngine.Debug;

// P1-3a2 编辑器核对：同步频率只从配置读、客户端能收到并更新周围人的坐标，
// 位置帧里没有身份与分线号。全程不进 playmode，不连服务端，也不重打 AB。
public static class P13a2Check
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // 1. 方案写定 5–10Hz，具体值只在 config/world.json，且注明待定
        var cfg = File.ReadAllText(Path.Combine(root, "config/world.json"));
        var match = Regex.Match(cfg, "\"sync_hz\"\\s*:\\s*(\\d+)");
        var hz = match.Success ? int.Parse(match.Groups[1].Value) : 0;
        var configOk = hz >= 5 && hz <= 10 && cfg.Contains("数值待定");
        Debug.Log("p13a2_config=" + (configOk ? "ok" : "fail") + " hz=" + hz);
        failed |= !configOk;

        // 2. proto 是双向流，客户端帧里带不进身份、分线号或别人的坐标
        var proto = File.ReadAllText(Path.Combine(root, "proto/mmo/world.proto"));
        var streamOk = proto.Contains(
            "rpc PositionStream(stream WorldClientFrame) returns (stream WorldServerFrame)");
        var report = Regex.Match(proto, @"message PositionReport \{(.*?)\}", RegexOptions.Singleline);
        var reportOk = report.Success
                       && !report.Groups[1].Value.Contains("account")
                       && !report.Groups[1].Value.Contains("character_id")
                       && !report.Groups[1].Value.Contains("line_id");
        var link = File.ReadAllText(Path.Combine(Application.dataPath, "Net/WorldLink.cs"));
        var framesOk = link.Contains("WorldClientFrame") && link.Contains("PositionReport")
                       && link.Contains("WorldServerFrame") && link.Contains("WebSocketMessageType.Binary");
        Debug.Log("p13a2_frames=" + (streamOk && reportOk && framesOk ? "ok" : "fail")
                  + " stream=" + streamOk + " report=" + reportOk + " client=" + framesOk);
        failed |= !(streamOk && reportOk && framesOk);

        // 3. 周围的人：两帧快照里同一个人坐标与朝向都变了，换线后名单清空
        var peers = new WorldPeerPositions();
        var first = new PositionSnapshot
        {
            LineId = "枯岚道#1", RegionId = "枯岚道", ServerTimeMs = 1000,
            SyncHz = (uint)hz, SelfCharacterId = 7,
        };
        first.Avatars.Add(new AvatarPosition
        {
            CharacterId = 42, X = 1, Y = 0, Z = 0, Facing = 0, Seq = 1,
        });
        var second = new PositionSnapshot
        {
            LineId = "枯岚道#1", RegionId = "枯岚道", ServerTimeMs = 1125,
            SyncHz = (uint)hz, SelfCharacterId = 7,
        };
        second.Avatars.Add(new AvatarPosition
        {
            CharacterId = 42, X = 3, Y = 0, Z = 2, Facing = 1.5f, Seq = 2,
        });
        var moved = new PositionSnapshot
        {
            LineId = "栖云城#1", RegionId = "栖云城", ServerTimeMs = 1250,
            SyncHz = (uint)hz, SelfCharacterId = 7,
        };

        peers.Apply(first);
        PeerPosition before;
        var beforeOk = peers.TryGet(42, out before) && before.X == 1 && before.Seq == 1;
        peers.Apply(second);
        PeerPosition after;
        var afterOk = peers.TryGet(42, out after) && after.X == 3 && after.Z == 2
                      && after.Facing > 1.4f && after.Seq == 2;
        peers.Apply(moved);
        var goneOk = peers.Count == 0 && peers.LineId == "栖云城#1" && peers.Snapshots == 3
                     && peers.LastSyncHz == (uint)hz;
        var peersOk = beforeOk && afterOk && goneOk;
        Debug.Log("p13a2_peers=" + (peersOk ? "ok" : "fail")
                  + " before=" + beforeOk + " moved=" + afterOk + " gone=" + goneOk);
        failed |= !peersOk;

        // 4. 客户端只做收发，不在这里定广播频率、不做同屏档位或技能结算
        var container = File.ReadAllText(Path.Combine(Application.dataPath, "Net/WorldPeerPositions.cs"));
        var containerOk = container.Contains("PositionSnapshot") && container.Contains("TryGet")
                          && !container.Contains("sync_hz =") && !container.Contains("Damage");
        Debug.Log("p13a2_boundary=" + (containerOk ? "ok" : "fail"));
        failed |= !containerOk;

        EditorApplication.Exit(failed ? 1 : 0);
    }
}
