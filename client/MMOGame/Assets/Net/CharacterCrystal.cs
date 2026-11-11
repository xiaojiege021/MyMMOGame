using System;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>给界面用的展示结果。类别、部位与镶嵌状态就是服务端回包里的值，不在本地改。</summary>
    public struct CrystalOutcome
    {
        public bool Ok;
        public string Reason;
        public bool Installed;
        public uint Slot;
        public uint Socket;
        public string CrystalItem;
        public string Category;
        public string Part;
        public int SocketCount;
        public int FilledBefore;
        public int Filled;
        public int CrystalLeft;
        public long Gold;
        public int EnhanceLevel;
        public bool Bound;
        public bool Duplicate;
        public string DisplayText;
    }

    /// <summary>
    /// 能量结晶装上 / 拆下的请求与回包拆装（方案第 9 章第 3 层）。
    /// 客户端只发「装备格、结晶背包格、镶嵌位号」或「装备格、镶嵌位号」：
    /// 结晶类别、装备部位、部位限定、固定值、能不能装、结果与事务号都在角色服
    /// 按服务端那一份结晶配置与库里的数据判，装上和拆下各走同一笔事务。
    /// 这里不读结晶表、不比类别与部位、不本地改镶嵌状态，也不往请求里塞这些结果。
    /// Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，不引引擎里的 JSON 组件，
    /// 只拆这一条固定形状的请求与回包。
    /// </summary>
    public static class CharacterCrystal
    {
        public static string InstallBody(ulong characterId, uint slot, uint crystalSlot, uint socket)
        {
            // 身份由网关认令牌与归属复核，这里只是同一套请求体里必须带的角色编号。
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture)
                + ",\"crystal_slot\":" + crystalSlot.ToString(CultureInfo.InvariantCulture)
                + ",\"socket\":" + socket.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static string RemoveBody(ulong characterId, uint slot, uint socket)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture)
                + ",\"socket\":" + socket.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static CrystalOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new CrystalOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var installed = ReadBool(json, "installed");
            var duplicate = ReadBool(json, "duplicate");
            var socket = (uint)Math.Max(0, ReadInt(json, "socket"));
            string text;
            if (!ok)
            {
                text = duplicate ? "重复提交，未生效"
                    : reason == "wrong_part" ? "部位不对，未生效"
                    : "操作未完成：" + reason;
            }
            else if (installed)
            {
                // 方案第 9 章：装上后可拆下，部位由服务端限定，客户端只翻译展示。
                text = "镶嵌成功：第 " + socket.ToString(CultureInfo.InvariantCulture) + " 位";
            }
            else
            {
                text = "拆卸成功：结晶已回到背包";
            }

            return new CrystalOutcome
            {
                Ok = ok,
                Reason = reason,
                Installed = installed,
                Slot = (uint)Math.Max(0, ReadInt(json, "slot")),
                Socket = socket,
                CrystalItem = Raw(json, "crystal_item"),
                Category = Raw(json, "category"),
                Part = Raw(json, "part"),
                SocketCount = ReadInt(json, "socket_count"),
                FilledBefore = ReadInt(json, "filled_before"),
                Filled = ReadInt(json, "filled"),
                CrystalLeft = ReadInt(json, "crystal_left"),
                Gold = ReadLong(json, "gold"),
                EnhanceLevel = ReadInt(json, "enhance_level"),
                Bound = ReadBool(json, "bound"),
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
