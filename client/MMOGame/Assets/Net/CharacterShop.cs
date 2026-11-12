using System;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>商城购买的回包。标价、扣款、余额、发货物与绑定态都是服务端回包里的值。</summary>
    public struct ShopOutcome
    {
        public bool Ok;
        public string Reason;
        public string ItemId;
        public string Kind;
        public string Name;
        public long PriceYuanbao;
        public string Currency;
        public long Balance;
        public bool Enough;
        public long ChargedYuanbao;
        public bool Delivered;
        public string State;
        public string DeliveryItemId;
        public uint BagSlot;
        public bool Bound;
        public bool Duplicate;
        public string DisplayText;
    }

    /// <summary>
    /// 商城购买的请求与回包拆装。请求里只有商品编号：标价、扣多少元宝、发什么、
    /// 绑定态、清单外拒绝全在角色服按清单判；这里不算价、不判够不够。
    /// </summary>
    public static class CharacterShop
    {
        public static string RequestBody(ulong characterId, string itemId) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
            + ",\"item_id\":\"" + Escape(itemId ?? string.Empty) + "\"}";

        public static ShopOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new ShopOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var delivered = ReadBool(json, "delivered");
            var charged = ReadLong(json, "charged_yuanbao");
            var bound = ReadBool(json, "bound");
            string text;
            if (!ok)
            {
                text = "没买成：" + reason;
            }
            else
            {
                text = "已买到：" + Raw(json, "name") + " · 扣 " + charged + " 元宝"
                    + " · 第 " + ReadLong(json, "bag_slot") + " 格"
                    + (bound ? " · 绑定" : "");
            }

            return new ShopOutcome
            {
                Ok = ok,
                Reason = reason,
                ItemId = Raw(json, "item_id"),
                Kind = Raw(json, "kind"),
                Name = Raw(json, "name"),
                PriceYuanbao = ReadLong(json, "price_yuanbao"),
                Currency = Raw(json, "currency"),
                Balance = ReadLong(json, "balance"),
                Enough = ReadBool(json, "enough"),
                ChargedYuanbao = charged,
                Delivered = delivered,
                State = Raw(json, "state"),
                DeliveryItemId = Raw(json, "delivery_item_id"),
                BagSlot = (uint)ReadLong(json, "bag_slot"),
                Bound = bound,
                Duplicate = ReadBool(json, "duplicate"),
                DisplayText = text,
            };
        }

        static bool ReadBool(string json, string key) =>
            string.Equals(Raw(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static long ReadLong(string json, string key)
        {
            long value;
            return long.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }

        static string Raw(string json, string key) => JsonField.Of(json, key);

        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
