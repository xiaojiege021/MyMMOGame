using System;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>建单与回调的展示结果。元宝数、订单状态与余额都是回包里的值。</summary>
    public struct RechargeOrderOutcome
    {
        public bool Ok;
        public string Reason;
        public string OrderId;
        public string ProductId;
        public long Yuanbao;
        public string Status;
        public string DisplayText;
    }

    public struct RechargeSettleOutcome
    {
        public bool Ok;
        public string Reason;
        public string OrderId;
        public string Status;
        public long Yuanbao;
        public long Balance;
        public long PaidAtMs;
        public bool Duplicate;
        public string DisplayText;
    }

    /// <summary>
    /// 内部充值的请求与回包拆装（P1-5d）。P0 不接真实渠道：建单只带商品编号，
    /// 回调只带订单号；元宝数与「一笔只发一次」都在角色服判。
    /// </summary>
    public static class CharacterRecharge
    {
        public static string OrderBody(ulong characterId, string productId) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
            + ",\"product_id\":\"" + Escape(productId ?? string.Empty) + "\"}";

        public static string CallbackBody(string orderId) =>
            "{\"order_id\":\"" + Escape(orderId ?? string.Empty) + "\"}";

        public static RechargeOrderOutcome ParseOrder(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return new RechargeOrderOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = Bool(json, "ok");
            var reason = JsonField.Of(json, "reason");
            return new RechargeOrderOutcome
            {
                Ok = ok,
                Reason = reason,
                OrderId = JsonField.Of(json, "order_id"),
                ProductId = JsonField.Of(json, "product_id"),
                Yuanbao = Long(json, "yuanbao"),
                Status = JsonField.Of(json, "status"),
                DisplayText = ok ? "订单已建：" + JsonField.Of(json, "order_id") : "没建成：" + reason,
            };
        }

        public static RechargeSettleOutcome ParseSettle(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return new RechargeSettleOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = Bool(json, "ok");
            var reason = JsonField.Of(json, "reason");
            var duplicate = Bool(json, "duplicate");
            string text;
            if (!ok)
            {
                text = "没结算：" + reason;
            }
            else
            {
                text = "已结算：+" + Long(json, "yuanbao") + " 元宝 · 余额 " + Long(json, "balance");
            }

            return new RechargeSettleOutcome
            {
                Ok = ok,
                Reason = reason,
                OrderId = JsonField.Of(json, "order_id"),
                Status = JsonField.Of(json, "status"),
                Yuanbao = Long(json, "yuanbao"),
                Balance = Long(json, "balance"),
                PaidAtMs = Long(json, "paid_at_ms"),
                Duplicate = duplicate,
                DisplayText = text,
            };
        }

        static bool Bool(string json, string key) =>
            string.Equals(JsonField.Of(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static long Long(string json, string key)
        {
            long value;
            return long.TryParse(JsonField.Of(json, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }

        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
