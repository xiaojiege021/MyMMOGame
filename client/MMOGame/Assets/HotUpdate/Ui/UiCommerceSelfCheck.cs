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

            var built = 0;
            built += Build(shell, flow, "商城", (s, f) => UiShopScreen.Build(s, f));
            built += Build(shell, flow, "拍卖与寄售", (s, f) => UiAuctionScreen.Build(s, f));
            built += Build(shell, flow, "货币", (s, f) => UiCurrencyScreen.Build(s, f));
            built += Build(shell, flow, "赛季通行证", (s, f) => UiPassScreen.Build(s, f));
            Report("four", built == 4);
            Debug.Log("u6_self_four=" + built);

            shell.ShowPage("商城", s => UiShopScreen.Build(s, flow));
            var layout = Layout(shell);
            var text = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", layout == Layout(shell));
            Report("skin_copy", text == Text(shell));

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
            "\"bag_slots\":40,\"character_name\":\"自检角色\",\"realm\":\"炼体\"}";

        const string ShopOkJson =
            "{\"ok\":true,\"reason\":\"\",\"item_id\":\"shop_outfit_1\",\"kind\":\"outfit\"," +
            "\"name\":\"服饰外观\",\"price_yuanbao\":120,\"currency\":\"yuanbao\",\"balance\":380," +
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
    }
}
#endif
