using System;
using System.Globalization;
using System.Text;

namespace Mmo.Net
{
    /// <summary>
    /// 家园币商城回包（N11 的 `POST /home-coin/shop/buy`）。
    ///
    /// 这次扣了多少、余额多少、有没有真发货，全部来自角色服；标价与发货种类在服务端配置里，
    /// 客户端不读、不算。请求体只带角色号与商品编号，事务号由网关生成。
    /// </summary>
    public struct HomeCoinShopOutcome
    {
        public bool Ok;
        public string Reason;
        public string ItemId;
        public string Kind;
        public string Currency;
        public long Balance;
        public long ChargedHomeCoin;
        public bool Delivered;
        public bool Duplicate;
        public string DisplayText;

        public static HomeCoinShopOutcome Failure(string reason) => new HomeCoinShopOutcome
        {
            Ok = false,
            Reason = reason ?? string.Empty,
            DisplayText = "没读到：" + (reason ?? string.Empty),
        };
    }

    /// <summary>`/home-coin/shop/buy` 的路径、请求体与回包拆装。</summary>
    public static class HomeCoinShopEntry
    {
        public const string BuyPath = "/home-coin/shop/buy";

        public static string BuyBody(ulong characterId, string itemId)
        {
            var sb = new StringBuilder(64);
            sb.Append("{\"character_id\":").Append(characterId.ToString(CultureInfo.InvariantCulture))
                .Append(",\"item_id\":\"").Append(Escape(itemId)).Append("\"}");
            return sb.ToString();
        }

        public static HomeCoinShopOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return HomeCoinShopOutcome.Failure("bad_reply");
            }

            var ok = string.Equals(JsonField.Of(json, "ok"), "true", StringComparison.OrdinalIgnoreCase);
            var reason = JsonField.Of(json, "reason");
            var itemId = JsonField.Of(json, "item_id");
            var charged = ReadLong(json, "charged_home_coin");
            var balance = ReadLong(json, "balance");
            var delivered = string.Equals(JsonField.Of(json, "delivered"), "true",
                StringComparison.OrdinalIgnoreCase);
            var text = ok
                ? ("已买 " + itemId + " · 扣 " + charged + " 家园币 · 余额 " + balance
                   + (delivered ? string.Empty : " · 未发货"))
                : ("没成：" + (reason.Length == 0 ? "未知原因" : reason));

            return new HomeCoinShopOutcome
            {
                Ok = ok,
                Reason = reason,
                ItemId = itemId,
                Kind = JsonField.Of(json, "kind"),
                Currency = JsonField.Of(json, "currency"),
                Balance = balance,
                ChargedHomeCoin = charged,
                Delivered = delivered,
                Duplicate = string.Equals(JsonField.Of(json, "duplicate"), "true",
                    StringComparison.OrdinalIgnoreCase),
                DisplayText = text,
            };
        }

        static long ReadLong(string json, string key)
        {
            long value;
            return long.TryParse(JsonField.Of(json, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
