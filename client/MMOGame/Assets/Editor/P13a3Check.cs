using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Mmo.Net;
using Mmo.Proto.World;
using Debug = UnityEngine.Debug;

// P1-3a3 编辑器核对：三档距离只在配置里、档位由世界服算、客户端只按档位翻译展示，
// 名字牌文本来自服务端快照。全程不进 playmode，不连服务端，也不重打 AB。
public static class P13a3Check
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // 1. 方案只写定三档顺序，没写定距离：两档阈值只在 config/world.json，且注明待定
        var cfg = File.ReadAllText(Path.Combine(root, "config/world.json"));
        var near = Number(cfg, "tier_near_distance");
        var far = Number(cfg, "tier_far_distance");
        var configOk = near > 0 && far > near && cfg.Contains("数值待定");
        Debug.Log("p13a3_config=" + (configOk ? "ok" : "fail")
                  + " near=" + near.ToString("F1", CultureInfo.InvariantCulture)
                  + " far=" + far.ToString("F1", CultureInfo.InvariantCulture));
        failed |= !configOk;

        // 2. 契约：档位与名字只在服务端下发的 AvatarPosition 里，客户端上报帧带不进
        var proto = File.ReadAllText(Path.Combine(root, "proto/mmo/world.proto"));
        var report = Regex.Match(proto, @"message PositionReport \{(.*?)\}", RegexOptions.Singleline);
        var reportOk = report.Success
                       && !report.Groups[1].Value.Contains("appearance_tier")
                       && !report.Groups[1].Value.Contains("display_name")
                       && !report.Groups[1].Value.Contains("account");
        var avatar = Regex.Match(proto, @"message AvatarPosition \{(.*?)\}", RegexOptions.Singleline);
        var enter = Regex.Match(proto, @"message EnterLineRequest \{(.*?)\}", RegexOptions.Singleline);
        var protoOk = reportOk && avatar.Success
                      && avatar.Groups[1].Value.Contains("AppearanceTier appearance_tier")
                      && avatar.Groups[1].Value.Contains("string display_name")
                      && enter.Success && enter.Groups[1].Value.Contains("string display_name")
                      && Regex.IsMatch(proto, @"APPEARANCE_TIER_FULL\s*=\s*1")
                      && Regex.IsMatch(proto, @"APPEARANCE_TIER_SIMPLE\s*=\s*2")
                      && Regex.IsMatch(proto, @"APPEARANCE_TIER_NAME\s*=\s*3");
        Debug.Log("p13a3_proto=" + (protoOk ? "ok" : "fail")
                  + " report=" + reportOk + " avatar=" + avatar.Success);
        failed |= !protoOk;

        // 3. 三档表现：完整 / 简模 / 只留名字，别人的拖尾与光效一律关掉
        var peers = new WorldPeerPositions();
        peers.Apply(SnapshotOf(42, AppearanceTier.Full, "p13a3_a"));
        PeerPosition nearPeer;
        var found = peers.TryGet(42, out nearPeer);
        var full = WorldPeerTiers.For(nearPeer);
        peers.Apply(SnapshotOf(42, AppearanceTier.Simple, "p13a3_a"));
        PeerPosition midPeer;
        found &= peers.TryGet(42, out midPeer);
        var simple = WorldPeerTiers.For(midPeer);
        peers.Apply(SnapshotOf(42, AppearanceTier.Name, "p13a3_a"));
        PeerPosition farPeer;
        found &= peers.TryGet(42, out farPeer);
        var nameOnly = WorldPeerTiers.For(farPeer);
        var unknown = WorldPeerTiers.For(AppearanceTier.Unspecified, "p13a3_a");

        var fullOk = full.Kind == PeerVisualKind.Full && full.FullModel
                     && !full.SimpleModel && full.NamePlate && !full.TrailsAndEffects;
        var simpleOk = simple.Kind == PeerVisualKind.Simple && !simple.FullModel
                       && simple.SimpleModel && simple.NamePlate && !simple.TrailsAndEffects;
        var nameOk = nameOnly.Kind == PeerVisualKind.NameOnly && !nameOnly.FullModel
                     && !nameOnly.SimpleModel && nameOnly.NamePlate && !nameOnly.TrailsAndEffects;
        var namesOk = full.DisplayName == "p13a3_a" && simple.DisplayName == "p13a3_a"
                      && nameOnly.DisplayName == "p13a3_a";
        // 不认识的档位按最省处理，绝不擅自画完整模型。
        var unknownOk = unknown.Kind == PeerVisualKind.NameOnly && !unknown.FullModel;
        var tiersOk = found && fullOk && simpleOk && nameOk && namesOk && unknownOk;
        Debug.Log("p13a3_tiers=" + (tiersOk ? "ok" : "fail")
                  + " full=" + fullOk + " simple=" + simpleOk + " name=" + nameOk
                  + " names=" + namesOk + " unknown=" + unknownOk);
        failed |= !tiersOk;

        // 4. 边界：档位只在服务端算，客户端表现层不读坐标、不算距离
        var tierTable = File.ReadAllText(Path.Combine(root, "src/World/AppearanceTierTable.cs"));
        var tiersCode = File.ReadAllText(Path.Combine(Application.dataPath, "Net/WorldPeerTiers.cs"));
        var hub = File.ReadAllText(Path.Combine(root, "src/World/PositionHub.cs"));
        var boundaryOk = tierTable.Contains("config.TierNearDistance")
                         && tierTable.Contains("config.TierFarDistance")
                         && tierTable.Contains("Math.Sqrt")
                         && hub.Contains("tiers.For(self, avatar)")
                         && tiersCode.Contains("AppearanceTier.Full")
                         && tiersCode.Contains("AppearanceTier.Simple")
                         && tiersCode.Contains("AppearanceTier.Name")
                         && !tiersCode.Contains("Math.Sqrt")
                         && !tiersCode.Contains("peer.X")
                         && !tiersCode.Contains("avatar.X");
        Debug.Log("p13a3_boundary=" + (boundaryOk ? "ok" : "fail"));
        failed |= !boundaryOk;

        EditorApplication.Exit(failed ? 1 : 0);
    }

    static PositionSnapshot SnapshotOf(ulong id, AppearanceTier tier, string name)
    {
        var snapshot = new PositionSnapshot
        {
            LineId = "栖云城#1", RegionId = "栖云城", ServerTimeMs = 1000,
            SyncHz = 8, SelfCharacterId = 7,
        };
        snapshot.Avatars.Add(new AvatarPosition
        {
            CharacterId = id, X = 1, Y = 0, Z = 0, Facing = 0, Seq = 1,
            AppearanceTier = tier, DisplayName = name,
        });
        return snapshot;
    }

    static double Number(string json, string key)
    {
        var match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
        return match.Success
            ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
    }
}
