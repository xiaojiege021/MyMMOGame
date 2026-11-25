using System;
using System.Collections.Generic;

namespace Mmo.Net
{
    /// <summary>
    /// 从一条固定形状的回包里取一个字段的裸值。Mmo.Net 不引引擎里的 JSON 组件，
    /// 几条回包都是平铺的「key: 标量」，这里只做这一件事。
    /// </summary>
    public static class JsonField
    {
        public static string Of(string json, string key)
        {
            if (string.IsNullOrEmpty(json))
            {
                return string.Empty;
            }

            // 找「键」而不是「同名值」：键后面必须紧跟冒号（允许空白），
            // 否则 "reason":"duplicate" 这种值会把 duplicate 这个键撞掉。
            var at = -1;
            var probe = 0;
            while (true)
            {
                var hit = json.IndexOf("\"" + key + "\"", probe, StringComparison.Ordinal);
                if (hit < 0)
                {
                    break;
                }

                var after = hit + key.Length + 2;
                while (after < json.Length && char.IsWhiteSpace(json[after]))
                {
                    after++;
                }

                if (after < json.Length && json[after] == ':')
                {
                    at = hit;
                    break;
                }

                probe = hit + 1;
            }

            if (at < 0)
            {
                return string.Empty;
            }

            var colon = json.IndexOf(':', at);
            if (colon < 0)
            {
                return string.Empty;
            }

            var index = colon + 1;
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }

            if (index >= json.Length)
            {
                return string.Empty;
            }

            if (json[index] == '"')
            {
                var close = json.IndexOf('"', index + 1);
                return close < 0 ? string.Empty : json.Substring(index + 1, close - index - 1);
            }

            var stop = index;
            while (stop < json.Length && json[stop] != ',' && json[stop] != '}' && json[stop] != ']')
            {
                stop++;
            }

            return json.Substring(index, stop - index).Trim();
        }

        /// <summary>
        /// 取一个数组字段里的顶层对象原文。只做「定位 [ ... ] 再按顶层 { } 切段」，
        /// 花括号按深度配平并跳过字符串里的括号，所以数组里嵌对象也不会被截断
        /// （摆摊的 `listings`、制作的 `recipes` 都是这种形状）。
        /// </summary>
        public static List<string> Objects(string json, string key)
        {
            var items = new List<string>();
            var inner = Array(json, key);
            if (inner.Length == 0)
            {
                return items;
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
                    continue;
                }

                if (c == '{')
                {
                    if (depth == 0)
                    {
                        start = i;
                    }

                    depth++;
                    continue;
                }

                if (c == '}' && depth > 0)
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        items.Add(inner.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }

            return items;
        }

        /// <summary>数组字段的方括号内部文本；没有这个字段或不是数组就给空串。</summary>
        public static string Array(string json, string key)
        {
            if (string.IsNullOrEmpty(json))
            {
                return string.Empty;
            }

            var needle = "\"" + key + "\"";
            var at = 0;
            while (true)
            {
                at = json.IndexOf(needle, at, StringComparison.Ordinal);
                if (at < 0)
                {
                    return string.Empty;
                }

                var colon = at + needle.Length;
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

            var open = json.IndexOf('[', at + needle.Length);
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
                    continue;
                }

                if (c == ']')
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

        public static int Int(string json, string key)
        {
            int.TryParse(Of(json, key), out var value);
            return value;
        }

        public static long Long(string json, string key)
        {
            long.TryParse(Of(json, key), out var value);
            return value;
        }

        public static ulong ULong(string json, string key)
        {
            ulong.TryParse(Of(json, key), out var value);
            return value;
        }

        public static bool Bool(string json, string key) =>
            string.Equals(Of(json, key), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 取一个字符串字段并解开 JSON 转义。网关把中文写成 `\u89C9\u9192` 这种形态发出来，
        /// 不解开屏上显示的是反斜杠加十六进制，看着像名字没进来
        /// （与 <c>CharacterRealmPhase</c> / <c>HeroCodexEntry</c> 同一道判，U21 起收在这一个 helper 里）。
        /// </summary>
        public static string Text(string json, string key) => Unescape(Of(json, key));

        /// <summary>解 JSON 转义：`\uXXXX` 与几个常见控制字符，其余原样保留。</summary>
        public static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
            {
                return value ?? string.Empty;
            }

            var sb = new System.Text.StringBuilder(value.Length);
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
                    if (int.TryParse(value.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var code))
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
                    default:
                        sb.Append(value[i]);
                        continue;
                }
            }

            return sb.ToString();
        }
    }
}
