using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mmo.Net
{
    /// <summary>
    /// 状态码到档位的唯一映射、错误文本，以及字段定位与转义解开。
    /// </summary>
    public static partial class WorldEntry
    {
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
