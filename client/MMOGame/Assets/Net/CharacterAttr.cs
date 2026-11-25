using System;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>
    /// A9 属性加点的展示结果。四属性是**已加点数**（角色服 `Allocated` 那四个值），
    /// 不是总值：基础值只在 `config/classes.json`，客户端不读表、不相加、不推算。
    /// </summary>
    public struct AttrPointOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong CharacterId;
        public int ClassId;
        public int Level;

        /// <summary>手上还剩多少可分配点。</summary>
        public int AttrPoints;

        /// <summary>四属性各已加多少点（顺序与 <c>UiUpgradeCodex.Attrs</c> 一致）。</summary>
        public int Strength;
        public int Agility;
        public int Spirit;
        public int Vitality;

        /// <summary>这一手是不是重放（网关每次生成新事务号，重放只可能来自同一个号被复用）。</summary>
        public bool Duplicate;

        public string Attribute;
        public int Points;
        public string DisplayText;

        public static AttrPointOutcome Failure(string reason) => new AttrPointOutcome
        {
            Ok = false,
            Reason = reason,
            DisplayText = "回包读不出来：" + reason,
        };
    }

    /// <summary>
    /// A9：属性点的两条 HTTP 口拆装。只读走 `GET /character/attr-points`，
    /// 加点走 `POST /character/attr-points/allocate`。
    ///
    /// 客户端只发「哪一条属性、加几点」：四属性键的白名单、点数够不够、单属性上限、
    /// 事务号幂等全在角色服；事务号由网关生成，请求体里不带。这里不读
    /// `config/attr_points.json`、不判上限、不本地扣点，也不往请求里塞
    /// 剩余点数、当前值、上限或洗点开关——本版没有洗点口。
    /// Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，不引引擎里的 JSON 组件。
    /// </summary>
    public static class CharacterAttr
    {
        /// <summary>只读口的路径前缀。归属由网关按令牌复核，这里只带角色编号。</summary>
        public const string ReadPath = "/character/attr-points";

        public static string ReadQuery(ulong characterId) =>
            ReadPath + "?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        /// <summary>加点口。事务号网关生成，客户端不带。</summary>
        public const string AllocatePath = "/character/attr-points/allocate";

        public static string RequestBody(ulong characterId, string attribute, int points)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"attribute\":\"" + Escape(attribute ?? string.Empty) + "\""
                + ",\"points\":" + points.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static AttrPointOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return AttrPointOutcome.Failure("bad_reply");
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var duplicate = ReadBool(json, "duplicate");
            var attribute = Raw(json, "attribute");
            var points = ReadInt(json, "points");

            string text;
            if (!ok)
            {
                // 拒绝码原样带出来：界面不换成自己的说法。
                text = "加点未生效：" + reason;
            }
            else if (duplicate)
            {
                text = "这一手已经记过账，不重复扣点";
            }
            else
            {
                text = "已加 " + points.ToString(CultureInfo.InvariantCulture) + " 点 " + AttributeName(attribute);
            }

            return new AttrPointOutcome
            {
                Ok = ok,
                Reason = reason,
                CharacterId = ReadULong(json, "character_id"),
                ClassId = ReadInt(json, "class_id"),
                Level = ReadInt(json, "level"),
                AttrPoints = ReadInt(json, "attr_points"),
                Strength = ReadInt(json, "strength"),
                Agility = ReadInt(json, "agility"),
                Spirit = ReadInt(json, "spirit"),
                Vitality = ReadInt(json, "vitality"),
                Duplicate = duplicate,
                Attribute = attribute,
                Points = points,
                DisplayText = text,
            };
        }

        /// <summary>四属性键的中文名。只用于把回包里的键说成人话，不参与任何判定。</summary>
        public static string AttributeName(string attribute)
        {
            switch (attribute)
            {
                case "strength": return "力量";
                case "agility": return "敏捷";
                case "spirit": return "精神";
                case "vitality": return "体力";
                default: return attribute ?? string.Empty;
            }
        }

        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static bool ReadBool(string json, string key) =>
            string.Equals(Raw(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static int ReadInt(string json, string key)
        {
            int.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        static ulong ReadULong(string json, string key)
        {
            ulong.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        /// <summary>
        /// 取一个顶层字段的原始文本。回包字段固定，够用即可，不做通用 JSON 解析。
        ///
        /// 这一版比别的拆装多一道「必须是键」的判：加点回包里 `attribute` 的**值**
        /// 就叫 `strength`，直接找 `"strength"` 会先命中那个值，把点数当成属性值读出来。
        /// 所以命中后要求下一个非空白字符是冒号，不是就继续往后找。
        /// </summary>
        static string Raw(string json, string key)
        {
            var needle = "\"" + key + "\"";
            var at = 0;
            int colon;
            while (true)
            {
                at = json.IndexOf(needle, at, StringComparison.Ordinal);
                if (at < 0)
                {
                    return string.Empty;
                }

                colon = at + needle.Length;
                while (colon < json.Length && char.IsWhiteSpace(json[colon]))
                {
                    colon++;
                }

                if (colon < json.Length && json[colon] == ':')
                {
                    break;
                }

                at += needle.Length;
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
