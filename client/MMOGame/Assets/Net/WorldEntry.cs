using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mmo.Net
{
    /// <summary>进分线的结果。分到哪条线、这条线满没满、周围连着哪几块地表都由世界服给。</summary>
    public enum WorldEnterKind
    {
        Entered,
        Refused,
        BadRequest,
        InvalidToken,
        CharacterNotFound,
        Unavailable,
        Network,
        Rejected,
    }

    /// <summary>`/world/enter` 拆出来的展示结果。没进成就看 <see cref="Reason"/>，不在本地补一个成功。</summary>
    public struct WorldEnterOutcome
    {
        public WorldEnterKind Kind;
        public bool Entered;
        public string Reason;
        public string LineId;
        public string RegionId;
        public uint LineNo;
        public uint LineSize;
        public uint LineCapacity;
        public bool Duplicate;
        public string[] Neighbors;
        public string[] DialogueIds;
        public string Error;

        /// <summary>被拒时给人看的一句。原因原样带出来，不翻译成别的话。</summary>
        public string DisplayText
        {
            get
            {
                switch (Kind)
                {
                    case WorldEnterKind.Entered:
                        return RegionId + " · 第 " + LineNo.ToString(CultureInfo.InvariantCulture) + " 线";
                    case WorldEnterKind.Refused:
                        return "进不去：" + Reason;
                    case WorldEnterKind.Network:
                        return "连不上网关";
                    case WorldEnterKind.InvalidToken:
                        return "令牌失效";
                    default:
                        return "进分线没成：" + (Error.Length > 0 ? Error : Reason);
                }
            }
        }
    }

    /// <summary>一块地表。邻接与对白编号都来自服务端，客户端不自己连边。</summary>
    public struct WorldRegionRow
    {
        public string Id;
        public string Name;
        public uint[] Chapters;
        public string[] Neighbors;
        public string[] DialogueIds;
        public bool Open;
    }

    /// <summary>`/world/regions` 只读回包：地点、开口的人、势力三张编号表。</summary>
    public struct WorldRegionsOutcome
    {
        public WorldEnterKind Kind;
        public uint Version;
        public WorldRegionRow[] Regions;
        public string[] Speakers;
        public string[] Factions;
        public string Error;
    }

    /// <summary>
    /// 表世界两条 REST 的请求与回包拆装：`/world/enter` 进分线、`/world/regions` 读地点表。
    /// 按开发架构第 3 节，分线分配、容量、邻接、开放与对白归属都在世界服判；
    /// 这里只拆固定形状的回包，不算距离、不选线、不按配置补一个「应该分到第几线」。
    /// Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，所以不用引擎的 JsonUtility，
    /// 也不引第二套 JSON 框架——这两条回包字段固定，够用即可。
    /// </summary>
    public static class WorldEntry
    {
        /// <summary>
        /// 进分线的请求体。只有角色号与地表编号：身份走令牌，名字牌与分线都由服务端定，
        /// 客户端带不进 display_name、line_id、neighbors。
        /// </summary>
        public static string EnterBody(ulong characterId, string regionId)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"region_id\":" + Quote(regionId ?? string.Empty) + "}";
        }

        public static WorldEnterOutcome ParseEnter(int status, string json)
        {
            var kind = KindOf(status);
            if (status == 200)
            {
                if (string.IsNullOrEmpty(json) || KeyAt(json, "entered") < 0)
                {
                    return new WorldEnterOutcome
                    {
                        Kind = WorldEnterKind.Rejected,
                        Error = "bad_reply",
                    };
                }

                var entered = ReadBool(json, "entered");
                return new WorldEnterOutcome
                {
                    Kind = entered ? WorldEnterKind.Entered : WorldEnterKind.Refused,
                    Entered = entered,
                    Reason = Raw(json, "reason"),
                    LineId = Raw(json, "line_id"),
                    RegionId = Raw(json, "region_id"),
                    LineNo = ReadUInt(json, "line_no"),
                    LineSize = ReadUInt(json, "line_size"),
                    LineCapacity = ReadUInt(json, "line_capacity"),
                    Duplicate = ReadBool(json, "duplicate"),
                    Neighbors = ReadStringArray(json, "neighbors"),
                    DialogueIds = ReadStringArray(json, "dialogue_ids"),
                    Error = string.Empty,
                };
            }

            // 非 200 的回包里没有业务字段，数组也要显式给空，
            // 免得调用方数邻居个数时撞空引用。
            return new WorldEnterOutcome
            {
                Kind = kind,
                Error = ErrorOf(json, kind),
                Neighbors = Array.Empty<string>(),
                DialogueIds = Array.Empty<string>(),
            };
        }

        public static WorldRegionsOutcome ParseRegions(int status, string json)
        {
            var kind = KindOf(status);
            if (status != 200)
            {
                return new WorldRegionsOutcome
                {
                    Kind = kind,
                    Error = ErrorOf(json, kind),
                    Regions = Array.Empty<WorldRegionRow>(),
                    Speakers = Array.Empty<string>(),
                    Factions = Array.Empty<string>(),
                };
            }

            if (string.IsNullOrEmpty(json) || KeyAt(json, "regions") < 0)
            {
                return new WorldRegionsOutcome
                {
                    Kind = WorldEnterKind.Rejected,
                    Error = "bad_reply",
                    Regions = Array.Empty<WorldRegionRow>(),
                    Speakers = Array.Empty<string>(),
                    Factions = Array.Empty<string>(),
                };
            }

            // regions 是对象数组：先切出每个对象的文本，再逐个取字段，
            // 免得内层的 neighbors / dialogue_ids 跟顶层的同名键串了。
            var blocks = ObjectBlocks(json, "regions");
            var rows = new List<WorldRegionRow>(blocks.Count);
            for (var i = 0; i < blocks.Count; i++)
            {
                var one = blocks[i];
                rows.Add(new WorldRegionRow
                {
                    Id = Raw(one, "id"),
                    Name = Raw(one, "name"),
                    Chapters = ReadUIntArray(one, "chapters"),
                    Neighbors = ReadStringArray(one, "neighbors"),
                    DialogueIds = ReadStringArray(one, "dialogue_ids"),
                    Open = ReadBool(one, "open"),
                });
            }

            return new WorldRegionsOutcome
            {
                Kind = WorldEnterKind.Entered,
                Version = ReadUInt(json, "version"),
                Regions = rows.ToArray(),
                Speakers = ReadStringArray(json, "speakers"),
                Factions = ReadStringArray(json, "factions"),
                Error = string.Empty,
            };
        }

        /// <summary>HTTP 状态到档位的唯一映射。0 是没有建立连接，不算「服务端拒绝」。</summary>
        static WorldEnterKind KindOf(int status)
        {
            switch (status)
            {
                case 200:
                    return WorldEnterKind.Entered;
                case 0:
                    return WorldEnterKind.Network;
                case 400:
                    return WorldEnterKind.BadRequest;
                case 401:
                    return WorldEnterKind.InvalidToken;
                case 404:
                    return WorldEnterKind.CharacterNotFound;
                case 503:
                    return WorldEnterKind.Unavailable;
                default:
                    return WorldEnterKind.Rejected;
            }
        }

        static string ErrorOf(string json, WorldEnterKind kind)
        {
            var error = Raw(json, "error");
            if (error.Length > 0)
            {
                return error;
            }

            switch (kind)
            {
                case WorldEnterKind.Network:
                    return "network";
                case WorldEnterKind.Unavailable:
                    return "unavailable";
                case WorldEnterKind.InvalidToken:
                    return "invalid_token";
                default:
                    return "rejected";
            }
        }

        static string Quote(string value) =>
            "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static bool ReadBool(string json, string key) =>
            string.Equals(Raw(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static uint ReadUInt(string json, string key)
        {
            uint.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        static uint[] ReadUIntArray(string json, string key)
        {
            var inner = ArrayInner(json, key);
            if (inner.Length == 0)
            {
                return Array.Empty<uint>();
            }

            var parts = inner.Split(',');
            var list = new List<uint>(parts.Length);
            for (var i = 0; i < parts.Length; i++)
            {
                if (uint.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    list.Add(value);
                }
            }

            return list.ToArray();
        }

        /// <summary>只认字段名后紧跟冒号，避免 `dialogue_ids` 撞到别的键。</summary>
        static string[] ReadStringArray(string json, string key)
        {
            var inner = ArrayInner(json, key);
            if (inner.Length == 0)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>();
            var i = 0;
            while (i < inner.Length)
            {
                if (inner[i] != '"')
                {
                    i++;
                    continue;
                }

                i++;
                var start = i;
                while (i < inner.Length && inner[i] != '"')
                {
                    if (inner[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                list.Add(Unescape(inner.Substring(start, Math.Min(i, inner.Length) - start)));
                i++;
            }

            return list.ToArray();
        }

        /// <summary>切出 `"key": [ ... ]` 的方括号内部文本。找不到返回空串。</summary>
        static string ArrayInner(string json, string key)
        {
            var at = KeyAt(json, key);
            if (at < 0)
            {
                return string.Empty;
            }

            var open = json.IndexOf('[', at);
            if (open < 0)
            {
                return string.Empty;
            }

            var depth = 0;
            var inString = false;
            for (var i = open; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return json.Substring(open + 1, i - open - 1);
                    }
                }
            }

            return string.Empty;
        }

        /// <summary>切出 `"key": [ {...}, {...} ]` 里的每个顶层对象文本（含花括号）。</summary>
        static List<string> ObjectBlocks(string json, string key)
        {
            var inner = ArrayInner(json, key);
            var list = new List<string>();
            if (inner.Length == 0)
            {
                return list;
            }

            var depth = 0;
            var start = -1;
            var inString = false;
            for (var i = 0; i < inner.Length; i++)
            {
                var c = inner[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    if (depth == 0)
                    {
                        start = i;
                    }

                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        list.Add(inner.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }

            return list;
        }

        /// <summary>取一个字段的原始文本。这两条回包字段固定，不做通用 JSON 解析。</summary>
        static string Raw(string json, string key)
        {
            var at = KeyAt(json, key);
            if (at < 0)
            {
                return string.Empty;
            }

            var colon = json.IndexOf(':', at);
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

                return Unescape(json.Substring(start, Math.Min(i, json.Length) - start));
            }

            // 对象与数组不在这里展开，调用方用 ArrayInner / ObjectBlocks 切片。
            if (json[i] == '{' || json[i] == '[')
            {
                return string.Empty;
            }

            var end = i;
            while (end < json.Length && json[end] != ',' && json[end] != '}'
                   && json[end] != ']' && !char.IsWhiteSpace(json[end]))
            {
                end++;
            }

            return json.Substring(i, end - i);
        }

        /// <summary>
        /// 解 JSON 的转义。网关把中文写成 `\u67AF\u5C9A\u9053` 这种形态发出来，
        /// 不解开的话屏上会显示成一串反斜杠加十六进制，看着像地名没进来。
        /// </summary>
        static string Unescape(string value)
        {
            if (value == null || value.IndexOf('\\') < 0)
            {
                return value ?? string.Empty;
            }

            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length)
                {
                    sb.Append(value[i]);
                    continue;
                }

                var next = value[i + 1];
                if (next == 'u' && i + 5 < value.Length)
                {
                    var hex = value.Substring(i + 2, 4);
                    if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        sb.Append((char)code);
                        i += 5;
                        continue;
                    }
                }

                switch (next)
                {
                    case 'n':
                        sb.Append('\n');
                        i++;
                        continue;
                    case 't':
                        sb.Append('\t');
                        i++;
                        continue;
                    case 'r':
                        sb.Append('\r');
                        i++;
                        continue;
                    case '"':
                        sb.Append('"');
                        i++;
                        continue;
                    case '\\':
                        sb.Append('\\');
                        i++;
                        continue;
                    case '/':
                        sb.Append('/');
                        i++;
                        continue;
                }

                sb.Append(value[i]);
            }

            return sb.ToString();
        }

        /// <summary>字段名的位置：要求引号后紧跟（允许空白）冒号，不做裸词包含匹配。</summary>
        static int KeyAt(string json, string key)
        {
            var needle = "\"" + key + "\"";
            var from = 0;
            while (true)
            {
                var at = json.IndexOf(needle, from, StringComparison.Ordinal);
                if (at < 0)
                {
                    return -1;
                }

                var i = at + needle.Length;
                while (i < json.Length && char.IsWhiteSpace(json[i]))
                {
                    i++;
                }

                if (i < json.Length && json[i] == ':')
                {
                    return at + needle.Length;
                }

                from = at + needle.Length;
            }
        }
    }
}
