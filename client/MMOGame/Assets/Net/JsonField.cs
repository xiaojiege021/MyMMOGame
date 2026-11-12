using System;

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
    }
}
