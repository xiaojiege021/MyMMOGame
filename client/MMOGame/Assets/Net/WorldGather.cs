using System;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>给界面用的展示结果。成功与否就是服务端回包里的 ok，不在本地改。</summary>
    public struct GatherOutcome
    {
        public bool Ok;
        public string Reason;
        public string NodeId;
        public string Kind;
        public string ItemId;
        public int Count;
        public uint BagSlot;
        public string TxnId;
        public bool Duplicate;
        public int RespawnSeconds;
        public string DisplayText;
    }

    /// <summary>
    /// 采集的请求与回包拆装。按开发架构第 3 节，客户端只做采集动作与展示：
    /// 成功与否、节点占用与刷新、产物种类与数量全由世界服按配置判，成功后由角色服入背包。
    /// 这里不读采集表、不算距离、不判定成功，也不往请求里塞产物或数量。
    /// Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，所以不用引擎的 JsonUtility，
    /// 只拆这一条固定形状的请求与回包，不引第二套网络或 JSON 框架。
    /// </summary>
    public static class WorldGather
    {
        public static string RequestBody(string nodeId)
        {
            // 请求体只有节点编号；身份、产物、数量、格子、事务号都带不进来。
            return "{\"node_id\":\"" + Escape(nodeId ?? string.Empty) + "\"}";
        }

        public static GatherOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new GatherOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = ReadBool(json, "ok");
            var itemId = Raw(json, "item_id");
            var count = ReadInt(json, "count");
            var reason = Raw(json, "reason");
            // 展示文本不参与数值；被拒时直接把服务端给的 reason 显示出来。
            var text = ok
                ? "获得 " + itemId + " ×" + count.ToString(CultureInfo.InvariantCulture)
                : "采集未成功：" + reason;
            return new GatherOutcome
            {
                Ok = ok,
                Reason = reason,
                NodeId = Raw(json, "node_id"),
                Kind = Raw(json, "kind"),
                ItemId = itemId,
                Count = count,
                BagSlot = ReadUInt(json, "bag_slot"),
                TxnId = Raw(json, "txn_id"),
                Duplicate = ReadBool(json, "duplicate"),
                RespawnSeconds = ReadInt(json, "respawn_s"),
                DisplayText = text,
            };
        }

        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static bool ReadBool(string json, string key) =>
            string.Equals(Raw(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static int ReadInt(string json, string key)
        {
            int.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        static uint ReadUInt(string json, string key)
        {
            uint.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
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
