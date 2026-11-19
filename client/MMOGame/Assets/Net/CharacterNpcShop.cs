using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>货架上一件商品。价格、今日剩多少、今天买了几件全是服务端回包里的值。</summary>
    public struct NpcShopGoodsRow
    {
        public string ItemId;
        public string Name;
        public long Price;
        public long StockLeft;
        public bool Unlimited;
        public long BoughtToday;
        public long LimitPerCharacter;
        public bool Enough;
        public bool SoldOut;
        public bool LimitReached;
    }

    /// <summary>
    /// NPC 商店面板的回包。货架顺序与配置一致；这里是「服务端说了算」的那一份，
    /// 客户端不拿它反算任何东西。
    /// </summary>
    public struct NpcShopOutcome
    {
        public bool Ok;
        public string Reason;
        public string ShopId;
        public string NpcId;
        public string Currency;
        public long Balance;
        public long SellBalance;
        public long RepairPricePerPoint;
        public List<NpcShopGoodsRow> Goods;
        public long BuybackCount;
        public long WornCount;
        public string DisplayText;
    }

    /// <summary>买 / 卖 / 回购 / 修理共用的一张回包：四件事的字段并集，没生效的一律是 0。</summary>
    public struct NpcActionOutcome
    {
        public bool Ok;
        public string Reason;
        public string ItemId;
        public string Name;
        public string Currency;
        public long Price;
        public long Balance;
        public long StockLeft;
        public long BoughtToday;
        public long Gained;
        public long Paid;
        public long Charged;
        public long BuybackId;
        public long BagSlot;
        public long Count;
        public string DisplayText;
    }

    /// <summary>
    /// NPC 商店的请求与回包拆装（K2+Q13）。请求里只有角色号、商店编号、格号与件数：
    /// 标价、库存、回收价、修理单价全在服务端按 config/npc_shop.json 与库里的计数判，
    /// 这里不算价、不判够不够、不改背包。
    /// </summary>
    public static class CharacterNpcShop
    {
        public static string DescribePath(ulong characterId, string shopId) =>
            "/shop/npc?character_id=" + characterId.ToString(CultureInfo.InvariantCulture)
            + "&shop_id=" + Uri.EscapeDataString(shopId ?? string.Empty);

        public static string BuyBody(ulong characterId, string shopId, string itemId) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
            + ",\"shop_id\":\"" + Escape(shopId) + "\""
            + ",\"item_id\":\"" + Escape(itemId) + "\"}";

        public static string SellBody(ulong characterId, string shopId, int bagSlot, int count) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
            + ",\"shop_id\":\"" + Escape(shopId) + "\""
            + ",\"bag_slot\":" + bagSlot.ToString(CultureInfo.InvariantCulture)
            + ",\"count\":" + count.ToString(CultureInfo.InvariantCulture) + "}";

        public static string BuybackBody(ulong characterId, string shopId, long buybackId) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
            + ",\"shop_id\":\"" + Escape(shopId) + "\""
            + ",\"buyback_id\":" + buybackId.ToString(CultureInfo.InvariantCulture) + "}";

        public static string RepairBody(ulong characterId) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture) + "}";

        public static NpcShopOutcome ParseDescribe(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return new NpcShopOutcome
                {
                    Ok = false,
                    Reason = "bad_reply",
                    Goods = new List<NpcShopGoodsRow>(),
                    DisplayText = "回包读不出来",
                };
            }

            var goods = new List<NpcShopGoodsRow>();
            foreach (var obj in Objects(json, "goods"))
            {
                goods.Add(new NpcShopGoodsRow
                {
                    ItemId = JsonField.Of(obj, "item_id"),
                    Name = JsonField.Of(obj, "name"),
                    Price = ReadLong(obj, "price"),
                    StockLeft = ReadLong(obj, "stock_left"),
                    Unlimited = ReadBool(obj, "unlimited"),
                    BoughtToday = ReadLong(obj, "bought_today"),
                    LimitPerCharacter = ReadLong(obj, "limit_per_character"),
                    Enough = ReadBool(obj, "enough"),
                    SoldOut = ReadBool(obj, "sold_out"),
                    LimitReached = ReadBool(obj, "limit_reached"),
                });
            }

            var ok = ReadBool(json, "ok");
            var reason = JsonField.Of(json, "reason");
            var shopId = JsonField.Of(json, "shop_id");

            return new NpcShopOutcome
            {
                Ok = ok,
                Reason = reason,
                ShopId = shopId,
                NpcId = JsonField.Of(json, "npc_id"),
                Currency = JsonField.Of(json, "currency"),
                Balance = ReadLong(json, "balance"),
                SellBalance = ReadLong(json, "sell_balance"),
                RepairPricePerPoint = ReadLong(json, "repair_price_per_point"),
                Goods = goods,
                BuybackCount = CountObjects(json, "buyback"),
                WornCount = CountObjects(json, "worn"),
                DisplayText = ok
                    ? "货架 " + goods.Count + " 件 · " + JsonField.Of(json, "currency") + " "
                      + ReadLong(json, "balance")
                    : "读不到货架：" + (reason.Length == 0 ? "unknown" : reason),
            };
        }

        public static NpcActionOutcome ParseAction(string json, string what)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return new NpcActionOutcome
                {
                    Ok = false,
                    Reason = "bad_reply",
                    DisplayText = "回包读不出来",
                };
            }

            var ok = ReadBool(json, "ok");
            var reason = JsonField.Of(json, "reason");
            var name = JsonField.Of(json, "name");
            var bought = ReadLong(json, "price");
            var gained = ReadLong(json, "gained");
            var paid = ReadLong(json, "paid");
            var charged = ReadLong(json, "charged");

            string text;
            if (!ok)
            {
                text = what + "没成：" + (reason.Length == 0 ? "unknown" : reason);
            }
            else
            {
                text = what + "成交：" + name
                    + (gained > 0 ? " · 得 " + gained : string.Empty)
                    + (paid > 0 ? " · 付 " + paid : string.Empty)
                    + (charged > 0 ? " · 付 " + charged : string.Empty)
                    + (bought > 0 ? " · 标价 " + bought : string.Empty)
                    + " · 余额 " + ReadLong(json, "balance");
            }

            return new NpcActionOutcome
            {
                Ok = ok,
                Reason = reason,
                ItemId = JsonField.Of(json, "item_id"),
                Name = name,
                Currency = JsonField.Of(json, "currency"),
                Price = bought,
                Balance = ReadLong(json, "balance"),
                StockLeft = ReadLong(json, "stock_left"),
                BoughtToday = ReadLong(json, "bought_today"),
                Gained = gained,
                Paid = paid,
                Charged = charged,
                BuybackId = ReadLong(json, "buyback_id"),
                BagSlot = ReadLong(json, "bag_slot"),
                Count = ReadLong(json, "count"),
                DisplayText = text,
            };
        }

        static bool ReadBool(string json, string key) =>
            string.Equals(JsonField.Of(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static long ReadLong(string json, string key)
        {
            long value;
            return long.TryParse(JsonField.Of(json, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }

        /// <summary>
        /// 把 `"goods":[ {...}, {...} ]` 拆成一段段对象文本。只按花括号配平，不引第三方解析器
        /// ——客户端的回包都是服务端自己写的扁平结构，够用且不依赖反射。
        /// </summary>
        static List<string> Objects(string json, string key)
        {
            var list = new List<string>();
            var body = ArrayBody(json, key);
            if (body == null)
            {
                return list;
            }

            var depth = 0;
            var start = -1;
            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c == '{')
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
                        list.Add(body.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }

            return list;
        }

        static long CountObjects(string json, string key) => Objects(json, key).Count;

        static string ArrayBody(string json, string key)
        {
            var marker = "\"" + key + "\"";
            var at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            var open = json.IndexOf('[', at);
            if (open < 0)
            {
                return null;
            }

            var close = json.IndexOf(']', open);
            return close < 0 ? null : json.Substring(open + 1, close - open - 1);
        }

        static string Escape(string text) =>
            (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
