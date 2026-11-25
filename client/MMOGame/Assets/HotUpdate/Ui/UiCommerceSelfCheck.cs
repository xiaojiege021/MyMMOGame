#if UNITY_EDITOR
using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U6 四屏自检：余额 / 商城扣款 / 清单外拒绝 / 拍卖税 / 寄售 / 充值只发一次，全部只读回包；
    /// 四屏都能建起来；切皮肤只换颜色。
    /// </summary>
    public static class UiCommerceSelfCheck
    {
        static bool _failed;

        static int _shopCalls;

        static int _settleCalls;

        /// <summary>摆摊的自检夹具状态：开摊与上架的请求体各记一份，买第二次回「没了」。</summary>
        static string _stallOpenBody = string.Empty;

        static string _stallListBody = string.Empty;

        static int _stallBuyCalls;

        public static bool Run()
        {
            _failed = false;
            _shopCalls = 0;
            _settleCalls = 0;

            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            var flow = UiCommerceFlow.Ensure(shell);
            flow.SetSender((path, body, token, done) =>
            {
                if (path.IndexOf("/currency", StringComparison.Ordinal) >= 0)
                {
                    done(200, CurrencyJson);
                    return;
                }

                if (path.IndexOf("/inventory", StringComparison.Ordinal) >= 0)
                {
                    done(200, BagJson);
                    return;
                }

                if (path.IndexOf("/shop/buy", StringComparison.Ordinal) >= 0)
                {
                    _shopCalls++;
                    done(200, _shopCalls == 1 ? ShopOkJson : ShopRejectJson);
                    return;
                }

                if (path.IndexOf("/recharge/order", StringComparison.Ordinal) >= 0)
                {
                    done(200, OrderJson);
                    return;
                }

                if (path.IndexOf("/recharge/callback", StringComparison.Ordinal) >= 0)
                {
                    _settleCalls++;
                    done(200, _settleCalls == 1 ? SettleOkJson : SettleDuplicateJson);
                    return;
                }

                if (path.IndexOf("/auction/ingot", StringComparison.Ordinal) >= 0)
                {
                    done(200, IngotJson);
                    return;
                }

                // U20 摆摊：六条口各回一份固定形状；成交第二次回「挂单已经没了」。
                if (path.IndexOf("/stall/open", StringComparison.Ordinal) >= 0)
                {
                    _stallOpenBody = body;
                    done(200, StallOpenJson());
                    return;
                }

                if (path.IndexOf("/stall/close", StringComparison.Ordinal) >= 0)
                {
                    done(200, StallCloseJson);
                    return;
                }

                if (path.IndexOf("/stall/list", StringComparison.Ordinal) >= 0)
                {
                    _stallListBody = body;
                    done(200, StallListJson);
                    return;
                }

                if (path.IndexOf("/stall/view", StringComparison.Ordinal) >= 0)
                {
                    done(200, StallViewJson());
                    return;
                }

                if (path.IndexOf("/stalls", StringComparison.Ordinal) >= 0)
                {
                    done(200, StallBrowseJson());
                    return;
                }

                if (path.IndexOf("/stall/buy", StringComparison.Ordinal) >= 0)
                {
                    _stallBuyCalls++;
                    done(200, _stallBuyCalls == 1 ? StallBuyJson : StallBuyGoneJson);
                    return;
                }

                done(200, AuctionJson);
            });

            flow.Bind("selftest-token", 9);
            flow.LoadCurrency();
            flow.LoadBag();
            Report("currency", flow.HasCurrency && flow.Balances.Count == 2
                && UiCommerceCodex.BalanceOf(flow.Balances, "yuanbao") == "500");
            Debug.Log("u6_self_currency rows=" + flow.Balances.Count);

            // 买到：扣 120、余额 380、入包第 1 格、绑定。
            flow.Buy("shop_outfit_1");
            var ok = flow.LastShop;
            Report("shop_ok", ok.Ok && ok.ChargedYuanbao == 120 && ok.Balance == 380
                && ok.Delivered && ok.Bound && ok.BagSlot == 1);
            Debug.Log("u6_self_shop_ok charged=" + ok.ChargedYuanbao + " balance=" + ok.Balance);

            // 清单外：不扣、不发，余额不动。
            flow.Buy("no_such_goods");
            var no = flow.LastShop;
            Report("shop_reject", !no.Ok && no.Reason == "not_in_list"
                && no.ChargedYuanbao == 0 && !no.Delivered && no.Balance == 380);
            Debug.Log("u6_self_shop_reject reason=" + no.Reason
                + " charged=" + no.ChargedYuanbao + " delivered=" + no.Delivered);

            // 拍卖：税与净额来自回包，不是本地算的。
            flow.AuctionList(1, 1, 1000);
            var a = flow.LastAuction;
            Report("auction", a.Ok && a.TaxGold == 60 && a.NetGold == 940 && a.ListingId != 0);
            Debug.Log("u6_self_auction tax=" + a.TaxGold + " net=" + a.NetGold);

            // 寄售：税与净额同样来自回包。
            flow.IngotList(100, 1000);
            var i = flow.LastIngot;
            Report("ingot", i.Ok && i.TaxGold == 60 && i.NetGold == 940);
            Debug.Log("u6_self_ingot tax=" + i.TaxGold + " net=" + i.NetGold);

            // 充值：建单 + 回调；同一笔再来一次回 duplicate。
            flow.RechargeOrder("recharge_60");
            flow.RechargeCallback(flow.LastOrder.OrderId);
            var st = flow.LastSettle;
            Report("recharge", flow.HasOrder && st.Ok && st.Yuanbao == 60 && st.Balance == 440);
            flow.RechargeCallback(flow.LastOrder.OrderId);
            var dup = flow.LastSettle;
            Report("duplicate", dup.Duplicate && dup.Balance == 440 && !dup.Ok);
            Debug.Log("u6_self_duplicate=" + dup.Duplicate + " balance=" + dup.Balance);

            // U20 摆摊：摊位在世界服显示、成交仍走拍卖那一笔事务；税与净额只看回包。
            flow.StallRegion = UiStrings.UiCommerceSelfCheck.T008;
            flow.OpenStall(flow.StallRegion, UiStrings.UiCommerceSelfCheck.T009);
            var so = flow.LastStallOpen;
            Report("stall_open", so.Ok && so.StallId == "st_1" && so.Slots == 8
                && so.TaxRatePct == 5 && so.Shown);
            Report("stall_open_body", _stallOpenBody.IndexOf("\"character_id\":9", StringComparison.Ordinal) >= 0
                && _stallOpenBody.IndexOf("txn_id", StringComparison.Ordinal) < 0);
            Debug.Log("u6_self_stall_open stall=" + so.StallId + " slots=" + so.Slots + " tax=" + so.TaxRatePct);

            flow.ListStallItem(1, 1, 1000);
            var ls = flow.LastStallList;
            Report("stall_list", ls.Ok && ls.Slot == 1 && ls.PriceGold == 1000 && ls.LiveListings == 3);
            Report("stall_list_body", _stallListBody.IndexOf("\"slot\":1", StringComparison.Ordinal) >= 0
                && _stallListBody.IndexOf("\"price_gold\":1000", StringComparison.Ordinal) >= 0
                && _stallListBody.IndexOf("txn_id", StringComparison.Ordinal) < 0);

            flow.BrowseStalls();
            var br = flow.LastStallBrowse;
            Report("stall_browse", br.Ok && br.Stalls.Count == 1 && br.Stalls[0].StallId == "st_1"
                && br.Stalls[0].LiveListings == 2);
            Debug.Log("u6_self_stall_browse=" + br.Stalls.Count);

            flow.StallViewId = "st_1";
            flow.ViewStall(flow.StallViewId);
            var vw = flow.LastStallView;
            Report("stall_view", vw.Found && vw.Listings.Count == 2 && vw.Listings[0].PriceGold == 1000);

            flow.BuyStallItem(vw.Listings[0].ListingId);
            var by = flow.LastStallBuy;
            // 5% 的税与净额都是回包给的（1000 / 50 / 950）：屏上不乘税率、不自己减。
            Report("stall_buy", by.Ok && by.TaxGold == 50 && by.NetGold == 950 && by.TaxRatePct == 5);
            Debug.Log("u6_self_stall_buy tax=" + by.TaxGold + " net=" + by.NetGold);

            flow.BuyStallItem(vw.Listings[1].ListingId);
            var gone = flow.LastStallBuy;
            Report("stall_reject", !gone.Ok && gone.Reason == "listing_gone");
            Debug.Log("u6_self_stall_reject reason=" + gone.Reason);

            var built = 0;
            built += Build(shell, flow, UiStrings.UiCommerceSelfCheck.T001, (s, f) => UiShopScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiCommerceSelfCheck.T002, (s, f) => UiAuctionScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiCommerceSelfCheck.T003, (s, f) => UiCurrencyScreen.Build(s, f));
            built += Build(shell, flow, UiStrings.UiCommerceSelfCheck.T004, (s, f) => UiPassScreen.Build(s, f));
            Report("four", built == 4);
            Debug.Log("u6_self_four=" + built);

            built += Build(shell, flow, UiStrings.UiCommerceSelfCheck.T007, (s, f) => UiStallScreen.Build(s, f));
            Report("five", built == 5);
            Debug.Log("u6_self_five=" + built);

            shell.ShowPage(UiStrings.UiCommerceSelfCheck.T001, s => UiShopScreen.Build(s, flow));
            var layout = Layout(shell);
            var text = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", layout == Layout(shell));
            Report("skin_copy", text == Text(shell));

            // 摆摊屏也一样：切皮肤只换颜色，布局与文案一字不差。
            shell.ShowPage(UiStrings.UiCommerceSelfCheck.T007, s => UiStallScreen.Build(s, flow));
            var stallLayout = Layout(shell);
            var stallText = Text(shell);
            shell.SetSkin(UiSkin.Light, false);
            Report("stall_skin_layout", stallLayout == Layout(shell));
            Report("stall_skin_copy", stallText == Text(shell));

            shell.ClosePage();
            UiSkinStore.Reset();
            UnityEngine.Object.DestroyImmediate(flow.gameObject);
            UnityEngine.Object.DestroyImmediate(shell.gameObject);

            Debug.Log("u6_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        static int Build(UiShell shell, UiCommerceFlow flow, string title, Action<UiShell, UiCommerceFlow> build)
        {
            try
            {
                shell.ShowPage(title, s => build(s, flow));
                return 1;
            }
            catch (Exception error)
            {
                Report("build_" + title, false);
                Debug.LogError("u6_self_build_error " + title + " " + error);
                return 0;
            }
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("u6_self_" + key + "=" + (ok ? "ok" : "fail"));
            if (!ok)
            {
                _failed = true;
            }
        }

        static string Layout(UiShell shell)
        {
            var builder = new StringBuilder();
            var rects = shell.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                var rt = rects[i];
                builder.Append(rt.name).Append(':')
                    .Append(rt.anchoredPosition.x.ToString("0.##")).Append(',')
                    .Append(rt.anchoredPosition.y.ToString("0.##")).Append(',')
                    .Append(rt.sizeDelta.x.ToString("0.##")).Append(',')
                    .Append(rt.sizeDelta.y.ToString("0.##")).Append(';');
            }

            return builder.ToString();
        }

        static string Text(UiShell shell)
        {
            var builder = new StringBuilder();
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                builder.Append(labels[i].name).Append(':').Append(labels[i].text).Append(';');
            }

            return builder.ToString();
        }

        const string CurrencyJson =
            "{\"balances\":[{\"kind\":\"yuanbao\",\"amount\":500},{\"kind\":\"gold\",\"amount\":1000}]}";

        const string BagJson =
            "{\"slots\":[{\"slot\":1,\"item_id\":\"sword_trial\",\"count\":1,\"bound\":true}," +
            "{\"slot\":2,\"item_id\":\"ore_iron\",\"count\":20,\"bound\":false}]," +
            UiStrings.UiCommerceSelfCheck.T005;

        const string ShopOkJson =
            "{\"ok\":true,\"reason\":\"\",\"item_id\":\"shop_outfit_1\",\"kind\":\"outfit\"," +
            UiStrings.UiCommerceSelfCheck.T006 +
            "\"enough\":true,\"charged_yuanbao\":120,\"delivered\":true,\"state\":\"delivered\"," +
            "\"delivery_item_id\":\"sword_trial\",\"bag_slot\":1,\"bound\":true,\"duplicate\":false}";

        const string ShopRejectJson =
            "{\"ok\":false,\"reason\":\"not_in_list\",\"item_id\":\"no_such_goods\",\"kind\":\"\"," +
            "\"name\":\"\",\"price_yuanbao\":0,\"currency\":\"yuanbao\",\"balance\":380,\"enough\":false," +
            "\"charged_yuanbao\":0,\"delivered\":false,\"state\":\"not_in_list\"," +
            "\"delivery_item_id\":\"\",\"bag_slot\":0,\"bound\":false,\"duplicate\":false}";

        const string AuctionJson =
            "{\"ok\":true,\"reason\":\"\",\"listing_id\":11,\"price_gold\":1000,\"tax_gold\":60," +
            "\"net_gold\":940,\"buyer_gold\":1000,\"seller_gold\":2940,\"tax_rate_pct\":6," +
            "\"item_id\":\"sword_trial\",\"count\":1,\"state\":\"listed\",\"duplicate\":false}";

        const string IngotJson =
            "{\"ok\":true,\"reason\":\"\",\"listing_id\":12,\"ingot_amount\":100,\"price_gold\":1000," +
            "\"tax_gold\":60,\"net_gold\":940,\"buyer_gold\":1000,\"seller_gold\":2940," +
            "\"buyer_ingot\":100,\"seller_ingot\":0,\"tax_rate_pct\":6,\"state\":\"listed\"," +
            "\"duplicate\":false}";

        const string OrderJson =
            "{\"ok\":true,\"reason\":\"\",\"order_id\":\"rc_selftest\",\"product_id\":\"recharge_60\"," +
            "\"yuanbao\":60,\"status\":\"pending\"}";

        const string SettleOkJson =
            "{\"ok\":true,\"reason\":\"\",\"order_id\":\"rc_selftest\",\"status\":\"paid\"," +
            "\"yuanbao\":60,\"balance\":440,\"paid_at_ms\":1700000000000,\"duplicate\":false}";

        const string SettleDuplicateJson =
            "{\"ok\":false,\"reason\":\"duplicate\",\"order_id\":\"rc_selftest\",\"status\":\"paid\"," +
            "\"yuanbao\":60,\"balance\":440,\"paid_at_ms\":0,\"duplicate\":true}";

        // ---- U20 摆摊夹具。地表名与招牌走文案表，免得在自检里再写一份中文。

        static string StallOpenJson()
        {
            return "{\"ok\":true,\"reason\":\"\",\"stall_id\":\"st_1\",\"owner_name\":\"\",\"region_id\":\""
                + UiStrings.UiCommerceSelfCheck.T008 + "\",\"pos_x\":1,\"pos_y\":2,\"title\":\""
                + UiStrings.UiCommerceSelfCheck.T009
                + "\",\"live_listings\":2,\"slots\":8,\"min_level\":20,\"tax_rate_pct\":5,\"shown\":true}";
        }

        static string StallBrowseJson()
        {
            return "{\"count\":1,\"region_id\":\"" + UiStrings.UiCommerceSelfCheck.T008 + "\",\"stalls\":[{"
                + "\"stall_id\":\"st_1\",\"owner_id\":9,\"owner_name\":\"\",\"region_id\":\""
                + UiStrings.UiCommerceSelfCheck.T008 + "\",\"pos_x\":1,\"pos_y\":2,\"title\":\""
                + UiStrings.UiCommerceSelfCheck.T009 + "\",\"live_listings\":2}]}";
        }

        static string StallViewJson()
        {
            return "{\"found\":true,\"stall_id\":\"st_1\",\"owner_id\":9,\"owner_name\":\"\",\"region_id\":\""
                + UiStrings.UiCommerceSelfCheck.T008 + "\",\"pos_x\":1,\"pos_y\":2,\"title\":\""
                + UiStrings.UiCommerceSelfCheck.T009 + "\",\"state\":\"open\",\"live_listings\":2,\"slots\":8,"
                + "\"listings\":["
                + "{\"listing_id\":21,\"slot\":1,\"item_id\":\"sword_trial\",\"item_name\":\"sword_trial\","
                + "\"count\":1,\"price_gold\":1000,\"bound\":false},"
                + "{\"listing_id\":22,\"slot\":2,\"item_id\":\"ore_iron\",\"item_name\":\"ore_iron\","
                + "\"count\":20,\"price_gold\":300,\"bound\":false}]}";
        }

        const string StallCloseJson =
            "{\"ok\":true,\"reason\":\"\",\"stall_id\":\"st_1\",\"cancelled_listings\":2}";

        const string StallListJson =
            "{\"ok\":true,\"reason\":\"\",\"listing_id\":21,\"stall_id\":\"st_1\",\"slot\":1," +
            "\"item_id\":\"sword_trial\",\"count\":1,\"price_gold\":1000,\"live_listings\":3,\"slots\":8}";

        const string StallBuyJson =
            "{\"ok\":true,\"reason\":\"\",\"listing_id\":21,\"stall_id\":\"st_1\",\"price_gold\":1000," +
            "\"tax_gold\":50,\"net_gold\":950,\"buyer_gold\":4000,\"seller_gold\":2950,\"tax_rate_pct\":5," +
            "\"item_id\":\"sword_trial\",\"count\":1,\"state\":\"settled\",\"live_listings\":1,\"duplicate\":false}";

        const string StallBuyGoneJson =
            "{\"ok\":false,\"reason\":\"listing_gone\",\"listing_id\":22,\"stall_id\":\"st_1\"," +
            "\"price_gold\":0,\"tax_gold\":0,\"net_gold\":0,\"buyer_gold\":0,\"seller_gold\":0," +
            "\"tax_rate_pct\":5,\"item_id\":\"\",\"count\":0,\"state\":\"\",\"live_listings\":1,\"duplicate\":false}";
    }
}
#endif
