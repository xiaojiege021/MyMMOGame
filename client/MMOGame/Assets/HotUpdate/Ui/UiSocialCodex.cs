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
            UiStrings.UiSocialCodex.T001,
            UiStrings.UiSocialCodex.T002,
            UiStrings.UiSocialCodex.T003,
            UiStrings.UiSocialCodex.T004,
            UiStrings.UiSocialCodex.T005,
        };

        /// <summary>五个职位，顺序与社交服职位表的 positions 同形。</summary>
        public static readonly string[] Positions = { UiStrings.UiSocialCodex.T006, UiStrings.UiSocialCodex.T007, UiStrings.UiSocialCodex.T008, UiStrings.UiSocialCodex.T009, UiStrings.UiSocialCodex.T010 };

        /// <summary>三条线。科技只在城战生效，不写进日常副本。</summary>
        public static readonly string[] Lines =
        {
            UiStrings.UiSocialCodex.T011,
            UiStrings.UiSocialCodex.T012,
            UiStrings.UiSocialCodex.T013,
        };

        /// <summary>宗门 NPC 的口径。</summary>
        public const string Npc =
            UiStrings.UiSocialCodex.T014;

        /// <summary>宗门屏顶那一行说明。</summary>
        public const string SectNote =
            UiStrings.UiSocialCodex.T015;

        /// <summary>三层战力，互不污染。</summary>
        public static readonly string[][] Layers =
        {
            new[] { UiStrings.UiSocialCodex.T016, UiStrings.UiSocialCodex.T017 },
            new[] { UiStrings.UiSocialCodex.T018, UiStrings.UiSocialCodex.T019 },
            new[] { UiStrings.UiSocialCodex.T020, UiStrings.UiSocialCodex.T021 },
        };

        /// <summary>城战同屏的两种画法（方案 15.1）。</summary>
        public const string SiegeRule =
            UiStrings.UiSocialCodex.T022;

        /// <summary>领地收益的口径。</summary>
        public const string TerritoryRule =
            UiStrings.UiSocialCodex.T023;

        /// <summary>世界 Boss 归属的口径。</summary>
        public const string OwnerRule =
            UiStrings.UiSocialCodex.T024;

        /// <summary>竞技场与战场屏顶那一行说明。</summary>
        public const string VersusNote =
            UiStrings.UiSocialCodex.T025;

        /// <summary>
        /// 进场口径（U12 接了 N5 的 `POST /versus/*` 之后屏上写这一条）。
        /// 进场路径、这一场多少人、场次与队伍都只认回包；匹配分组与战力换算不在客户端。
        /// </summary>
        public const string VersusEnterNote =
            UiStrings.UiSocialCodex.T026;

        /// <summary>
        /// 两个竞技场模式编号。只作请求参数（与 `arena_match.json` 的 modes 同形），
        /// 认不认、这一场多少人由网关与配置判；屏上不按 1v1 / 3v3 自己乘人数。
        /// </summary>
        public static readonly string[] ArenaModeIds = { "duel", "trio" };

        /// <summary>
        /// 战场场次编号。只作请求参数：场次谁来开、和谁同队由网关与战斗服判，
        /// 客户端不排队、不造匹配。
        /// </summary>
        public static readonly string[] BattlefieldMatchIds = { "bf_m1" };

        /// <summary>战场两侧的编号，与网关认的 a / b 一致。</summary>
        public static readonly string[] BattlefieldTeams = { "a", "b" };

        /// <summary>公平竞技的奖励口径。</summary>
        public const string VersusReward =
            UiStrings.UiSocialCodex.T027;

        /// <summary>家园洞府四段。</summary>
        public static readonly string[][] Homestead =
        {
            new[] { UiStrings.UiSocialCodex.T028, UiStrings.UiSocialCodex.T029 },
            new[] { UiStrings.UiSocialCodex.T030, UiStrings.UiSocialCodex.T031 },
            new[] { UiStrings.UiSocialCodex.T032, UiStrings.UiSocialCodex.T033 },
            new[] { UiStrings.UiSocialCodex.T034, UiStrings.UiSocialCodex.T035 },
        };

        /// <summary>家园的口径。</summary>
        public const string HomesteadRule =
            UiStrings.UiSocialCodex.T036;

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
            UiStrings.UiSocialCodex.T037;

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
            UiStrings.UiSocialCodex.T038;

        /// <summary>外观收藏共享的口径（与第二角色栏一致）。</summary>
        public const string WardrobeRule =
            UiStrings.UiSocialCodex.T039;

        /// <summary>
        /// 图鉴口径（U11 接了 N12 的 `GET /hero-codex` 之后屏上写这一条）。
        /// 九位名字、收没收到、每位与合计的被动万分比都只认回包；这里不写死任何一位，
        /// 也不把万分比换算成别的数。
        /// </summary>
        public const string CodexNote =
            UiStrings.UiSocialCodex.T040;

        /// <summary>建宗时提交的名字。只作请求参数，能不能用由社交服判。</summary>
        public const string DemoSectName = UiStrings.UiSocialCodex.T041;

        /// <summary>可选的宗门编号与目标角色编号（请求参数，不是事实值）。</summary>
        public static readonly ulong[] NumberChoices = { 1, 2, 3 };
    }
}
