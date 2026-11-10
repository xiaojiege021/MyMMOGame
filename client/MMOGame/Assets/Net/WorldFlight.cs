using System;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>飞行回包给界面用的展示结果。成不成只认服务端回包，不在本地改。</summary>
    public struct FlyOutcome
    {
        public bool Ok;
        public string Reason;
        public bool Flying;
        public bool CanFly;
        public string Realm;
        public string RequiredRealm;
        public float Y;
        public string DisplayText;

        // 方案 12.1 写定「飞行仍由境界开放。没有飞行权限时，拖尾不让人飞」：
        // 拖尾只跟着服务端判完的 flying 走，买没买拖尾、本地想不想飞都不算数。
        public bool TrailAllowed => Flying;
    }

    /// <summary>跨区回包给界面用的展示结果。被拉回时 RegionId / LineId 是原来那一块、那一条。</summary>
    public struct RegionOutcome
    {
        public bool Ok;
        public string Reason;
        public string RegionId;
        public string LineId;
        public uint LineNo;
        public string Realm;
        public string RequiredRealm;
        public string DisplayText;
    }

    /// <summary>
    /// 飞行与跨区的请求体、回包拆装。按开发架构第 3 节，客户端只做动作与展示：
    /// 境界由世界服问角色服，权限、相连、开放与门槛都在服务端判，不通过就拉回。
    /// 这里不算境界、不看配置、不自行决定能不能飞，也不往请求里塞 realm / can_fly / 账号。
    /// Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，不用引擎的 JsonUtility，
    /// 只拆这两条固定形状的回包，不引第二套网络或 JSON 框架。
    /// </summary>
    public static class WorldFlight
    {
        public static string FlyRequestBody(bool wantFly) =>
            "{\"want_fly\":" + (wantFly ? "true" : "false") + "}";

        public static string RegionRequestBody(string targetRegion) =>
            "{\"target_region\":\"" + Escape(targetRegion ?? string.Empty) + "\"}";

        public static FlyOutcome ParseFly(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new FlyOutcome
                {
                    Ok = false,
                    Reason = "bad_reply",
                    DisplayText = "回包读不出来",
                };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var flying = ReadBool(json, "flying");
            var required = Raw(json, "required_realm");
            // 被拒时直接用服务端给的原因与拉回后的高度，不在本地补一个「其实飞起来了」。
            var text = ok
                ? (flying ? "起飞" : "落地")
                : "飞不起来：" + reason
                  + (required.Length > 0 ? "（需要 " + required + "）" : string.Empty);
            return new FlyOutcome
            {
                Ok = ok,
                Reason = reason,
                Flying = flying,
                CanFly = ReadBool(json, "can_fly"),
                Realm = Raw(json, "realm"),
                RequiredRealm = required,
                Y = ReadFloat(json, "y"),
                DisplayText = text,
            };
        }

        public static RegionOutcome ParseRegion(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new RegionOutcome
                {
                    Ok = false,
                    Reason = "bad_reply",
                    DisplayText = "回包读不出来",
                };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var regionId = Raw(json, "region_id");
            var required = Raw(json, "required_realm");
            var text = ok
                ? "进入 " + regionId
                : "没能跨区：" + reason
                  + (required.Length > 0 ? "（需要 " + required + "）" : string.Empty);
            return new RegionOutcome
            {
                Ok = ok,
                Reason = reason,
                RegionId = regionId,
                LineId = Raw(json, "line_id"),
                LineNo = ReadUInt(json, "line_no"),
                Realm = Raw(json, "realm"),
                RequiredRealm = required,
                DisplayText = text,
            };
        }

        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static bool ReadBool(string json, string key) =>
            string.Equals(Raw(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static uint ReadUInt(string json, string key)
        {
            uint.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        static float ReadFloat(string json, string key)
        {
            float.TryParse(Raw(json, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        /// <summary>取一个顶层字段的原始文本。回包字段固定，够用即可，不做通用 JSON 解析。</summary>
        static string Raw(string json, string key)
        {
            var needle = "\"" + key + "\"";
            var at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0)
            {
                return string.Empty;
            }

            var colon = json.IndexOf(':', at + needle.Length);
            if (colon < 0)
            {
                return string.Empty;
            }

            var i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i]))
            {
                i++;
            }

            if (i >= json.Length)
            {
                return string.Empty;
            }

            if (json[i] == '"')
            {
                i++;
                var start = i;
                while (i < json.Length)
                {
                    if (json[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (json[i] == '"')
                    {
                        break;
                    }

                    i++;
                }

                return json.Substring(start, Math.Min(i, json.Length) - start);
            }

            var end = i;
            while (end < json.Length && json[end] != ',' && json[end] != '}'
                   && !char.IsWhiteSpace(json[end]))
            {
                end++;
            }

            return json.Substring(i, end - i);
        }
    }
}
