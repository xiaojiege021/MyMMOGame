namespace HotUpdate.Ui
{
    /// <summary>
    /// K2+Q13：NPC 商店的入口清单与固定文案。
    ///
    /// 这里只有「哪家店、挂在哪位 NPC 身上、货架几件」——由 tools/gen/gen-ui-content.py
    /// 从 config/npc_shop.json 生成到 UiContentTable.NpcShops。标价、今日库存、回收价、
    /// 回购窗口、修理单价与耐久一律读 /shop/npc 回包，客户端不算价、不缓存钱。
    /// </summary>
    public static class UiNpcShopCodex
    {
        public struct ShopMeta
        {
            public string Id;
            public string NpcId;
            public string Name;
            public int Goods;
        }

        public const string ReadyLog = "u6_npc_shop_screen=ready";

        public const string Note =
            "买 / 卖 / 回购 / 修理同一个面板 · 标价、库存、回收价、修理费全看 /shop/npc 回包";

        public const string SellNote = "白名单外的物品不能卖 · 身上穿着的不能卖";

        public const string RepairNote = "只修身上三件 · 按缺失耐久扣金币";
    }
}
