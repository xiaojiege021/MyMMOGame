namespace HotUpdate.Ui
{
    /// <summary>
    /// U7 五屏的固定文案与「只作请求参数」的编号。
    ///
    /// 这里不写名册上限、不写席位数字、不写税率与战力权重：那些只从
    /// `/sect/*`、`/world/worldboss` 与战斗服下发的 `SiegeView` 里读。
    /// 屏上出现的「写定」文字只讲规则，不带可以用作结算的数字。
    /// </summary>
    public static class UiSocialCodex
    {
        /// <summary>五级的名字（方案 11.1）。上限是数字，只由回包给，不写在这里。</summary>
        public static readonly string[] Levels =
        {
            "1 级 · 初建",
            "2 级 · 开始能分工",
            "3 级 · 中型宗门",
            "4 级 · 大型宗门",
            "5 级 · 顶级",
        };

        /// <summary>五个职位，顺序与社交服职位表的 positions 同形。</summary>
        public static readonly string[] Positions = { "掌门", "副掌门", "长老", "执事", "其余弟子" };

        /// <summary>三条线。科技只在城战生效，不写进日常副本。</summary>
        public static readonly string[] Lines =
        {
            "贡献：宗门秘境与宗门任务来，不从商城买",
            "建设：创建耗金币，升级耗建设，商城不卖",
            "科技：只在城战生效，不给日常副本加伤害",
        };

        /// <summary>宗门 NPC 的口径。</summary>
        public const string Npc =
            "只卖基础药品和辅助物品，不卖材料：矿、药、灵石、强化石、结晶、胚子、装备都不卖。";

        /// <summary>宗门屏顶那一行说明。</summary>
        public const string SectNote =
            "名册上限与席位数字只从 /sect/* 回包读 · 客户端不写死 · 拒绝一律 ok=false + reason";

        /// <summary>三层战力，互不污染。</summary>
        public static readonly string[][] Layers =
        {
            new[] { "竞技场 1v1 / 3v3", "属性模板统一，只留职业与分支" },
            new[] { "战场 10v10", "装备生效，同队战力由战斗服压缩" },
            new[] { "城战 / 领地", "装备全额生效，再加宗门科技" },
        };

        /// <summary>城战同屏的两种画法（方案 15.1）。</summary>
        public const string SiegeRule =
            "同屏规格只按战斗服下发的档位：完整的画完整模型，超限的只留队伍颜色和血条，不按距离自己判。";

        /// <summary>领地收益的口径。</summary>
        public const string TerritoryRule =
            "领地归属只记名字：不加属性、不改税收、不把灵石矿脉转走。个人采集照样采不到灵石。";

        /// <summary>世界 Boss 归属的口径。</summary>
        public const string OwnerRule =
            "伤害最高的宗门在下一场刷新前把名字记在入口上；没有宗门的人也能进，也能拿个人奖励。";

        /// <summary>竞技场与战场屏顶那一行说明。</summary>
        public const string VersusNote =
            "本场数值只来自战斗服回包 · 界面不算战力、不乘平衡系数、不读配置";

        /// <summary>匹配入口的现状。</summary>
        public const string VersusGap =
            "匹配与进场没有 HTTP 入口（P2-3 只做到战斗服按模板和平衡系数开战）· 本卡不做假入口";

        /// <summary>公平竞技的奖励口径。</summary>
        public const string VersusReward =
            "竞技奖励偏外观与荣誉，不给战力材料；长线养成在城战兑现，两套目标分开。";

        /// <summary>家园洞府四段。</summary>
        public static readonly string[][] Homestead =
        {
            new[] { "生活技能", "挖矿产矿石给铁匠，采药产药材给炼丹师；谁都能学、可同时学、不耗疲劳" },
            new[] { "城镇 NPC", "卖镐、药锄和基础背包；宗门 NPC 只卖基础药品和辅助物品" },
            new[] { "家园 / 洞府", "种植产家园币，只换外观和便捷；摆件与装饰只做展示，不进伤害公式" },
            new[] { "时间结构", "大世界、挖矿、采药、家园、钓鱼、外观收集不设日限周限" },
        };

        /// <summary>家园的口径。</summary>
        public const string HomesteadRule =
            "摆件只做展示；家园币不换金币、材料、副本次数或任何战力。";

        /// <summary>
        /// 洞府六格编号（与 C3 产品表同形，只作请求参数与屏上标签；
        /// 运行期不打开家园配置文件）。
        /// </summary>
        public static readonly string[] HomesteadSlots =
        {
            "court_1", "court_2", "hall_1", "hall_2", "garden_1", "garden_2",
        };

        /// <summary>允许摆的摆件编号（与 P3-4a2 / C3 发货的 decor_s* 同形）。</summary>
        public static readonly string[] DecorItemIds =
        {
            "decor_s1", "decor_s2", "decor_s3",
        };

        /// <summary>
        /// 洞府庭院两格的编号（与 N11 种植配置的格位同形，只作请求参数与屏上标签；
        /// 运行期不打开家园配置文件，格位熟没熟只认回包）。
        /// </summary>
        public static readonly string[] PlantSlots = { "garden_1", "garden_2" };

        /// <summary>
        /// 可以种的作物编号。只作请求参数：产量、成长时长与收多少家园币都在服务端配置，
        /// 客户端一个数字都不写在这里。
        /// </summary>
        public static readonly string[] CropIds = { "crop_spirit_herb" };

        /// <summary>
        /// 家园币商城的商品编号。只作请求参数：标价、这次扣多少、有没有真发货只认回包。
        /// </summary>
        public static readonly string[] HomeCoinShopItemIds = { "hc_shop_outfit_1", "hc_shop_teleport_1" };

        /// <summary>种植与家园币的口径（接口已接，屏上不再写缺口）。</summary>
        public const string PlantingNote =
            "种植与家园币只认回包：产量、成熟、这次扣多少与余额都不在客户端算";

        public static bool IsDecorItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return false;
            }

            for (var i = 0; i < DecorItemIds.Length; i++)
            {
                if (string.Equals(DecorItemIds[i], itemId, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>图鉴两条口径。</summary>
        public const string CodexRule =
            "收集度与被动只做收集、外观和生活协助，不进入伤害公式；灵兽同理。";

        /// <summary>外观收藏共享的口径（与第二角色栏一致）。</summary>
        public const string WardrobeRule =
            "第二角色栏开启后，装备、等级、境界不共享，外观收藏可以共享。";

        /// <summary>
        /// 图鉴口径（U11 接了 N12 的 `GET /hero-codex` 之后屏上写这一条）。
        /// 九位名字、收没收到、每位与合计的被动万分比都只认回包；这里不写死任何一位，
        /// 也不把万分比换算成别的数。
        /// </summary>
        public const string CodexNote =
            "九格只认 /hero-codex 回包：名字、收集度、被动 bps 都照抄；未定的格子留空，不补编条目";

        /// <summary>建宗时提交的名字。只作请求参数，能不能用由社交服判。</summary>
        public const string DemoSectName = "云锦";

        /// <summary>可选的宗门编号与目标角色编号（请求参数，不是事实值）。</summary>
        public static readonly ulong[] NumberChoices = { 1, 2, 3 };
    }
}
