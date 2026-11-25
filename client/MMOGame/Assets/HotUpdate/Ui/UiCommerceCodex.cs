namespace HotUpdate.Ui
{
    /// <summary>
    /// U6 四屏的固定文案与「只作请求参数」的商品编号。
    ///
    /// 商城的标价、发什么、绑定态都是 `/shop/buy` 回包说了算；这里只列编号与名字，
    /// 和原型一样是入口清单，不是价格表。拍卖的税、净额同样只读回包。
    /// </summary>
    public static class UiCommerceCodex
    {
        public struct Goods
        {
            public string Id;
            public string Name;
            public string OneLine;
        }

        public static readonly Goods[] Shop =
        {
            new Goods { Id = "shop_protect_ticket_1", Name = UiStrings.UiCommerceCodex.T001, OneLine = UiStrings.UiCommerceCodex.T002 },
            new Goods { Id = "shop_red_talisman_1", Name = UiStrings.UiCommerceCodex.T003, OneLine = UiStrings.UiCommerceCodex.T004 },
            new Goods { Id = "shop_exp_booster_1", Name = UiStrings.UiCommerceCodex.T005, OneLine = UiStrings.UiCommerceCodex.T006 },
            new Goods { Id = "shop_unbind_item_1", Name = UiStrings.UiCommerceCodex.T007, OneLine = UiStrings.UiCommerceCodex.T008 },
            new Goods { Id = "shop_teleport_talisman_1", Name = UiStrings.UiCommerceCodex.T009, OneLine = UiStrings.UiCommerceCodex.T010 },
            new Goods { Id = "shop_outfit_1", Name = UiStrings.UiCommerceCodex.T011, OneLine = UiStrings.UiCommerceCodex.T012 },
        };

        /// <summary>充值商品编号（P1-5d）。元宝数由服务端按配置决定，这里只带编号。</summary>
        public static readonly Goods[] Recharge =
        {
            new Goods { Id = "recharge_60", Name = UiStrings.UiCommerceCodex.T013, OneLine = UiStrings.UiCommerceCodex.T014 },
            new Goods { Id = "recharge_300", Name = UiStrings.UiCommerceCodex.T015, OneLine = UiStrings.UiCommerceCodex.T014 },
        };

        /// <summary>拍卖上架用的几档标价（金币）。只作请求参数，成交与税看回包。</summary>
        public static readonly long[] PriceSteps = { 100L, 1000L, 10000L };

        public const string ShopNote =
            UiStrings.UiCommerceCodex.T016;

        public const string AuctionNote =
            UiStrings.UiCommerceCodex.T017;

        public const string CurrencyNote =
            UiStrings.UiCommerceCodex.T018;

        public const string PassNote =
            UiStrings.UiCommerceCodex.T019;

        public const string StallNote =
            UiStrings.UiCommerceCodex.T020;

        public static string BalanceOf(System.Collections.Generic.List<Mmo.Net.CurrencyRow> rows, string kind)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Kind == kind)
                {
                    return rows[i].Amount.ToString();
                }
            }

            return "0";
        }
    }
}
