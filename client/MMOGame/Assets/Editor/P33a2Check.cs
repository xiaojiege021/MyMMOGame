using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Mmo.Net;
using Mmo.Proto.Battle;
using Debug = UnityEngine.Debug;

// P3-3a2 编辑器核对：同屏上限只在配置里、档位只由战斗服算、客户端只按档位翻译展示。
// 全程不进 playmode，不连服务端，也不重打 AB。
public static class P33a2Check
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // 1. 方案只写定「超出同屏上限的人只留队伍颜色和血条」，没写定上限：
        //    数字只在 config/siege.json 的 view 段，并注明待定，代码里不给兜底。
        var cfg = File.ReadAllText(Path.Combine(root, "config/siege.json"));
        var limit = Number(cfg, "full_model_limit");
        var configOk = limit > 0
                       && Regex.Matches(cfg, "\"full_model_limit\"").Count == 1
                       && cfg.Contains("数值待定")
                       && cfg.Contains("\"view\"");
        Debug.Log("p33a2_config=" + (configOk ? "ok" : "fail")
                  + " limit=" + limit.ToString(CultureInfo.InvariantCulture));
        failed |= !configOk;

        // 2. 契约：档位只在服务端下发的 SiegeView 里，请求侧表达不了「把谁画全」；
        //    单位消息只带队伍编号与血条事实值，没有模型 / 外观 / 颜色 / 坐标字段。
        var proto = File.ReadAllText(Path.Combine(root, "proto/mmo/battle.proto"));
        var client = Regex.Match(proto, @"message ClientMessage \{(.*?)\}", RegexOptions.Singleline);
        var server = Regex.Match(proto, @"message ServerMessage \{(.*?)\}", RegexOptions.Singleline);
        var unit = Regex.Match(proto, @"message SiegeUnitVisual \{(.*?)\}", RegexOptions.Singleline);
        var fieldPattern = @"^\s*\w+\s+(\w+)\s*=\s*\d+;";
        var fields = Regex.Matches(unit.Success ? unit.Groups[1].Value : "", fieldPattern,
            RegexOptions.Multiline);
        var fieldNames = new System.Collections.Generic.HashSet<string>();
        foreach (Match f in fields)
        {
            fieldNames.Add(f.Groups[1].Value);
        }
        var protoOk = client.Success && server.Success && unit.Success
                      && !client.Groups[1].Value.Contains("siege_view")
                      && !client.Groups[1].Value.Contains("tier")
                      && server.Groups[1].Value.Contains("SiegeView siege_view")
                      && fieldNames.Contains("member_id") && fieldNames.Contains("team")
                      && fieldNames.Contains("hp") && fieldNames.Contains("hp_max")
                      && fieldNames.Contains("tier")
                      && !fieldNames.Contains("model") && !fieldNames.Contains("prefab")
                      && !fieldNames.Contains("appearance") && !fieldNames.Contains("color")
                      && !fieldNames.Contains("x") && !fieldNames.Contains("y")
                      && !fieldNames.Contains("distance")
                      && Regex.IsMatch(proto, @"SIEGE_VISUAL_MARKER\s*=\s*2");
        Debug.Log("p33a2_proto=" + (protoOk ? "ok" : "fail")
                  + " client=" + client.Success + " unit=" + unit.Success);
        failed |= !protoOk;

        // 3. 两档表现：完整模型 / 只留队伍颜色和血条；未识别档位按最省处理。
        var full = SiegeViewTiers.For(UnitOf(7, SiegeVisualTier.SiegeVisualFull, 0, 900, 1200));
        var marker = SiegeViewTiers.For(UnitOf(8, SiegeVisualTier.SiegeVisualMarker, 1, 300, 1200));
        var unknown = SiegeViewTiers.For(UnitOf(9, SiegeVisualTier.SiegeVisualUnspecified, 1, 1200, 1200));
        var fullOk = full.Kind == SiegeVisualKind.Full && full.FullModel
                     && !full.SimpleModel && full.TeamColor && full.HpBar && !full.TrailsAndEffects;
        var markerOk = marker.Kind == SiegeVisualKind.MarkerOnly && !marker.FullModel
                       && !marker.SimpleModel && marker.TeamColor && marker.HpBar
                       && !marker.NamePlate && !marker.TrailsAndEffects
                       && marker.Team == 1 && marker.Hp == 300 && marker.HpMax == 1200;
        var unknownOk = unknown.Kind == SiegeVisualKind.MarkerOnly && !unknown.FullModel;
        var tiersOk = fullOk && markerOk && unknownOk;
        Debug.Log("p33a2_tiers=" + (tiersOk ? "ok" : "fail")
                  + " full=" + fullOk + " marker=" + markerOk + " unknown=" + unknownOk);
        failed |= !tiersOk;

        // 4. 边界：排序与上限在战斗服，客户端表现层不读坐标、不算距离、不重排名单。
        var tableCode = File.ReadAllText(Path.Combine(root, "src/Battle/SiegeViewTable.cs"));
        var tiersCode = File.ReadAllText(Path.Combine(Application.dataPath, "Net/SiegeViewTiers.cs"));
        var serverCode = File.ReadAllText(Path.Combine(root, "src/Battle/Server.cs"));
        var boundaryOk = tableCode.Contains("static SiegeView Build(")
                         && tableCode.Contains("OrderBy(")
                         && serverCode.Contains("RefreshSiegeViewsAsync")
                         && serverCode.Contains("config.Siege.ViewFullModelLimit")
                         && serverCode.Contains("conn.ViewReady = true")
                         && tiersCode.Contains("SiegeVisualTier.SiegeVisualFull")
                         && tiersCode.Contains("SiegeVisualTier.SiegeVisualMarker")
                         && !tiersCode.Contains("Math.Sqrt")
                         && !tiersCode.Contains("OrderBy")
                         && !tiersCode.Contains("FullModelLimit")
                         && !tiersCode.Contains(".X")
                         && !tiersCode.Contains(".Y");
        Debug.Log("p33a2_boundary=" + (boundaryOk ? "ok" : "fail"));
        failed |= !boundaryOk;

        EditorApplication.Exit(failed ? 1 : 0);
    }

    static SiegeUnitVisual UnitOf(ulong id, SiegeVisualTier tier, uint team, long hp, long hpMax)
    {
        return new SiegeUnitVisual
        {
            MemberId = id, Tier = tier, Team = team, Hp = hp, HpMax = hpMax,
        };
    }

    static double Number(string json, string key)
    {
        var match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
        return match.Success
            ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
    }
}
