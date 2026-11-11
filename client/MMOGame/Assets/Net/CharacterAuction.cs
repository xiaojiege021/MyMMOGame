using System;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>元宝寄售的展示结果。元宝数量、标价、税与到账都是服务端回包里的值。</summary>
    public struct IngotOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong ListingId;
        public long IngotAmount;
        public long PriceGold;
        public long TaxGold;
        public long NetGold;
        public long BuyerGold;
        public long SellerGold;
        public long BuyerIngot;
        public long SellerIngot;
        public int TaxRatePct;
        public string State;
        public bool Duplicate;
        public string DisplayText;
    }

    /// <summary>给界面用的展示结果。税额与到账就是服务端回包里的值，不在本地改。</summary>
    public struct AuctionOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong ListingId;
        public long PriceGold;
        public long TaxGold;
        public long NetGold;
        public long BuyerGold;
        public long SellerGold;
        public int TaxRatePct;
        public string ItemId;
        public uint Count;
        public string State;
        public bool Duplicate;
        public string DisplayText;
    }

    /// <summary>
    /// 拍卖挂单与购买的请求、回包拆装（方案第 12 章）。挂单只发角色、格位、
    /// 数量与金币标价，购买只发角色与挂单号：税率、税额、卖方、物品编号、
    /// 到账、成交状态与事务号都在服务端，客户端一个都不算也不塞进请求。
    /// Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，只拆这两条固定形状。
    /// </summary>
    public static class CharacterAuction
    {
        public static string ListBody(ulong characterId, uint slot, uint count, long priceGold)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture)
                + ",\"count\":" + count.ToString(CultureInfo.InvariantCulture)
                + ",\"price_gold\":" + priceGold.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static string BuyBody(ulong characterId, ulong listingId)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"listing_id\":" + listingId.ToString(CultureInfo.InvariantCulture) + "}";
        }

        /// <summary>元宝寄售挂单：只发角色、元宝数量与金币总价。</summary>
        public static string IngotListBody(ulong characterId, long ingotAmount, long priceGold)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"ingot_amount\":" + ingotAmount.ToString(CultureInfo.InvariantCulture)
                + ",\"price_gold\":" + priceGold.ToString(CultureInfo.InvariantCulture) + "}";
        }

        /// <summary>买元宝：只发角色与挂单号。</summary>
        public static string IngotBuyBody(ulong characterId, ulong listingId)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"listing_id\":" + listingId.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static AuctionOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new AuctionOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var duplicate = ReadBool(json, "duplicate");
            string text;
            if (!ok)
            {
                text = duplicate ? "重复提交，未生效" : "成交未完成：" + reason;
            }
            else
            {
                // 税直接销毁，不进任何背包；客户端只把服务端给的数翻译出来。
                text = "成交：标价 " + ReadLong(json, "price_gold").ToString(CultureInfo.InvariantCulture)
                    + "，销毁 " + ReadLong(json, "tax_gold").ToString(CultureInfo.InvariantCulture)
                    + "，卖方到账 " + ReadLong(json, "net_gold").ToString(CultureInfo.InvariantCulture);
            }

            return new AuctionOutcome
            {
                Ok = ok,
                Reason = reason,
                ListingId = (ulong)Math.Max(0, ReadLong(json, "listing_id")),
                PriceGold = ReadLong(json, "price_gold"),
                TaxGold = ReadLong(json, "tax_gold"),
                NetGold = ReadLong(json, "net_gold"),
                BuyerGold = ReadLong(json, "buyer_gold"),
                SellerGold = ReadLong(json, "seller_gold"),
                TaxRatePct = ReadInt(json, "tax_rate_pct"),
                ItemId = Raw(json, "item_id"),
                Count = (uint)Math.Max(0, ReadInt(json, "count")),
                State = Raw(json, "state"),
                Duplicate = duplicate,
                DisplayText = text,
            };
        }

        /// <summary>元宝寄售回包拆装。元宝与金币都只翻译服务端给的数，不在本地算。</summary>
        public static IngotOutcome ParseIngot(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new IngotOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var duplicate = ReadBool(json, "duplicate");
            string text;
            if (!ok)
            {
                text = duplicate ? "重复提交，未生效" : "寄售未完成：" + reason;
            }
            else
            {
                text = "成交：挂 " + ReadLong(json, "ingot_amount").ToString(CultureInfo.InvariantCulture)
                    + " 元宝，标价 " + ReadLong(json, "price_gold").ToString(CultureInfo.InvariantCulture)
                    + "，销毁 " + ReadLong(json, "tax_gold").ToString(CultureInfo.InvariantCulture)
                    + "，卖方到账 " + ReadLong(json, "net_gold").ToString(CultureInfo.InvariantCulture);
            }

            return new IngotOutcome
            {
                Ok = ok,
                Reason = reason,
                ListingId = (ulong)Math.Max(0, ReadLong(json, "listing_id")),
                IngotAmount = ReadLong(json, "ingot_amount"),
                PriceGold = ReadLong(json, "price_gold"),
                TaxGold = ReadLong(json, "tax_gold"),
                NetGold = ReadLong(json, "net_gold"),
                BuyerGold = ReadLong(json, "buyer_gold"),
                SellerGold = ReadLong(json, "seller_gold"),
                BuyerIngot = ReadLong(json, "buyer_ingot"),
                SellerIngot = ReadLong(json, "seller_ingot"),
                TaxRatePct = ReadInt(json, "tax_rate_pct"),
                State = Raw(json, "state"),
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
            // 另一个字段名，拿裸子串找会先撞上那个值，把字段读错。
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
