namespace Character.Cli;

/// <summary>
/// 核对 flag 的共享状态。拆文件前它们是 Program 的顶层局部变量；
/// D1a 只把 133 个声明搬到这里，名字、默认值与含义一行未改；K5 追加 --item-list。
/// 各 CLI 类用 using static 引用。
/// </summary>
internal static class CliFlags
{
    public static string configPath = "config/character.json";
    // C7 数值台账：把角色服启动配置的现值打出来（listen / db_pool_max / bag_slots / 表路径），
    // 不连库、不启动进程。台账表 E 的「改配置重启生效」用它取证。
    public static bool configShow = false;
    public static string sqlPath = "db/character/character.sql";
    // D8：--sql 是不是显式给的。给了就还是「只跑这一份」的老行为；
    // 没给时 --migrate 按迁移清单走版本表。
    public static bool sqlGiven = false;
    public static string migrationsPath = "db/migrations.json";
    // all / character / social / trade。只影响这一次迁移选哪个 schema，不改清单本身。
    public static string schemaFilter = "all";
    // 只读版本表：打印每个 schema 已应用到第几版，不建表、不迁移。
    public static bool schemaStatus = false;
    public static string? reportName = null;
    public static string? deleteName = null;
    public static bool stats = false;
    public static bool migrate = false;
    public static long rawLevelWrite = 0;
    public static int rawLevelValue = 0;
    public static long levelOf = 0;
    public static long branchStateOf = 0;
    public static long advanceBranchId = 0;
    public static string? advanceBranchTo = null;
    public static long canEquipOf = 0;
    public static string? canEquipItem = null;
    // K6：把 NPC / 怪物两张内容表读出来，不连库、不启动进程。坏配置也走这里。
    public static bool contentList = false;
    // K5：把物品表的 grade / set_id / desc 读出来，不连库、不启动进程（icon 不进配置，见第 17 节 17.1）。
    public static bool itemList = false;
    // K3：把配方表读出来（与启动共用同一 RecipeTable.Load），不连库、不启动进程；坏配置也走这里。
    public static bool recipeList = false;
    public static string? recipeTablePath = null;
    // K3：连库核对。start / finish 各一笔事务，state 只读；事务号不传就自己生成。
    public static long craftStartOf = 0;
    public static string? craftStartRecipe = null;
    public static long craftFinishOf = 0;
    public static long craftStateOf = 0;
    public static string? craftTxn = null;
    // K3：核对用的原始背包写入（绕过业务规则，只给造料 / 清格用，与 --raw-currency-write 同口径）。
    public static long rawItemWriteId = 0;
    public static int rawItemSlot = 0;
    public static string? rawItemId = null;
    public static int rawItemCount = 0;
    public static bool weaponRules = false;
    public static string? weaponRulesBranchPath = null;
    public static string? weaponRulesItemPath = null;
    public static string? bindingOf = null;
    public static int bindingSlot = 0;
    public static long rawCurrencyId = 0;
    public static string? rawCurrencyKind = null;
    public static long rawCurrencyAmount = 0L;
    // K3：核对用的货币加数（走 CurrencyRepository.AddAsync，行不存在会建），
    // 与 --raw-currency-write 的「只改已有行」分开，只为造金币 / 灵石。
    public static long rawCurrencyAddId = 0;
    public static string? rawCurrencyAddKind = null;
    public static long rawCurrencyAddDelta = 0L;
    public static string? txnCount = null;
    public static long reservesOf = 0;
    public static long entryCountOf = 0;
    public static long fatigueStateOf = 0;
    public static long setFatigueDayId = 0;
    public static string? setFatigueDay = null;
    public static long rawFatigueId = 0;
    public static int rawFatigueValue = 0;
    public static long shareStateOf = 0;
    public static long setWeekDayId = 0;
    public static string? setWeekDay = null;
    public static long rawShareId = 0;
    public static int rawShareValue = 0;
    public static long sharesTotalOf = 0;
    public static string? sharesTotalDungeon = null;
    public static long expStateOf = 0;
    public static long rawExpId = 0;
    public static long rawExpValue = 0L;
    // P2-1c：把经验表读出来，不连库、不启动进程，坏配置也走这里。
    public static bool expConfigState = false;
    public static string? expConfigPath = null;
    // P2-2a：把副本表读出来，不连库、不启动进程，坏配置也走这里（与启动共用同一 DungeonTable.Load）。
    public static bool dungeonTableState = false;
    public static string? dungeonTablePath = null;
    // N6：掉落表只读核对（与启动共用 DungeonDropTable.Load）。
    public static bool dungeonDropTableState = false;
    public static string? dungeonDropTablePath = null;
    public static bool mainlineDropTableState = false;
    public static string? mainlineDropTablePath = null;
    public static long mainlineSeedSettlementOf = 0;
    public static string? mainlineSeedSettlementTxn = null;
    public static long itemCountOf = 0;
    public static string? itemCountItem = null;
    // P3-1a1：把境界表读出来（与启动共用同一 RealmTable.Load），坏配置也走这里。
    public static bool realmTableState = false;
    public static string? realmTablePath = null;
    public static string? realmDungeonPath = null;
    // 只给核对用：单角色的当前境界与「下一段开没开」，或直写境界验开关。
    public static long realmStateOf = 0;
    public static long rawRealmId = 0;
    public static string? rawRealmValue = null;
    // P3-1a2：只给核对用——直写历练 / 主线场次、记一条必要支线、读突破状态，
    // 以及不经过网关直接走一次突破或历练结算。
    public static long rawDrillId = 0;
    public static long rawDrillValue = 0L;
    public static long rawMainStageId = 0;
    public static int rawMainStageValue = 0;
    public static long completeQuestOf = 0;
    public static string? completeQuestId = null;
    public static long realmBreakStateOf = 0;
    public static long advanceRealmOf = 0;
    public static string? advanceRealmTxn = null;
    public static long settleDrillOf = 0;
    public static string? settleDrillDungeon = null;
    public static string? settleDrillTxn = null;
    // N10：段内四阶核对入口。
    public static long rawRealmPhaseId = 0;
    public static int rawRealmPhaseValue = 0;
    public static long realmPassiveStateOf = 0;
    public static long heroCodexStateOf = 0;
    public static string? heroCodexTablePath = null;
    public static long promoteRealmPhaseOf = 0;
    public static string? promoteRealmPhaseTxn = null;
    // 只给核对用：读高难本本周已用次数，或把进本记录整体往前挪一周验跨周归零。
    public static long hardEntryStateOf = 0;
    public static string? hardEntryDungeon = null;
    public static long shiftEntryWeekOf = 0;
    public static string? shiftEntryWeekDungeon = null;
    public static bool enhanceState = false;
    public static string? enhanceConfigPath = null;
    public static long enhanceLevelOf = 0;
    public static int enhanceSlotOf = 0;
    public static string? enhanceTxnCount = null;
    public static long couponCountOf = 0;
    public static long rawEnhanceId = 0;
    public static int rawEnhanceSlot = 0;
    public static int rawEnhanceLevel = 0;
    public static bool transferState = false;
    public static string? transferConfigPath = null;
    public static long transferStateOf = 0;
    public static int transferFromSlot = 0;
    public static int transferToSlot = 0;
    public static string? transferTxnCount = null;
    public static bool crystalState = false;
    public static string? crystalConfigPath = null;
    public static long crystalStateOf = 0;
    public static int crystalSlotOf = 0;
    public static string? crystalItemOf = null;
    public static string? crystalTxnCount = null;
    public static bool auctionState = false;
    public static string? auctionConfigPath = null;
    public static bool shopList = false;
    public static string? shopConfigPath = null;
    // P3-4a2 通行证：list 只读配置不连库；state 连库读回；买与领另有两个直连入口，
    // 玩法路径走网关 /shop/buy 与 /season/pass/claim，这两个只给核对用。
    public static bool seasonPassList = false;
    public static string? seasonPassPath = null;
    public static long seasonPassStateOf = 0;
    public static string? seasonPassSeason = null;
    public static long buySeasonPassOf = 0;
    public static string? buySeasonPassTxn = null;
    public static long claimSeasonPassOf = 0;
    public static string? claimSeasonPassTrack = null;
    public static int claimSeasonPassStage = 0;
    public static string? claimSeasonPassTxn = null;
    public static long shopStateOf = 0;
    public static long shopLedgerOf = 0;
    // P3-4b：月叠与第二角色栏。list 只读配置不连库（坏配置也走这里），
    // 三个连库入口只给核对用：名额现状、账号外观收藏、把名额直推到指定值（种子 / 核对）。
    public static bool monthlyBundleList = false;
    public static string? monthlyBundlePath = null;
    // C1：主线十二场只读核对。与启动共用同一 MainlineTable.Load，不连库也不启动进程。
    public static bool mainlineList = false;
    public static string? mainlinePath = null;
    public static bool characterSlotList = false;
    public static string? characterSlotPath = null;
    public static string? characterSlotsOf = null;
    public static string? wardrobeOf = null;
    public static string? rawSlotAccount = null;
    public static int rawSlotValue = 0;
    // C1：只给核对用的主线入口——读进度、用固定事务号交任务、注入一条突破成功事实。
    // 正常路径分别走网关 GET /mainline、POST /mainline/turn-in 和 P3-1a2 的突破链路。
    public static long mainlineStateOf = 0;
    public static long mainlineTurnInOf = 0;
    public static int mainlineStage = 0;
    public static string? mainlineAlignment = null;
    public static string? mainlineTxn = null;
    public static long mainlineBreakthroughOf = 0;
    public static string? mainlineFrom = null;
    // 红符：只读核对用。talismanConfigPath 指向临时表试坏配置，talismanStateOf 连库读张数。
    public static bool talismanList = false;
    public static string? talismanConfigPath = null;
    public static long talismanStateOf = 0;
    // P1-5c6 复活卷轴：清单读配置，state 连库读冷却到期、当前级经验与背包张数。
    public static bool scrollList = false;
    public static string? scrollConfigPath = null;
    // 只给核对用：把职业表指到临时副本，验「拉起血量比例 / 经验比例」的坏配置。
    public static string? scrollClassesPath = null;
    public static long scrollStateOf = 0;
    // 只给核对用：把角色库里的卷轴冷却清掉，让一张核对能连着验几个成功场景。
    // 玩法代码不碰这个入口，冷却到期只由 ConsumeReviveScroll 那一笔事务写。
    public static long scrollResetOf = 0;
    // P1-5c7 限时经验道具：清单读配置，state 连库读到期时刻、剩余时间与背包张数。
    // reset / expire 只给核对用：清掉这一行 / 把到期时刻挪到过去，验「到点消失」。
    // 玩法代码不碰这两个入口，到期时刻只由 UseExpBooster 那一笔事务写。
    public static bool boosterList = false;
    public static string? boosterConfigPath = null;
    public static long boosterStateOf = 0;
    public static long boosterResetOf = 0;
    public static long boosterExpireOf = 0;
    // P1-5c8 解绑道具：list 读配置，quote / state 连库读这一格的绑定年龄与张数。
    // raw-bound-at 只给核对用：把 bound_at 挪到 N 小时前，验「绑定越久张数越少」。
    // 玩法代码不碰这个入口，绑定态只由购买与 UseUnbindItem 改。
    public static bool unbindList = false;
    public static string? unbindConfigPath = null;
    public static long unbindQuoteOf = 0;
    public static int unbindQuoteSlot = 0;
    public static long unbindStateOf = 0;
    public static int unbindStateSlot = 0;
    public static long rawBoundAtOf = 0;
    public static int rawBoundAtSlot = 0;
    public static int rawBoundAtHours = 0;
    // P1-5d 内部充值：list 读商品表，state 连库读一张订单的状态与余额。
    public static bool rechargeList = false;
    public static string? rechargeConfigPath = null;
    public static string? rechargeStateOf = null;
    public static long auctionBuyerOf = 0;
    public static long auctionSellerOf = 0;
    public static long auctionListingOf = 0;
    public static string? auctionTxnOf = null;
    public static long ingotBuyerOf = 0;
    public static long ingotSellerOf = 0;
    public static long ingotListingOf = 0;
    public static string? ingotTxnOf = null;
    // P3-2a：灵石兑换表与城池表的只读核对（不连库），坏配置也走这里。
    public static bool stoneExchangeTableState = false;
    public static string? stoneExchangeTablePath = null;
    public static string? stoneExchangeCurrencyPath = null;
    public static bool cityTableState = false;
    public static string? cityTablePath = null;
    // P3-2a：连库核对——走一次兑换、读两档余额与兑换笔数、城池两张税账、挂单状态。
    public static long exchangeStoneOf = 0;
    public static string? exchangeStoneKind = null;
    public static string? exchangeStoneTxn = null;
    public static long stoneExchangeStateOf = 0;
    public static bool cityState = false;
    public static string? cityTaxRowTxn = null;
    public static bool stoneListingState = false;
    // P3-3a4：领地表只读、城池写回状态、以及直接走同一笔写回事务的核对入口。
    public static bool siegeTerritoryTableState = false;
    public static string? siegeTerritoryTablePath = null;
    public static bool siegeResultState = false;
    public static string? applySiegeResultTxn = null;
    public static string applySiegeResultCity = "";
    public static string applySiegeResultMatch = "";
    public static uint applySiegeResultTeam = 0;
    public static ulong applySiegeResultSect = 0;
    // D17：只读不变量核对。--db 只给核对用：把连接串里的 Database 换到临时库，
    // 用来在「故意造一条违反」的临时库上验核对器还灵，不改主库。
    public static bool invariants = false;
    public static string? dbOverride = null;
}
