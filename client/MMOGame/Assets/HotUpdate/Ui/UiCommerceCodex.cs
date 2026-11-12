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
            new Goods { Id = "shop_protect_ticket_1", Name = "保护券", OneLine = "只挡一次掉级" },
            new Goods { Id = "shop_red_talisman_1", Name = "红符", OneLine = "只回自己的生命" },
            new Goods { Id = "shop_exp_booster_1", Name = "限时经验道具", OneLine = "只加经验、到点消失" },
            new Goods { Id = "shop_unbind_item_1", Name = "解绑道具", OneLine = "只解开装备绑定" },
            new Goods { Id = "shop_teleport_talisman_1", Name = "传送符", OneLine = "只省路程" },
            new Goods { Id = "shop_outfit_1", Name = "服饰外观", OneLine = "只改样子" },
        };

        /// <summary>充值商品编号（P1-5d）。元宝数由服务端按配置决定，这里只带编号。</summary>
        public static readonly Goods[] Recharge =
        {
            new Goods { Id = "recharge_60", Name = "六十元宝", OneLine = "内部订单" },
            new Goods { Id = "recharge_300", Name = "三百元宝", OneLine = "内部订单" },
        };

        /// <summary>拍卖上架用的几档标价（金币）。只作请求参数，成交与税看回包。</summary>
        public static readonly long[] PriceSteps = { 100L, 1000L, 10000L };

        public const string ShopNote =
            "清单外一律拒绝且不扣元宝 · 标价、扣款、余额、发什么、绑定态全看 /shop/buy 回包";

        public const string AuctionNote =
            "成交税与净额只读回包 · 上架与购买都不在本地算价、算税";

        public const string CurrencyNote =
            "元宝 / 金币余额只读 /currency · 充值建单与回调都在服务端判，一笔只发一次";

        public const string PassNote =
            "赛季通行证在后面的卡（P3-4a2）；这里只占位，不做任何请求。";

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
