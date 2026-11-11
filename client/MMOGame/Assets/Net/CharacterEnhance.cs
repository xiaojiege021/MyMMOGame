using System;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>给界面用的展示结果。等级与成败就是服务端回包里的值，不在本地改。</summary>
    public struct EnhanceOutcome
    {
        public bool Ok;
        public string Reason;
        public int LevelBefore;
        public int Level;
        public int MaxLevel;
        public bool Success;
        public string Tier;
        public string RequiredStone;
        public int StoneCost;
        public long GoldCost;
        public long Gold;
        public int StoneLeft;
        public bool Duplicate;
        public bool ProtectedLevel;
        public int CouponUsed;
        public int CouponLeft;
        public string DisplayText;
    }

    /// <summary>
    /// 强化的请求与回包拆装。按开发架构第 3 节，客户端只发「哪一格、用哪种石头」，
    /// P1-4b 起多一个「这一手用不用保护券」的开关：
    /// 当前等级、目标档位、材料是否够、成功率、扣多少金币与石头、掷点结果都在角色服按
    /// config/enhance.json 判，扣材料与改等级在同一笔事务里。这里不读强化表、不算概率、
    /// 不本地改等级，也不往请求里塞等级、成功率、结果、余额、券编号 / 数量 / 来源或事务号。
    /// Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，不引引擎里的 JSON 组件，
    /// 只拆这一条固定形状的请求与回包。
    /// </summary>
    public static class CharacterEnhance
    {
        public static string RequestBody(ulong characterId, uint slot, string stoneId) =>
            RequestBody(characterId, slot, stoneId, false);

        public static string RequestBody(ulong characterId, uint slot, string stoneId, bool useCoupon)
        {
            // 身份由网关认令牌与归属复核，这里只是同一套请求体里必须带的角色编号。
            // 用券只带一个开关：券的编号、张数、来源、成功率与结果都不在这里。
            var coupon = useCoupon ? ",\"use_coupon\":true" : string.Empty;
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture)
                + ",\"stone_id\":\"" + Escape(stoneId ?? string.Empty) + "\""
                + coupon + "}";
        }

        public static EnhanceOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new EnhanceOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = ReadBool(json, "ok");
            var success = ReadBool(json, "success");
            var before = ReadInt(json, "level_before");
            var level = ReadInt(json, "level");
            var reason = Raw(json, "reason");
            var protectedLevel = ReadBool(json, "protected_level");
            string text;
            if (!ok)
            {
                text = "强化未成功：" + reason;
            }
            else if (success)
            {
                text = "强化成功：+" + level.ToString(CultureInfo.InvariantCulture);
            }
            else if (protectedLevel)
            {
                // 方案 9.2：券只挡掉级，不提高成功率；这里只翻译服务端回包。
                text = "强化失败：+" + before.ToString(CultureInfo.InvariantCulture)
                    + " 被保护券挡住，等级不变";
            }
            else
            {
                // 方案 9.2：失败只掉 1 级，不碎也不清零。
                text = "强化失败：+" + before.ToString(CultureInfo.InvariantCulture)
                    + " → +" + level.ToString(CultureInfo.InvariantCulture);
            }

            return new EnhanceOutcome
            {
                Ok = ok,
                Reason = reason,
                LevelBefore = before,
                Level = level,
                MaxLevel = ReadInt(json, "max_level"),
                Success = success,
                Tier = Raw(json, "tier"),
                RequiredStone = Raw(json, "required_stone"),
                StoneCost = ReadInt(json, "stone_cost"),
                GoldCost = ReadLong(json, "gold_cost"),
                Gold = ReadLong(json, "gold"),
                StoneLeft = ReadInt(json, "stone_left"),
                Duplicate = ReadBool(json, "duplicate"),
                ProtectedLevel = protectedLevel,
                CouponUsed = ReadInt(json, "coupon_used"),
                CouponLeft = ReadInt(json, "coupon_left"),
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

        static long ReadLong(string json, string key)
        {
            long.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
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
