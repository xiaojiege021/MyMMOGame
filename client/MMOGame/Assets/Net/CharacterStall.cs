using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>摊上的一件在售（B5）。价格、数量、绑定态都是角色服从库里读出来的。</summary>
    public struct StallListingView
    {
        public ulong ListingId;
        public int Slot;
        public string ItemId;
        public string ItemName;
        public int Count;
        public long PriceGold;
        public bool Bound;
    }

    /// <summary>地表上的一块摊位（世界服只存摘要，不读角色库）。</summary>
    public struct StallRowView
    {
        public string StallId;
        public ulong OwnerId;
        public string OwnerName;
        public string RegionId;
        public string Title;
        public int LiveListings;

        /// <summary>坐标只拼串展示，不参与任何判定。</summary>
        public string PosText;
    }

    public struct StallOpenOutcome
    {
        public bool Ok;
        public string Reason;
        public string StallId;
        public string OwnerName;
        public string RegionId;
        public string Title;
        public int LiveListings;
        public int Slots;
        public int MinLevel;
        public int TaxRatePct;
        public bool Shown;

        public string DisplayText;
    }

    public struct StallCloseOutcome
    {
        public bool Ok;
        public string Reason;
        public string StallId;
        public int CancelledListings;

        public string DisplayText;
    }

    public struct StallListOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong ListingId;
        public string StallId;
        public int Slot;
        public string ItemId;
        public int Count;
        public long PriceGold;
        public int LiveListings;
        public int Slots;

        public string DisplayText;
    }

    public struct StallViewOutcome
    {
        public bool Ok;
        public bool Found;
        public string Reason;
        public string StallId;
        public ulong OwnerId;
        public string OwnerName;
        public string RegionId;
        public string Title;
        public string State;
        public int LiveListings;
        public int Slots;
        public List<StallListingView> Listings;

        public string DisplayText;
    }

    public struct StallBrowseOutcome
    {
        public bool Ok;
        public string Reason;
        public int Count;
        public string RegionId;
        public List<StallRowView> Stalls;

        public string DisplayText;
    }

    public struct StallBuyOutcome
    {
        public bool Ok;
        public string Reason;
        public bool Duplicate;
        public ulong ListingId;
        public string StallId;
        public long PriceGold;
        public long TaxGold;
        public long NetGold;
        public long BuyerGold;
        public long SellerGold;
        public int TaxRatePct;
        public string ItemId;
        public int Count;
        public int LiveListings;

        public string DisplayText;
    }

    /// <summary>
    /// B5 摆摊的六条 HTTP 口：开摊 / 关摊 / 上架 / 看摊 / 地表摊位列表 / 成交。
    ///
    /// 摊位在世界服显示（只有内存摘要），成交仍提交拍卖那一笔角色服事务；
    /// 税按挂单 channel 取 `stall.json` 的税率，世界服不碰背包。客户端只发格号、
    /// 数量、价格与招牌，够不够格、8 格满没满、绑定能不能上架、税多少全看回包；
    /// 事务号由网关生成，请求体里不带。
    /// </summary>
    public static class CharacterStall
    {
        public const string OpenPath = "/stall/open";

        /// <summary>开摊：地表、坐标与招牌都由界面带，角色服只记账、世界服只显示。</summary>
        public static string OpenBody(ulong characterId, string regionId, string title)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"region_id\":\"" + Escape(regionId) + "\""
                + ",\"pos_x\":0,\"pos_y\":0"
                + ",\"title\":\"" + Escape(title) + "\"}";
        }

        public const string ClosePath = "/stall/close";

        public static string CloseBody(ulong characterId, string stallId)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"stall_id\":\"" + Escape(stallId) + "\"}";
        }

        public const string ListPath = "/stall/list";

        /// <summary>上架一件：格号、数量、单价；能不能上架、卖多少税都在角色服判。</summary>
        public static string ListingBody(ulong characterId, string stallId, int slot, int count, long priceGold)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"stall_id\":\"" + Escape(stallId) + "\""
                + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture)
                + ",\"count\":" + count.ToString(CultureInfo.InvariantCulture)
                + ",\"price_gold\":" + priceGold.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public const string ViewPath = "/stall/view";

        public static string ViewQuery(string stallId) =>
            ViewPath + "?stall_id=" + Uri.EscapeDataString(stallId ?? string.Empty);

        public const string BrowsePath = "/stalls";

        public static string BrowseQuery(string regionId) =>
            BrowsePath + "?region_id=" + Uri.EscapeDataString(regionId ?? string.Empty);

        public const string BuyPath = "/stall/buy";

        /// <summary>买一件：只发挂单编号；钱、税、净额全看回包。</summary>
        public static string BuyBody(ulong characterId, ulong listingId)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"listing_id\":" + listingId.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static StallOpenOutcome ParseOpen(string json)
        {
            var ok = JsonField.Bool(json, "ok");
            var reason = JsonField.Of(json, "reason");
            return new StallOpenOutcome
            {
                Ok = ok,
                Reason = reason,
                StallId = JsonField.Of(json, "stall_id"),
                OwnerName = JsonField.Of(json, "owner_name"),
                RegionId = JsonField.Of(json, "region_id"),
                Title = JsonField.Of(json, "title"),
                LiveListings = JsonField.Int(json, "live_listings"),
                Slots = JsonField.Int(json, "slots"),
                MinLevel = JsonField.Int(json, "min_level"),
                TaxRatePct = JsonField.Int(json, "tax_rate_pct"),
                Shown = JsonField.Bool(json, "shown"),
                DisplayText = ok
                    ? (JsonField.Bool(json, "shown") ? "摊位已开，地表上能看到" : "摊位已开，地表还没刷新出来")
                    : "开摊被拒：" + reason,
            };
        }

        public static StallCloseOutcome ParseClose(string json)
        {
            var ok = JsonField.Bool(json, "ok");
            var reason = JsonField.Of(json, "reason");
            var cancelled = JsonField.Int(json, "cancelled_listings");
            return new StallCloseOutcome
            {
                Ok = ok,
                Reason = reason,
                StallId = JsonField.Of(json, "stall_id"),
                CancelledListings = cancelled,
                DisplayText = ok
                    ? "摊位已关，撤下 " + cancelled.ToString(CultureInfo.InvariantCulture) + " 件"
                    : "关摊被拒：" + reason,
            };
        }

        public static StallListOutcome ParseListing(string json)
        {
            var ok = JsonField.Bool(json, "ok");
            var reason = JsonField.Of(json, "reason");
            return new StallListOutcome
            {
                Ok = ok,
                Reason = reason,
                ListingId = JsonField.ULong(json, "listing_id"),
                StallId = JsonField.Of(json, "stall_id"),
                Slot = JsonField.Int(json, "slot"),
                ItemId = JsonField.Of(json, "item_id"),
                Count = JsonField.Int(json, "count"),
                PriceGold = JsonField.Long(json, "price_gold"),
                LiveListings = JsonField.Int(json, "live_listings"),
                Slots = JsonField.Int(json, "slots"),
                DisplayText = ok
                    ? "已上架，在售 " + JsonField.Int(json, "live_listings").ToString(CultureInfo.InvariantCulture) + " 件"
                    : "上架被拒：" + reason,
            };
        }

        public static StallViewOutcome ParseView(string json)
        {
            var found = JsonField.Bool(json, "found");
            var outcome = new StallViewOutcome
            {
                Ok = found,
                Found = found,
                Reason = found ? string.Empty : "stall_not_found",
                StallId = JsonField.Of(json, "stall_id"),
                OwnerId = JsonField.ULong(json, "owner_id"),
                OwnerName = JsonField.Of(json, "owner_name"),
                RegionId = JsonField.Of(json, "region_id"),
                Title = JsonField.Of(json, "title"),
                State = JsonField.Of(json, "state"),
                LiveListings = JsonField.Int(json, "live_listings"),
                Slots = JsonField.Int(json, "slots"),
                Listings = new List<StallListingView>(),
            };

            foreach (var one in JsonField.Objects(json, "listings"))
            {
                outcome.Listings.Add(new StallListingView
                {
                    ListingId = JsonField.ULong(one, "listing_id"),
                    Slot = JsonField.Int(one, "slot"),
                    ItemId = JsonField.Of(one, "item_id"),
                    ItemName = JsonField.Of(one, "item_name"),
                    Count = JsonField.Int(one, "count"),
                    PriceGold = JsonField.Long(one, "price_gold"),
                    Bound = JsonField.Bool(one, "bound"),
                });
            }

            outcome.DisplayText = found
                ? "摊位在售 " + outcome.Listings.Count.ToString(CultureInfo.InvariantCulture) + " 件"
                : "这个摊位不在了";
            return outcome;
        }

        public static StallBrowseOutcome ParseBrowse(string json)
        {
            var outcome = new StallBrowseOutcome
            {
                Ok = true,
                Reason = string.Empty,
                Count = JsonField.Int(json, "count"),
                RegionId = JsonField.Of(json, "region_id"),
                Stalls = new List<StallRowView>(),
            };

            foreach (var one in JsonField.Objects(json, "stalls"))
            {
                outcome.Stalls.Add(new StallRowView
                {
                    StallId = JsonField.Of(one, "stall_id"),
                    OwnerId = JsonField.ULong(one, "owner_id"),
                    OwnerName = JsonField.Of(one, "owner_name"),
                    RegionId = JsonField.Of(one, "region_id"),
                    Title = JsonField.Of(one, "title"),
                    LiveListings = JsonField.Int(one, "live_listings"),
                    PosText = JsonField.Of(one, "pos_x") + "," + JsonField.Of(one, "pos_y"),
                });
            }

            // 名次与顺序照回包：不按在售件数重排。
            outcome.DisplayText = "这块地表有 " + outcome.Stalls.Count.ToString(CultureInfo.InvariantCulture) + " 个摊";
            return outcome;
        }

        public static StallBuyOutcome ParseBuy(string json)
        {
            var ok = JsonField.Bool(json, "ok");
            var reason = JsonField.Of(json, "reason");
            return new StallBuyOutcome
            {
                Ok = ok,
                Reason = reason,
                Duplicate = JsonField.Bool(json, "duplicate"),
                ListingId = JsonField.ULong(json, "listing_id"),
                StallId = JsonField.Of(json, "stall_id"),
                PriceGold = JsonField.Long(json, "price_gold"),
                TaxGold = JsonField.Long(json, "tax_gold"),
                NetGold = JsonField.Long(json, "net_gold"),
                BuyerGold = JsonField.Long(json, "buyer_gold"),
                SellerGold = JsonField.Long(json, "seller_gold"),
                TaxRatePct = JsonField.Int(json, "tax_rate_pct"),
                ItemId = JsonField.Of(json, "item_id"),
                Count = JsonField.Int(json, "count"),
                LiveListings = JsonField.Int(json, "live_listings"),
                DisplayText = ok
                    ? (JsonField.Bool(json, "duplicate") ? "这笔已经成交过，不重复扣钱" : "成交，税 " + JsonField.Long(json, "tax_gold").ToString(CultureInfo.InvariantCulture))
                    : "购买被拒：" + reason,
            };
        }

        static string Escape(string value) =>
            (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
