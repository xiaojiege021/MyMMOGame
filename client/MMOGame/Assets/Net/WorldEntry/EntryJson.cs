using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mmo.Net
{
    /// <summary>
    /// 固定形状回包的取字段工具：引号 / 布尔 / 数字 / 数组 / 对象块切片。
    /// </summary>
    public static partial class WorldEntry
    {
        static string Quote(string value) =>
            "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static bool ReadBool(string json, string key) =>
            string.Equals(Raw(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static uint ReadUInt(string json, string key)
        {
            uint.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        static int ReadInt(string json, string key)
        {
            int.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
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
    }
}
