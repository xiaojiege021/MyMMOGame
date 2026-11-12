using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>背包里的一格。数量与绑定态都是回包里的值，界面不改。</summary>
    public struct BagSlotView
    {
        public uint Slot;
        public string ItemId;
        public int Count;
        public bool Bound;
    }

    /// <summary>
    /// 背包只读回包（`GET /inventory`）。U5 的角色与装备、背包、强化、转移、结晶五屏
    /// 都从这里拿格号与物品编号——客户端不读 `config/items.json`，也不本地判种类。
    /// </summary>
    public struct BagOutcome
    {
        public bool Ok;
        public string Reason;
        public List<BagSlotView> Slots;
        public int BagSlots;
        public string CharacterName;
        public string Realm;

        public static BagOutcome Failure(string reason) => new BagOutcome
        {
            Ok = false,
            Reason = reason,
            Slots = new List<BagSlotView>(),
        };
    }

    /// <summary>
    /// 背包回包的拆装。Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，
    /// 只拆这一条固定形状的回包，不引引擎里的 JSON 组件。
    /// </summary>
    public static class CharacterBag
    {
        /// <summary>背包请求。只带角色号，分页与筛选都在界面侧按已有回包做。</summary>
        public static string RequestPath(ulong characterId) =>
            "/inventory?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        public static BagOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || json.IndexOf("\"slots\"", StringComparison.Ordinal) < 0)
            {
                return BagOutcome.Failure("bad_reply");
            }

            var slots = new List<BagSlotView>();
            var start = json.IndexOf("\"slots\"", StringComparison.Ordinal);
            start = json.IndexOf('[', start);
            var end = start >= 0 ? json.IndexOf(']', start) : -1;
            if (start < 0 || end < 0)
            {
                return BagOutcome.Failure("bad_reply");
            }

            var at = start;
            while (true)
            {
                var open = json.IndexOf('{', at);
                if (open < 0 || open > end)
                {
                    break;
                }

                var close = json.IndexOf('}', open);
                if (close < 0 || close > end)
                {
                    break;
                }

                var one = json.Substring(open, close - open + 1);
                slots.Add(new BagSlotView
                {
                    Slot = (uint)ReadLong(one, "slot"),
                    ItemId = ReadRaw(one, "item_id"),
                    Count = (int)ReadLong(one, "count"),
                    Bound = ReadBool(one, "bound"),
                });
                at = close + 1;
            }

            slots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            return new BagOutcome
            {
                Ok = true,
                Reason = string.Empty,
                Slots = slots,
                BagSlots = (int)ReadLong(json, "bag_slots"),
                CharacterName = ReadRaw(json, "character_name"),
                Realm = ReadRaw(json, "realm"),
            };
        }

        static bool ReadBool(string json, string key)
        {
            var raw = ReadRaw(json, key);
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        static long ReadLong(string json, string key)
        {
            var raw = ReadRaw(json, key);
            long value;
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }

        static string ReadRaw(string json, string key)
        {
            var at = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
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
                if (close < 0)
                {
                    return string.Empty;
                }

                return json.Substring(index + 1, close - index - 1);
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
