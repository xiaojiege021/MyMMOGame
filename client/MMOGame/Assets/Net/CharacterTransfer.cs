using System;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>给界面用的展示结果。两格等级与绑定状态就是服务端回包里的值，不在本地改。</summary>
    public struct TransferOutcome
    {
        public bool Ok;
        public string Reason;
        public int FromLevelBefore;
        public int ToLevelBefore;
        public int FromLevel;
        public int ToLevel;
        public int MaxLevel;
        public long GoldCost;
        public long Gold;
        public string StoneItem;
        public int StoneCost;
        public int StoneLeft;
        public bool TargetBoundBefore;
        public bool TargetBound;
        public bool Duplicate;
        public string DisplayText;
    }

    /// <summary>
    /// 强化转移的请求与回包拆装。按开发架构第 3 节，客户端只发「来源格、目标格」：
    /// 两格等级、绑定状态、转移石编号与张数、金币费用、转移结果、事务号都在角色服
    /// 按转移配置与库里的数据判，扣材料、改绑定与改两格等级在同一笔事务里。
    /// 这里不读转移表、不比等级、不本地改绑定，也不往请求里塞等级、金币、石头或结果。
    /// Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，不引引擎里的 JSON 组件，
    /// 只拆这一条固定形状的请求与回包。
    /// </summary>
    public static class CharacterTransfer
    {
        public static string RequestBody(ulong characterId, uint fromSlot, uint toSlot)
        {
            // 身份由网关认令牌与归属复核，这里只是同一套请求体里必须带的角色编号。
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"from_slot\":" + fromSlot.ToString(CultureInfo.InvariantCulture)
                + ",\"to_slot\":" + toSlot.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static TransferOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new TransferOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var fromBefore = ReadInt(json, "from_level_before");
            var toBefore = ReadInt(json, "to_level_before");
            var fromLevel = ReadInt(json, "from_level");
            var toLevel = ReadInt(json, "to_level");
            var boundBefore = ReadBool(json, "target_bound_before");
            var bound = ReadBool(json, "target_bound");
            var duplicate = ReadBool(json, "duplicate");
            string text;
            if (!ok)
            {
                text = duplicate ? "重复提交，未生效" : "转移未完成：" + reason;
            }
            else
            {
                // 方案 9.3：覆盖不是相加，来源变为 +0，目标变成来源原来的等级。
                text = "转移成功：+" + fromBefore.ToString(CultureInfo.InvariantCulture)
                    + " → +" + toLevel.ToString(CultureInfo.InvariantCulture)
                    + "，来源归 +0";
                if (bound && !boundBefore)
                {
                    text += "，目标已绑定";
                }
            }

            return new TransferOutcome
            {
                Ok = ok,
                Reason = reason,
                FromLevelBefore = fromBefore,
                ToLevelBefore = toBefore,
                FromLevel = fromLevel,
                ToLevel = toLevel,
                MaxLevel = ReadInt(json, "max_level"),
                GoldCost = ReadLong(json, "gold_cost"),
                Gold = ReadLong(json, "gold"),
                StoneItem = Raw(json, "stone_item"),
                StoneCost = ReadInt(json, "stone_cost"),
                StoneLeft = ReadInt(json, "stone_left"),
                TargetBoundBefore = boundBefore,
                TargetBound = bound,
                Duplicate = duplicate,
                DisplayText = text,
            };
        }

        static bool ReadBool(string json, string key) =>
            string.Equals(Raw(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static int ReadInt(string json, string key)
        {
            int.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        static long ReadLong(string json, string key)
        {
            long.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        /// <summary>取一个顶层字段的原始文本。回包字段固定，够用即可，不做通用 JSON 解析。</summary>
        static string Raw(string json, string key)
        {
            var needle = "\"" + key + "\"";
            // 只认「字段名」：后面必须紧跟冒号。回包里可能有一个字符串值恰好等于
            // 另一个字段名（例如 "reason":"duplicate"），拿裸子串找会先撞上这个值，
            // 把 duplicate 读成 false。逐次往后找，直到命中真正的字段。
            var at = -1;
            var scan = 0;
            while (true)
            {
                var hit = json.IndexOf(needle, scan, StringComparison.Ordinal);
                if (hit < 0)
                {
                    return string.Empty;
                }

                var after = hit + needle.Length;
                var p = after;
                while (p < json.Length && char.IsWhiteSpace(json[p]))
                {
                    p++;
                }

                if (p < json.Length && json[p] == ':')
                {
                    at = hit;
                    break;
                }

                scan = after;
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
