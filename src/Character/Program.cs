using System.Runtime.InteropServices;
using Character;
using Npgsql;

// 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
var configPath = "config/character.json";
var sqlPath = "db/character/character.sql";
string? reportName = null;
string? deleteName = null;
var stats = false;
var migrate = false;
long rawLevelWrite = 0;
var rawLevelValue = 0;
long levelOf = 0;
long branchStateOf = 0;
long advanceBranchId = 0;
string? advanceBranchTo = null;
long canEquipOf = 0;
string? canEquipItem = null;
var weaponRules = false;
string? weaponRulesBranchPath = null;
string? weaponRulesItemPath = null;
string? bindingOf = null;
var bindingSlot = 0;
long rawCurrencyId = 0;
string? rawCurrencyKind = null;
var rawCurrencyAmount = 0L;
string? txnCount = null;
long reservesOf = 0;
long entryCountOf = 0;
long fatigueStateOf = 0;
long setFatigueDayId = 0;
string? setFatigueDay = null;
long rawFatigueId = 0;
var rawFatigueValue = 0;
long shareStateOf = 0;
long setWeekDayId = 0;
string? setWeekDay = null;
long rawShareId = 0;
var rawShareValue = 0;
long sharesTotalOf = 0;
string? sharesTotalDungeon = null;
long expStateOf = 0;
long rawExpId = 0;
var rawExpValue = 0L;
// P2-1c：把经验表读出来，不连库、不启动进程，坏配置也走这里。
var expConfigState = false;
string? expConfigPath = null;
// P2-2a：把副本表读出来，不连库、不启动进程，坏配置也走这里（与启动共用同一 DungeonTable.Load）。
var dungeonTableState = false;
string? dungeonTablePath = null;
// N6：掉落表只读核对（与启动共用 DungeonDropTable.Load）。
var dungeonDropTableState = false;
string? dungeonDropTablePath = null;
var mainlineDropTableState = false;
string? mainlineDropTablePath = null;
long mainlineSeedSettlementOf = 0;
string? mainlineSeedSettlementTxn = null;
long itemCountOf = 0;
string? itemCountItem = null;
// P3-1a1：把境界表读出来（与启动共用同一 RealmTable.Load），坏配置也走这里。
var realmTableState = false;
string? realmTablePath = null;
string? realmDungeonPath = null;
// 只给核对用：单角色的当前境界与「下一段开没开」，或直写境界验开关。
long realmStateOf = 0;
long rawRealmId = 0;
string? rawRealmValue = null;
// P3-1a2：只给核对用——直写历练 / 主线场次、记一条必要支线、读突破状态，
// 以及不经过网关直接走一次突破或历练结算。
long rawDrillId = 0;
var rawDrillValue = 0L;
long rawMainStageId = 0;
var rawMainStageValue = 0;
long completeQuestOf = 0;
string? completeQuestId = null;
long realmBreakStateOf = 0;
long advanceRealmOf = 0;
string? advanceRealmTxn = null;
long settleDrillOf = 0;
string? settleDrillDungeon = null;
string? settleDrillTxn = null;
// 只给核对用：读高难本本周已用次数，或把进本记录整体往前挪一周验跨周归零。
long hardEntryStateOf = 0;
string? hardEntryDungeon = null;
long shiftEntryWeekOf = 0;
string? shiftEntryWeekDungeon = null;
var enhanceState = false;
string? enhanceConfigPath = null;
long enhanceLevelOf = 0;
var enhanceSlotOf = 0;
string? enhanceTxnCount = null;
long couponCountOf = 0;
long rawEnhanceId = 0;
var rawEnhanceSlot = 0;
var rawEnhanceLevel = 0;
var transferState = false;
string? transferConfigPath = null;
long transferStateOf = 0;
var transferFromSlot = 0;
var transferToSlot = 0;
string? transferTxnCount = null;
var crystalState = false;
string? crystalConfigPath = null;
long crystalStateOf = 0;
var crystalSlotOf = 0;
string? crystalItemOf = null;
string? crystalTxnCount = null;
var auctionState = false;
string? auctionConfigPath = null;
var shopList = false;
string? shopConfigPath = null;
// P3-4a2 通行证：list 只读配置不连库；state 连库读回；买与领另有两个直连入口，
// 玩法路径走网关 /shop/buy 与 /season/pass/claim，这两个只给核对用。
var seasonPassList = false;
string? seasonPassPath = null;
long seasonPassStateOf = 0;
string? seasonPassSeason = null;
long buySeasonPassOf = 0;
string? buySeasonPassTxn = null;
long claimSeasonPassOf = 0;
string? claimSeasonPassTrack = null;
var claimSeasonPassStage = 0;
string? claimSeasonPassTxn = null;
long shopStateOf = 0;
long shopLedgerOf = 0;
// P3-4b：月叠与第二角色栏。list 只读配置不连库（坏配置也走这里），
// 三个连库入口只给核对用：名额现状、账号外观收藏、把名额直推到指定值（种子 / 核对）。
var monthlyBundleList = false;
string? monthlyBundlePath = null;
// C1：主线十二场只读核对。与启动共用同一 MainlineTable.Load，不连库也不启动进程。
var mainlineList = false;
string? mainlinePath = null;
var characterSlotList = false;
string? characterSlotPath = null;
string? characterSlotsOf = null;
string? wardrobeOf = null;
string? rawSlotAccount = null;
var rawSlotValue = 0;
// C1：只给核对用的主线入口——读进度、用固定事务号交任务、注入一条突破成功事实。
// 正常路径分别走网关 GET /mainline、POST /mainline/turn-in 和 P3-1a2 的突破链路。
long mainlineStateOf = 0;
long mainlineTurnInOf = 0;
var mainlineStage = 0;
string? mainlineAlignment = null;
string? mainlineTxn = null;
long mainlineBreakthroughOf = 0;
string? mainlineFrom = null;
// 红符：只读核对用。talismanConfigPath 指向临时表试坏配置，talismanStateOf 连库读张数。
var talismanList = false;
string? talismanConfigPath = null;
long talismanStateOf = 0;
// P1-5c6 复活卷轴：清单读配置，state 连库读冷却到期、当前级经验与背包张数。
var scrollList = false;
string? scrollConfigPath = null;
// 只给核对用：把职业表指到临时副本，验「拉起血量比例 / 经验比例」的坏配置。
string? scrollClassesPath = null;
long scrollStateOf = 0;
// 只给核对用：把角色库里的卷轴冷却清掉，让一张核对能连着验几个成功场景。
// 玩法代码不碰这个入口，冷却到期只由 ConsumeReviveScroll 那一笔事务写。
long scrollResetOf = 0;
// P1-5c7 限时经验道具：清单读配置，state 连库读到期时刻、剩余时间与背包张数。
// reset / expire 只给核对用：清掉这一行 / 把到期时刻挪到过去，验「到点消失」。
// 玩法代码不碰这两个入口，到期时刻只由 UseExpBooster 那一笔事务写。
var boosterList = false;
string? boosterConfigPath = null;
long boosterStateOf = 0;
long boosterResetOf = 0;
long boosterExpireOf = 0;
// P1-5c8 解绑道具：list 读配置，quote / state 连库读这一格的绑定年龄与张数。
// raw-bound-at 只给核对用：把 bound_at 挪到 N 小时前，验「绑定越久张数越少」。
// 玩法代码不碰这个入口，绑定态只由购买与 UseUnbindItem 改。
var unbindList = false;
string? unbindConfigPath = null;
long unbindQuoteOf = 0;
int unbindQuoteSlot = 0;
long unbindStateOf = 0;
int unbindStateSlot = 0;
long rawBoundAtOf = 0;
int rawBoundAtSlot = 0;
int rawBoundAtHours = 0;
// P1-5d 内部充值：list 读商品表，state 连库读一张订单的状态与余额。
var rechargeList = false;
string? rechargeConfigPath = null;
string? rechargeStateOf = null;
long auctionBuyerOf = 0;
long auctionSellerOf = 0;
long auctionListingOf = 0;
string? auctionTxnOf = null;
long ingotBuyerOf = 0;
long ingotSellerOf = 0;
long ingotListingOf = 0;
string? ingotTxnOf = null;
// P3-2a：灵石兑换表与城池表的只读核对（不连库），坏配置也走这里。
var stoneExchangeTableState = false;
string? stoneExchangeTablePath = null;
string? stoneExchangeCurrencyPath = null;
var cityTableState = false;
string? cityTablePath = null;
// P3-2a：连库核对——走一次兑换、读两档余额与兑换笔数、城池两张税账、挂单状态。
long exchangeStoneOf = 0;
string? exchangeStoneKind = null;
string? exchangeStoneTxn = null;
long stoneExchangeStateOf = 0;
var cityState = false;
var stoneListingState = false;
// P3-3a4：领地表只读、城池写回状态、以及直接走同一笔写回事务的核对入口。
var siegeTerritoryTableState = false;
string? siegeTerritoryTablePath = null;
var siegeResultState = false;
string? applySiegeResultTxn = null;
var applySiegeResultCity = "";
var applySiegeResultMatch = "";
uint applySiegeResultTeam = 0;
ulong applySiegeResultSect = 0;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--sql" when i + 1 < args.Length:
            sqlPath = args[++i];
            break;
        case "--migrate":
            migrate = true;
            break;
        case "--character-report" when i + 1 < args.Length:
            reportName = args[++i];
            break;
        case "--delete-character" when i + 1 < args.Length:
            deleteName = args[++i];
            break;
        case "--character-stats":
            stats = true;
            break;
        // 只给核对用：绕过接口直写库，验库层 CHECK。不能当运维手段用。
        case "--raw-level-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawLevelWrite);
            int.TryParse(args[++i], out rawLevelValue);
            break;
        // 只给核对用：读回分支与二转步数。
        case "--branch-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out branchStateOf);
            break;
        // 只给核对用：走一遍二转（等级、分支锁定与幂等都在仓储里判）。
        case "--advance-branch" when i + 2 < args.Length:
            long.TryParse(args[++i], out advanceBranchId);
            advanceBranchTo = args[++i];
            break;
        // 只给核对用：把武器锁与职业可用兵器打出来，不连库。坏配置也走这里。
        case "--weapon-rules":
            weaponRules = true;
            break;
        // 坏配置走这里：指定另一份分支表 / 物品表，不碰仓库里那两份。
        case "--weapon-rules-path" when i + 2 < args.Length:
            weaponRules = true;
            weaponRulesBranchPath = args[++i];
            weaponRulesItemPath = args[++i];
            break;
        // 只给核对用：这件装备这个人能不能穿（与 RPC 同一套判定）。
        case "--can-equip" when i + 2 < args.Length:
            long.TryParse(args[++i], out canEquipOf);
            canEquipItem = args[++i];
            break;
        case "--level-of" when i + 1 < args.Length:
            long.TryParse(args[++i], out levelOf);
            break;
        // 只给核对用：看这一格的 bound 与 bound_at 有没有一起落对。
        case "--binding-of" when i + 2 < args.Length:
            bindingOf = args[++i];
            int.TryParse(args[++i], out bindingSlot);
            break;
        // 只给核对用：绕过接口直写余额，验库层 CHECK 会不会挡住负数。
        case "--raw-currency-write" when i + 3 < args.Length:
            long.TryParse(args[++i], out rawCurrencyId);
            rawCurrencyKind = args[++i];
            long.TryParse(args[++i], out rawCurrencyAmount);
            break;
        // 只给核对用：这个事务号在表里有几行。
        case "--txn-count" when i + 1 < args.Length:
            txnCount = args[++i];
            break;
        // 只给核对用：读一个角色的疲劳与周份额。
        case "--reserves-of" when i + 1 < args.Length:
            long.TryParse(args[++i], out reservesOf);
            break;
        // 只给核对用：这个角色进过几次本。
        case "--entry-count" when i + 1 < args.Length:
            long.TryParse(args[++i], out entryCountOf);
            break;
        // 只给核对用：读疲劳、周份额与日界（含跨天归零后的状态）。
        case "--fatigue-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out fatigueStateOf);
            break;
        // 只给核对用：把日界改到别的一天，验第二天从 0 起算。
        case "--set-fatigue-day" when i + 2 < args.Length:
            long.TryParse(args[++i], out setFatigueDayId);
            setFatigueDay = args[++i];
            break;
        // 只给核对用：绕过接口直写疲劳，验库层日上限 CHECK。
        case "--raw-fatigue-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawFatigueId);
            int.TryParse(args[++i], out rawFatigueValue);
            break;
        // 只给核对用：读已用份额、剩余份额、周界与疲劳。
        case "--share-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out shareStateOf);
            break;
        // 只给核对用：把周界改到别的周首，验跨周归零。
        case "--set-week-day" when i + 2 < args.Length:
            long.TryParse(args[++i], out setWeekDayId);
            setWeekDay = args[++i];
            break;
        // 只给核对用：绕过接口直写已用份额，造并发现场或验库层非负 CHECK。
        case "--raw-share-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawShareId);
            int.TryParse(args[++i], out rawShareValue);
            break;
        // 只给核对用：这个角色在某个副本累计应得多少单人份。
        case "--shares-total" when i + 2 < args.Length:
            long.TryParse(args[++i], out sharesTotalOf);
            sharesTotalDungeon = args[++i];
            break;
        // 只给核对用：读等级、当前级经验、结算笔数与累计入账经验。
        case "--exp-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out expStateOf);
            break;
        // 只给核对用：绕过接口直写经验，验库层的非负 CHECK。
        case "--raw-exp-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawExpId);
            long.TryParse(args[++i], out rawExpValue);
            break;
        // P2-1c：把经验表读出来，不连库、不启动进程，坏配置也走这里。
        // 与真实启动共用同一 ExpTable.Load（默认指向 CharacterConfig.ExpTable）。
        case "--exp-config":
            expConfigState = true;
            break;
        case "--exp-config-path" when i + 1 < args.Length:
            expConfigState = true;
            expConfigPath = args[++i];
            break;
        // P2-2a：副本表只读核对。与真实启动共用同一 DungeonTable.Load，
        // 不连库也不启动进程；--dungeon-table-path 指到临时副本验坏配置。
        case "--dungeon-table":
            dungeonTableState = true;
            break;
        case "--dungeon-table-path" when i + 1 < args.Length:
            dungeonTableState = true;
            dungeonTablePath = args[++i];
            break;
        case "--dungeon-drop-table-path" when i + 1 < args.Length:
            dungeonDropTableState = true;
            dungeonDropTablePath = args[++i];
            break;
        case "--item-count" when i + 2 < args.Length:
            long.TryParse(args[++i], out itemCountOf);
            itemCountItem = args[++i];
            break;
        // P3-1a1：境界表只读核对。与启动共用同一 RealmTable.Load；给两个路径时
        // 第一个是临时境界表、第二个是临时副本表，用来验「旧副本不许写 realm」。
        case "--realm-table":
            realmTableState = true;
            break;
        case "--realm-table-path" when i + 2 < args.Length:
            realmTableState = true;
            realmTablePath = args[++i];
            realmDungeonPath = args[++i];
            break;
        // 只给核对用：读一个角色的境界，并按配置回答「能不能突破到下一段」。
        case "--realm-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out realmStateOf);
            break;
        // 只给核对用：绕过突破流程直写境界，验开关关着时进不去该段副本。
        case "--raw-realm-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawRealmId);
            rawRealmValue = args[++i];
            break;

        case "--raw-drill-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawDrillId);
            long.TryParse(args[++i], out rawDrillValue);
            break;

        case "--raw-main-stage-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out rawMainStageId);
            int.TryParse(args[++i], out rawMainStageValue);
            break;

        case "--complete-quest" when i + 2 < args.Length:
            long.TryParse(args[++i], out completeQuestOf);
            completeQuestId = args[++i];
            break;

        // C1：只给核对用。固定事务号用来验「同一个号不写第二次」。
        case "--mainline-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out mainlineStateOf);
            break;
        case "--mainline-turn-in" when i + 1 < args.Length:
            long.TryParse(args[++i], out mainlineTurnInOf);
            break;
        case "--mainline-stage" when i + 1 < args.Length:
            int.TryParse(args[++i], out mainlineStage);
            break;
        case "--mainline-alignment" when i + 1 < args.Length:
            mainlineAlignment = args[++i];
            break;
        case "--mainline-txn" when i + 1 < args.Length:
            mainlineTxn = args[++i];
            break;
        case "--mainline-breakthrough" when i + 2 < args.Length:
            long.TryParse(args[++i], out mainlineBreakthroughOf);
            mainlineFrom = args[++i];
            break;
        case "--mainline-seed-settlement" when i + 2 < args.Length:
            long.TryParse(args[++i], out mainlineSeedSettlementOf);
            mainlineSeedSettlementTxn = args[++i];
            break;
        case "--mainline-drop-table-path" when i + 1 < args.Length:
            mainlineDropTableState = true;
            mainlineDropTablePath = args[++i];
            break;

        case "--realm-break-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out realmBreakStateOf);
            break;

        case "--advance-realm" when i + 2 < args.Length:
            long.TryParse(args[++i], out advanceRealmOf);
            advanceRealmTxn = args[++i];
            break;

        case "--settle-drill" when i + 3 < args.Length:
            long.TryParse(args[++i], out settleDrillOf);
            settleDrillDungeon = args[++i];
            settleDrillTxn = args[++i];
            break;
        // 只给核对用：高难本本周已用次数 / 剩余次数，不经过进本逻辑。
        case "--hard-entry-state" when i + 2 < args.Length:
            long.TryParse(args[++i], out hardEntryStateOf);
            hardEntryDungeon = args[++i];
            break;
        // 只给核对用：把进本记录整体往前挪一周，验新的一周次数归零。
        case "--shift-entry-week" when i + 2 < args.Length:
            long.TryParse(args[++i], out shiftEntryWeekOf);
            shiftEntryWeekDungeon = args[++i];
            break;
        // 只给核对用：把强化表读出来，不连库、不启动进程。坏配置也走这里。
        case "--enhance-state":
            enhanceState = true;
            break;
        case "--enhance-config" when i + 1 < args.Length:
            enhanceConfigPath = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回强化等级、金币与三档石头余量。
        case "--enhance-level" when i + 2 < args.Length:
            long.TryParse(args[++i], out enhanceLevelOf);
            int.TryParse(args[++i], out enhanceSlotOf);
            break;
        // 只给核对用：这个强化事务号在表里有几行。
        case "--enhance-txn-count" when i + 1 < args.Length:
            enhanceTxnCount = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回保护券张数。
        case "--coupon-count" when i + 1 < args.Length:
            long.TryParse(args[++i], out couponCountOf);
            break;
        // 只给核对用：绕过业务层直写强化等级，验库层 CHECK 挡住越界。
        case "--raw-enhance-write" when i + 3 < args.Length:
            long.TryParse(args[++i], out rawEnhanceId);
            int.TryParse(args[++i], out rawEnhanceSlot);
            int.TryParse(args[++i], out rawEnhanceLevel);
            break;
        // 只给核对用：把转移表读出来，不连库、不启动进程。坏配置也走这里。
        case "--transfer-state":
            transferState = true;
            break;
        case "--transfer-config" when i + 1 < args.Length:
            transferConfigPath = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回两格等级、绑定、金币与转移石余量。
        case "--transfer-level" when i + 3 < args.Length:
            long.TryParse(args[++i], out transferStateOf);
            int.TryParse(args[++i], out transferFromSlot);
            int.TryParse(args[++i], out transferToSlot);
            break;
        // 只给核对用：这个转移事务号在表里有几行。
        case "--transfer-txn-count" when i + 1 < args.Length:
            transferTxnCount = args[++i];
            break;
        // 只给核对用：把结晶表读出来，不连库、不启动进程。坏配置也走这里。
        case "--crystal-state":
            crystalState = true;
            break;
        case "--crystal-config" when i + 1 < args.Length:
            crystalConfigPath = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回镶嵌状态与几个不该被改动的字段。
        // 第三个参数（结晶编号）可选，给了才顺带读背包余量。
        case "--crystal-sockets" when i + 2 < args.Length:
            long.TryParse(args[++i], out crystalStateOf);
            int.TryParse(args[++i], out crystalSlotOf);
            crystalItemOf = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i] : "";
            break;
        // 只给核对用：这个结晶事务号在表里有几行。
        case "--crystal-txn-count" when i + 1 < args.Length:
            crystalTxnCount = args[++i];
            break;
        // 只给核对用：把拍卖税率读出来，不连库、不启动进程。坏配置也走这里。
        case "--auction-state":
            auctionState = true;
            break;
        case "--auction-config" when i + 1 < args.Length:
            auctionConfigPath = args[++i];
            break;
        // 只给核对用：把商城清单读出来，不连库也不启动进程。坏清单也走这里。
        case "--shop-list":
            shopList = true;
            break;
        case "--shop-config" when i + 1 < args.Length:
            shopConfigPath = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回这个角色的元宝余额，
        // 用来证「清单外被拒之后元宝一分没动」。
        case "--shop-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out shopStateOf);
            break;
        // 只给核对用：数一数这个角色名下有几笔商城成交（事务表里 shop_buy: 前缀）。
        case "--shop-ledger" when i + 1 < args.Length:
            long.TryParse(args[++i], out shopLedgerOf);
            break;
        // P3-4a2：通行证表只读。与启动共用同一 SeasonPassTable.Load（含与 season.json、
        // shop.json、items.json、enhance.json 的交叉校验），坏表在这一步就起不来。
        case "--season-pass-list":
            seasonPassList = true;
            break;
        case "--season-pass-path" when i + 1 < args.Length:
            seasonPassPath = args[++i];
            break;
        // 只给核对用：从库里读回这一季付费轨开没开、哪几阶领过、包里那几件绑定态。
        case "--season-pass-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out seasonPassStateOf);
            break;
        // 季由全局服务回答，这里显式给出来只用于直连核对入口；网关那条路不带季。
        case "--pass-season" when i + 1 < args.Length:
            seasonPassSeason = args[++i];
            break;
        // 只给核对用：不经过网关直接买一次（扣元宝 + 开付费轨同一笔事务）。
        case "--buy-season-pass" when i + 1 < args.Length:
            long.TryParse(args[++i], out buySeasonPassOf);
            break;
        case "--pass-buy-txn" when i + 1 < args.Length:
            buySeasonPassTxn = args[++i];
            break;
        // 只给核对用：不经过网关直接领一阶（入包 + 领取流水同一笔事务）。
        case "--claim-season-pass" when i + 1 < args.Length:
            long.TryParse(args[++i], out claimSeasonPassOf);
            break;
        case "--pass-track" when i + 1 < args.Length:
            claimSeasonPassTrack = args[++i];
            break;
        case "--pass-stage" when i + 1 < args.Length:
            int.TryParse(args[++i], out claimSeasonPassStage);
            break;
        case "--pass-claim-txn" when i + 1 < args.Length:
            claimSeasonPassTxn = args[++i];
            break;
        // P3-4b：月叠表只读。与启动共用同一 MonthlyBundleTable.Load（含与 shop.json、
        // items.json、talisman.json 的交叉校验），塞蓝符 / 卷轴 / 经验道具在这一步就起不来。
        case "--monthly-bundle-list":
            monthlyBundleList = true;
            break;
        case "--monthly-bundle-path" when i + 1 < args.Length:
            monthlyBundlePath = args[++i];
            break;
        // C1：十二场内容只读。与启动共用同一份 Load（含与 dungeons.json、realms.json
        // 的交叉校验），塞奖励键 / 改场次 / 换地名在这一步就起不来。
        case "--mainline-list":
            mainlineList = true;
            break;
        case "--mainline-path" when i + 1 < args.Length:
            mainlinePath = args[++i];
            break;
        // P3-4b：角色栏表只读。与启动共用同一 CharacterSlotTable.Load（基础位 1、上限 2、
        // 商品必须是 second_slot 且没有假发货物）。
        case "--character-slot-list":
            characterSlotList = true;
            break;
        case "--character-slot-path" when i + 1 < args.Length:
            characterSlotPath = args[++i];
            break;
        // 只给核对用：读回这个账号有几个角色位、已经建了几个角色。
        case "--character-slots" when i + 1 < args.Length:
            characterSlotsOf = args[++i];
            break;
        // 只给核对用：读回这个账号的外观收藏（只读并集，不写库）。
        case "--wardrobe" when i + 1 < args.Length:
            wardrobeOf = args[++i];
            break;
        // 只给核对 / 种子用：把名额直推到指定值（只增不减，不扣钱）。产品路径不走这里。
        case "--raw-grant-character-slot" when i + 2 < args.Length:
            rawSlotAccount = args[++i];
            int.TryParse(args[++i], out rawSlotValue);
            break;
        // 只给核对用：把红符表读出来，不连库也不启动进程。坏表也走这里——
        // 「回复超过同级红药」「发货物不是红符」都在这一步就起不来。
        case "--talisman-list":
            talismanList = true;
            break;
        case "--talisman-config" when i + 1 < args.Length:
            talismanConfigPath = args[++i];
            break;
        // 只给核对用：从库里独立读回这个角色背包里有几张红符、绑没绑，
        // 用来证「用掉一张之后只少一张」。
        case "--talisman-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out talismanStateOf);
            break;
        // 只给核对用：把复活卷轴表读出来，不连库也不启动进程。坏表也走这里。
        case "--revive-scroll-list":
            scrollList = true;
            break;
        case "--revive-scroll-config" when i + 1 < args.Length:
            scrollConfigPath = args[++i];
            break;
        case "--revive-scroll-classes" when i + 1 < args.Length:
            scrollClassesPath = args[++i];
            break;
        // 只给核对用：从库里独立读回冷却到期、当前级经验、等级与背包张数，
        // 用来证「冷却跨场保留」「当前级经验是零时不扣、不掉级」。
        case "--revive-scroll-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out scrollStateOf);
            break;
        // 只给核对用：清掉这个角色的全局冷却。写库，不进玩法路径。
        case "--scroll-cooldown-reset" when i + 1 < args.Length:
            long.TryParse(args[++i], out scrollResetOf);
            break;
        // 只给核对用：把限时经验道具表读出来，不连库也不启动进程。坏表也走这里。
        case "--exp-booster-list":
            boosterList = true;
            break;
        case "--exp-booster-config" when i + 1 < args.Length:
            boosterConfigPath = args[++i];
            break;
        // 只给核对用：从库里独立读回到期时刻、剩余时间、绑定态与背包张数。
        case "--exp-booster-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out boosterStateOf);
            break;
        // 只给核对用：删掉这一行（回到「从来没用过」）。写库，不进玩法路径。
        case "--exp-booster-reset" when i + 1 < args.Length:
            long.TryParse(args[++i], out boosterResetOf);
            break;
        // 只给核对用：把到期时刻挪到过去，不必真等一段配置时长就能验「到点消失」。
        // 只动 expires_at，不动 used_at / last_txn_id，也不碰背包与经验。
        case "--exp-booster-expire" when i + 1 < args.Length:
            long.TryParse(args[++i], out boosterExpireOf);
            break;
        // 只给核对用：把解绑道具表读出来，不连库也不启动进程。坏表也走这里。
        case "--unbind-list":
            unbindList = true;
            break;
        case "--unbind-config" when i + 1 < args.Length:
            unbindConfigPath = args[++i];
            break;
        // 只给核对用：按这一格的绑定时长报「要几张」，不写库。
        case "--unbind-quote" when i + 2 < args.Length:
            long.TryParse(args[++i], out unbindQuoteOf);
            int.TryParse(args[++i], out unbindQuoteSlot);
            break;
        // 只给核对用：从库里独立读回这一格的绑定态、绑定时长与解绑道具张数。
        case "--unbind-state" when i + 2 < args.Length:
            long.TryParse(args[++i], out unbindStateOf);
            int.TryParse(args[++i], out unbindStateSlot);
            break;
        // 只给核对用：把 bound_at 挪到 N 小时前，验「绑定越久张数越少」。
        // 只动 bound_at（并保证 bound=true），不动数量与物品编号，不进玩法路径。
        case "--raw-bound-at" when i + 3 < args.Length:
            long.TryParse(args[++i], out rawBoundAtOf);
            int.TryParse(args[++i], out rawBoundAtSlot);
            int.TryParse(args[++i], out rawBoundAtHours);
            break;
        // 只给核对用：把充值商品表读出来，不连库也不启动进程。坏表也走这里。
        case "--recharge-list":
            rechargeList = true;
            break;
        case "--recharge-config" when i + 1 < args.Length:
            rechargeConfigPath = args[++i];
            break;
        // 只给核对用：按订单号从库里独立读回状态、金额、发放时刻与余额。
        case "--recharge-state" when i + 1 < args.Length:
            rechargeStateOf = args[++i];
            break;
        // 只给核对用：不看回包，从库里独立读回买方/卖方金币、全库金币分布、
        // 挂单状态与成交流水，用来证「这笔税不进任何背包」。
        case "--auction-ledger" when i + 3 < args.Length:
            long.TryParse(args[++i], out auctionBuyerOf);
            long.TryParse(args[++i], out auctionSellerOf);
            long.TryParse(args[++i], out auctionListingOf);
            auctionTxnOf = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i] : "";
            break;
        // 只给核对用：元宝寄售前后双方元宝、金币与全库总量，用来证元宝等量转移、
        // 金币只少一笔税。
        case "--ingot-ledger" when i + 3 < args.Length:
            long.TryParse(args[++i], out ingotBuyerOf);
            long.TryParse(args[++i], out ingotSellerOf);
            long.TryParse(args[++i], out ingotListingOf);
            ingotTxnOf = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i] : "";
            break;
        // P3-2a：灵石兑换表只读核对。与启动共用同一 StoneExchangeTable.Load，
        // 不连库也不启动进程；--stone-exchange-table-path 指到临时兑换表与币种表验坏配置。
        case "--stone-exchange-table":
            stoneExchangeTableState = true;
            break;
        case "--stone-exchange-table-path" when i + 2 < args.Length:
            stoneExchangeTableState = true;
            stoneExchangeTablePath = args[++i];
            stoneExchangeCurrencyPath = args[++i];
            break;
        // P3-2a：城池表只读核对。与启动共用同一 CityTable.Load，不连库也不启动进程。
        case "--city-table":
            cityTableState = true;
            break;
        case "--city-table-path" when i + 1 < args.Length:
            cityTableState = true;
            cityTablePath = args[++i];
            break;
        // P3-2a：连库走一次相邻档兑换（可直接 new repository）。
        case "--exchange-stone" when i + 3 < args.Length:
            long.TryParse(args[++i], out exchangeStoneOf);
            exchangeStoneKind = args[++i];
            exchangeStoneTxn = args[++i];
            break;
        case "--stone-exchange-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out stoneExchangeStateOf);
            break;
        case "--city-state":
            cityState = true;
            break;
        // P3-3a4：领地表只读核对（不连库），与启动共用同一份 SiegeTerritoryPolicy.Load。
        case "--siege-territory-table":
            siegeTerritoryTableState = true;
            break;
        case "--siege-territory-table-path" when i + 1 < args.Length:
            siegeTerritoryTableState = true;
            siegeTerritoryTablePath = args[++i];
            break;
        case "--siege-result-state":
            siegeResultState = true;
            break;
        // 核对用：直接调同一笔写回事务，验重放幂等与各类拒绝，不必每次都打一场城战。
        case "--apply-siege-result" when i + 5 < args.Length:
            applySiegeResultTxn = args[++i];
            applySiegeResultCity = args[++i];
            applySiegeResultMatch = args[++i];
            uint.TryParse(args[++i], out applySiegeResultTeam);
            ulong.TryParse(args[++i], out applySiegeResultSect);
            break;
        case "--stone-listing-state":
            stoneListingState = true;
            break;
        default:
            Console.WriteLine("unknown_arg=" + args[i]);
            return 64;
    }
}

// P2-2a 副本表只读核对：与真实启动共用同一 DungeonTable.Load，不连库也不启动进程，
// 坏配置（高难缺字段、下限超过上限、普通本配了高难三项）能在这一步直接看出来。
if (dungeonTableState)
{
    try
    {
        var table = DungeonTable.Load(
            dungeonTablePath ?? CharacterConfig.Load(configPath).DungeonTable);
        Console.WriteLine("dungeon_table_state=ok version=" + table.Version);
        foreach (var dungeon in table.All)
        {
            Console.WriteLine("dungeon=" + dungeon.Id
                + " tier=" + dungeon.Tier
                + " min=" + dungeon.MinPlayers
                + " max=" + dungeon.MaxPlayers
                + " entries_per_week=" + dungeon.EntriesPerWeek
                + " reward_per_player=" + dungeon.RewardPerPlayer
                + " fatigue=" + (dungeon.ChargesFatigue ? "true" : "false")
                + " share=" + (dungeon.TakesWeeklyShare ? "true" : "false")
                + " realm=" + (dungeon.Realm ?? "none")
                + " drill=" + dungeon.Drill);
        }
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// N7：主线交任务发奖表只读核对。
if (mainlineDropTableState)
{
    try
    {
        var items = ItemTable.Load("config/items.json");
        var currencies = CurrencyTable.Load("config/currency.json");
        var path = mainlineDropTablePath
            ?? Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(configPath))!, "mainline_drops.json"));
        var table = MainlineDropTable.Load(path, items, currencies);
        Console.WriteLine("mainline_drop_table_state=ok version=" + table.Version
            + " stages=" + table.StageCount
            + " pending=" + (table.Pending ? "true" : "false"));
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// N6：掉落表只读核对，坏配置（灵石币种、表外物品）在这里直接被挡住。
if (dungeonDropTableState)
{
    try
    {
        var items = ItemTable.Load("config/items.json");
        var currencies = CurrencyTable.Load("config/currency.json");
        var path = dungeonDropTablePath
            ?? Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(configPath))!, "dungeon_drops.json"));
        var table = DungeonDropTable.Load(path, items, currencies);
        Console.WriteLine("dungeon_drop_table_state=ok version=" + table.Version
            + " dungeons=" + table.DungeonCount);
        foreach (var id in new[] { "abyss", "lair" })
        {
            var lines = table.LinesFor(id);
            if (lines.Count == 0)
            {
                continue;
            }

            Console.WriteLine("dungeon_drop=" + id + " lines=" + lines.Count);
        }
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// P3-1a1 境界表只读核对：与启动共用同一 RealmTable.Load，不连库也不启动进程。
// 坏配置（缺开关、首发段配了开关、别名指错、旧副本写了 realm）能在这一步直接看出来。
if (realmTableState)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var dungeons = DungeonTable.Load(
            realmDungeonPath ?? characterConfig.DungeonTable);
        var currencies = CurrencyTable.Load("config/currency.json");
        var table = RealmTable.Load(realmTablePath ?? characterConfig.RealmTable, dungeons, currencies);
        Console.WriteLine("realm_table_state=ok version=" + table.Version
            + " realms=" + table.Realms.Count
            + " launch_until=" + table.LaunchUntil
            + " open=" + string.Join(",", table.Realms.Where(table.IsOpen))
            + " closed=" + string.Join(",", table.Realms.Where(r => !table.IsOpen(r))));
        for (var r = 0; r < table.Realms.Count; r++)
        {
            Console.WriteLine("realm=" + (r + 1) + ":" + table.Realms[r]
                + ":" + (table.IsOpen(table.Realms[r]) ? "open" : "closed"));
        }
        foreach (var pair in table.Aliases)
        {
            Console.WriteLine("alias=" + pair.Key + "=" + pair.Value);
        }
        // P3-1a2：每段的突破要求，数值全部来自配置。
        foreach (var requirement in table.Requirements)
        {
            Console.WriteLine("advance_req=" + requirement.From + "->" + requirement.Next
                + " drill=" + requirement.Drill
                + " stone=" + requirement.Stone
                + " stone_cost=" + requirement.StoneCost
                + " success_pct=" + requirement.SuccessPct
                + " main_stage=" + requirement.MainStage
                + " side_quests=" + string.Join("+", requirement.SideQuests));
        }
        foreach (var realm in table.Realms)
        {
            var advance = table.CheckAdvance(realm);
            Console.WriteLine("advance=" + realm + "->"
                + (advance.Next.Length == 0 ? "none" : advance.Next)
                + ":" + (advance.Ok ? "ok" : advance.Reason));
        }
        foreach (var dungeon in dungeons.All)
        {
            // 用首发那一段去试：关着时应该回 realm_locked，开着时也只是 realm_not_enough；
            // 旧副本没写 realm，这里应当直接 ok。
            var gate = table.CheckDungeon(dungeon.Realm, table.LaunchUntil);
            Console.WriteLine("dungeon_gate=" + dungeon.Id
                + " realm=" + (dungeon.Realm ?? "none")
                + " open=" + (dungeon.Realm is null || table.IsOpen(dungeon.Realm) ? "true" : "false")
                + " from_launch=" + (gate.Ok ? "ok" : gate.Reason));
        }
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// 经验表只读核对（P2-1c）：与真实启动共用同一 ExpTable.Load，不连库也不启动进程，
// 坏配置能在这一步直接看出来。gain 三个数证明「组队加成 / 同宗门加成 / 单人」三档口径。
if (expConfigState)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var table = ExpTable.Load(expConfigPath ?? characterConfig.ExpTable);
        Console.WriteLine("exp_config_state=ok version=" + table.Version
            + " level_cap=" + table.LevelCap
            + " base_exp=" + table.BaseExp
            + " party_bonus_pct=" + table.PartyBonusPct
            + " party_min_size=" + table.PartyMinSize
            + " party_max_size=" + table.PartyMaxSize
            + " same_sect_bonus_pct=" + table.SameSectBonusPct
            + " exp_per_level=" + table.ExpPerLevel
            + " gain1=" + table.Gain(1, false)
            + " gain2=" + table.Gain(2, false)
            + " gain2_same=" + table.Gain(2, true));
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// 强化表只读核对：与启动共用同一套校验（三档材料要在物品表里、金币要在币种表里），
// 不连库也不启动进程，坏配置能在这一步直接看出来。
if (enhanceState)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var items = ItemTable.Load("config/items.json");
        var currencies = CurrencyTable.Load("config/currency.json");
        var table = EnhanceTable.Load(enhanceConfigPath ?? characterConfig.EnhanceTable, items, currencies);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine("enhance_table_state=ok version=" + table.Version
            + " max_level=" + table.MaxLevel
            + " gold_kind=" + table.GoldKind
            + " tiers=" + string.Join(",", table.Tiers.Select(
                t => t.Id + ":" + t.FromLevel + "-" + t.ToLevel + ":" + t.StoneId))
            + " rates=" + string.Join(",", table.Tiers.Select(
                t => t.SuccessRate.ToString("0.##", culture)))
            + " gold=" + string.Join(",", table.Tiers.Select(t => t.GoldCost))
            + " stones=" + string.Join(",", table.Tiers.Select(t => t.StoneCost))
            + " coupon=" + table.ProtectItemId
            + " coupon_bind=always");
        Console.WriteLine("enhance_gate=ok target=7 tier=" + table.TierFor(7).Id
            + " stone=" + table.TierFor(7).StoneId
            + " target=12 tier=" + table.TierFor(12).Id);
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// 转移表只读核对：与启动共用同一套校验（转移石要在物品表里、费用表要照
// 强化上限与递增关系），不连库也不启动进程，坏配置能在这一步直接看出来。
if (transferState)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var items = ItemTable.Load("config/items.json");
        var currencies = CurrencyTable.Load("config/currency.json");
        var enhanceTable = EnhanceTable.Load(
            enhanceConfigPath ?? characterConfig.EnhanceTable, items, currencies);
        var table = TransferTable.Load(
            transferConfigPath ?? characterConfig.TransferTable, items, currencies, enhanceTable);
        Console.WriteLine("transfer_table_state=ok version=" + table.Version
            + " max_level=" + table.MaxLevel
            + " transfer_stone=" + table.StoneItemId
            + " transfer_stone_cost=" + table.StoneCost
            + " transfer_gold_kind=" + table.GoldKind
            + " transfer_gold=" + string.Join(",", table.GoldCosts));
        Console.WriteLine("transfer_gate=ok level=1 gold=" + table.GoldCostFor(1)
            + " level=" + table.MaxLevel + " gold=" + table.GoldCostFor(table.MaxLevel));
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// 结晶表只读核对：与启动共用同一套校验（三类写定、装备部位不能漏配、
// 结晶要在物品表里），不连库也不启动进程，坏配置能在这一步直接看出来。
if (crystalState)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var items = ItemTable.Load("config/items.json");
        var table = CrystalTable.Load(crystalConfigPath ?? characterConfig.CrystalTable, items);
        Console.WriteLine("crystal_table_state=ok version=" + table.Version
            + " crystal_sockets=" + table.SocketCount
            + " crystal_categories=" + string.Join(",", CrystalTable.WrittenCategories)
            + " crystal_parts=" + string.Join(",", table.EquipmentParts
                .OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value))
            + " crystal_allowed=" + string.Join(",", CrystalTable.WrittenCategories
                .Select(c => c + ":" + string.Join("+", table.CategoryParts[c])))
            + " crystal_items=" + string.Join(",", table.Crystals
                .OrderBy(c => c.Id).Select(c => c.Id + ":" + c.Category + ":" + c.FixedValue)));
        Console.WriteLine("crystal_gate=ok " + string.Join(" ", CrystalTable.WrittenCategories
            .Select(c => c + "=" + string.Join("+", table.CategoryParts[c]))));
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// 商城清单只读核对：与启动共用同一套校验（十八种白名单、币种必须是元宝、
// 标价必须是正整数），不连库也不启动进程。坏清单在这一步就能看出来。
if (shopList)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var shopCurrencies = CurrencyTable.Load("config/currency.json");
        // 发货校验要物品表与强化表：保护券的发货物必须就是 enhance.json 那一个编号，
        // 与启动走同一套 Load，坏清单在这里就露出来。
        var shopItems = ItemTable.Load("config/items.json");
        var shopEnhance = EnhanceTable.Load(characterConfig.EnhanceTable, shopItems, shopCurrencies);
        // 红符的发货物必须就是 talisman.json 那一个编号，与启动走同一套 Load。
        var shopTalismans = TalismanTable.Load(characterConfig.TalismanTable, shopItems);
        // P1-5c6：复活卷轴的发货物必须就是 revive_scroll.json 那一个编号，
        // 同样与启动走同一套 Load（比例从 classes.json 读，不另写一份）。
        var shopScrolls = ReviveScrollTable.Load(
            characterConfig.ReviveScrollTable, "config/classes.json",
            shopItems, shopTalismans);
        // P1-5c7：限时经验道具的发货物必须就是 exp_booster.json 那一个编号，
        // 同样与启动走同一套 Load。
        var shopBoosters = ExpBoosterTable.Load(
            characterConfig.ExpBoosterTable, shopItems, shopTalismans, shopScrolls);
        // P1-5c8：解绑道具的发货物必须就是 unbind_item.json 那一个编号。
        var shopUnbinds = UnbindTable.Load(
            characterConfig.UnbindTable, shopItems, shopTalismans, shopScrolls, shopBoosters);
        var table = ShopTable.Load(
            shopConfigPath ?? characterConfig.ShopTable, shopCurrencies, shopItems, shopEnhance,
            shopTalismans, shopScrolls, shopBoosters, shopUnbinds);
        Console.WriteLine("shop_list_state=ok version=" + table.Version
            + " currency=" + table.Currency
            + " items=" + table.Items.Count
            + " kinds=" + ShopTable.AllowedKinds.Length
            + " delivered_kinds=" + ShopTable.DeliveredKinds.Length
            + " deliverable_items=" + table.Items.Count(i => i.Deliverable));
        foreach (var item in table.Items)
        {
            Console.WriteLine("shop_item=" + item.Id
                + " kind=" + item.Kind
                + " price=" + item.PriceYuanbao
                + " delivery=" + (item.Deliverable ? item.DeliveryItemId + "x" + item.DeliveryCount : "pending")
                + " name=" + item.Name);
        }
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// P3-4a2 通行证只读核对：与启动共用同一套校验（免费轨只许称号与外观碎片、付费轨只许
// 当季外观 / 洞府摆件 / 保护券、保护券必须就是 enhance.json 那一件且到账即绑、
// 标价只从 shop.json 那条 season_pass 商品读），不连库也不启动进程。
if (seasonPassList)
{
    try
    {
        var passConfig = CharacterConfig.Load(configPath);
        var (passTable, _) = LoadSeasonPass(passConfig, seasonPassPath);
        Console.WriteLine("season_pass_list_state=ok version=" + passTable.Version
            + " pending=" + (passTable.Pending ? "true" : "false")
            + " seasons=" + passTable.Seasons.Count
            + " product=" + passTable.ProductId
            + " price=" + passTable.PriceYuanbao
            + " protect=" + passTable.ProtectItemId
            + " protect_per_stage_max=" + passTable.ProtectPerStageMax);
        foreach (var season in passTable.Seasons)
        {
            Console.WriteLine("season_pass_season=" + season.SeasonId
                + " free=" + season.Free.Count
                + " paid=" + season.Paid.Count);
            foreach (var stage in season.Free)
            {
                Console.WriteLine("season_pass_stage=" + season.SeasonId + " track=free"
                    + " stage=" + stage.Stage
                    + " kind=" + stage.Rewards[0].Kind
                    + " item=" + stage.Rewards[0].ItemId
                    + " count=" + stage.Rewards[0].Count
                    + " bind=always");
            }

            foreach (var stage in season.Paid)
            {
                Console.WriteLine("season_pass_stage=" + season.SeasonId + " track=paid"
                    + " stage=" + stage.Stage
                    + " kind=" + stage.Rewards[0].Kind
                    + " item=" + stage.Rewards[0].ItemId
                    + " count=" + stage.Rewards[0].Count
                    + " bind=always");
            }
        }
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// C1：主线十二场只读核对。与启动共用同一份 MainlineTable.Load（含与 dungeons.json
// 的 main、realms.json 的首发三段交叉校验），不连库也不启动进程。
if (mainlineList)
{
    try
    {
        var mainlineConfig = CharacterConfig.Load(configPath);
        var table = LoadMainline(mainlineConfig, mainlinePath);
        Console.WriteLine("mainline_list_state=ok version=" + table.Version
            + " pending=" + (table.Pending ? "true" : "false")
            + " stages=" + table.Stages.Count
            + " battle_dungeon=" + table.BattleDungeon
            + " alignments=" + string.Join("+", table.AlignmentChoices)
            + " places=" + table.Places.Count
            + " factions=" + table.Factions.Count
            + " speakers=" + table.Speakers.Count);
        foreach (var stage in table.Stages)
        {
            Console.WriteLine("mainline_stage=" + stage.Stage
                + " title=" + stage.Title
                + " regions=" + string.Join("+", stage.Regions)
                + " faction=" + stage.Faction
                + " speakers=" + string.Join("+", stage.Speakers)
                + " dialogue=" + string.Join("+", stage.Dialogue.Select(l => l.Speaker + "@" + l.Chapter))
                + " level_min=" + stage.LevelMin
                + " requires_breakthrough=" + stage.RequiresBreakthrough
                + " alignment_choice=" + (stage.AlignmentChoice ? "true" : "false")
                + " battle=" + stage.Battle);
        }
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// 拍卖税率只读核对：与启动共用同一套 5%–8% 写定区间校验，不连库也不启动进程。
if (monthlyBundleList)
{
    try
    {
        var bundleConfig = CharacterConfig.Load(configPath);
        var bundleTable = LoadMonthlyBundle(bundleConfig, monthlyBundlePath);
        Console.WriteLine("monthly_bundle_list_state=ok version=" + bundleTable.Version
            + " pending=" + (bundleTable.Pending ? "true" : "false")
            + " product=" + bundleTable.ProductId
            + " price=" + bundleTable.PriceYuanbao
            + " days=" + bundleTable.Days
            + " kinds=" + bundleTable.Entries.Count);
        foreach (var entry in bundleTable.Entries)
        {
            Console.WriteLine("monthly_bundle_item=" + entry.Kind
                + " item=" + entry.ItemId
                + " per_day=" + entry.PerDay
                + " count=" + entry.Count
                + " bind=always");
        }
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// P3-4b：第二角色栏只读核对。与启动共用同一 CharacterSlotTable.Load，
// 基础位不是 1、上限不是「基础位 + 1」、商品不是 second_slot，都在这一步起不来。
if (characterSlotList)
{
    try
    {
        var slotConfig = CharacterConfig.Load(configPath);
        var slotTable = LoadCharacterSlot(slotConfig, characterSlotPath);
        Console.WriteLine("character_slot_list_state=ok version=" + slotTable.Version
            + " pending=" + (slotTable.Pending ? "true" : "false")
            + " product=" + slotTable.ProductId
            + " price=" + slotTable.PriceYuanbao
            + " base_slots=" + slotTable.BaseSlots
            + " max_slots=" + slotTable.MaxSlots
            + " table=character_slot");
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// 拍卖税率只读核对：与启动共用同一套 5%–8% 写定区间校验，不连库也不启动进程。
if (auctionState)
{
    try
    {
        var characterConfig = CharacterConfig.Load(configPath);
        var currencies = CurrencyTable.Load("config/currency.json");
        var table = AuctionTable.Load(auctionConfigPath ?? characterConfig.AuctionTable, currencies);
        Console.WriteLine("auction_table_state=ok version=" + table.Version
            + " tax_rate_pct=" + table.TaxRatePct
            + " tax_min=" + AuctionTable.WrittenMinRatePct
            + " tax_max=" + AuctionTable.WrittenMaxRatePct
            + " gold_kind=" + table.GoldKind
            + " rounding=" + table.Rounding
            + " ingot_kind=" + table.IngotKind);
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// 红符表只读核对：与启动共用同一套校验（物品在 items.json 里、是消耗品、到账即绑、
// 回复不超过同级红药、冷却组必须是 heal），不连库也不启动进程。坏表在这一步就能看出来。
if (talismanList)
{
    try
    {
        var talismanConfig = CharacterConfig.Load(configPath);
        var talismanItems = ItemTable.Load("config/items.json");
        var table = TalismanTable.Load(
            talismanConfigPath ?? talismanConfig.TalismanTable, talismanItems);
        var red = table.RedTalisman;
        var blue = table.BlueTalisman;
        Console.WriteLine("talisman_list_state=ok version=" + table.Version
            + " red_item=" + (red?.ItemId ?? "-")
            + " hp_restore=" + (red?.HpRestore ?? 0)
            + " potion_hp_restore=" + (red?.PotionHpRestore ?? 0)
            + " cooldown_group=" + (red?.CooldownGroup ?? "-")
            + " cooldown_ms=" + (red?.CooldownMs ?? 0)
            + " blue_item=" + (blue?.ItemId ?? "-")
            + " mana_restore=" + (blue?.ManaRestore ?? 0)
            + " potion_mana_restore=" + (blue?.PotionManaRestore ?? 0)
            + " blue_group=" + (blue?.CooldownGroup ?? "-")
            + " blue_cooldown_ms=" + (blue?.CooldownMs ?? 0));
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// P1-5c6 卷轴表只读核对：与启动共用同一套校验（物品在 items.json 里、是消耗品、
// 到账即绑、不能和符同编号、冷却为正、两个基础比例合法），不连库也不启动进程。
// 坏表在这一步就能看出来；临时表用 --revive-scroll-config 指过去。
if (scrollList)
{
    try
    {
        var scrollConfig = CharacterConfig.Load(configPath);
        var scrollItems = ItemTable.Load("config/items.json");
        var scrollTalismans = TalismanTable.Load(scrollConfig.TalismanTable, scrollItems);
        var table = ReviveScrollTable.Load(
            scrollConfigPath ?? scrollConfig.ReviveScrollTable,
            scrollClassesPath ?? "config/classes.json", scrollItems, scrollTalismans);
        Console.WriteLine("revive_scroll_list_state=ok version=" + table.Version
            + " item=" + table.ReviveScroll.ItemId
            + " name=" + table.ReviveScroll.Name
            + " cooldown_ms=" + table.ReviveScroll.CooldownMs
            + " hp_restore_pct=" + table.HpRestorePct
            + " exp_cost_pct=" + table.ExpCostPct);
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// P1-5c7 限时经验道具表只读核对：与启动共用同一套校验（物品在 items.json 里、
// 是消耗品、到账即绑、不与符 / 卷轴同编号、时长为正、倍率必须提高经验），
// 不连库也不启动进程。坏表在这一步就能看出来；临时表用 --exp-booster-config 指过去。
if (boosterList)
{
    try
    {
        var boosterConfig = CharacterConfig.Load(configPath);
        var boosterItems = ItemTable.Load("config/items.json");
        var boosterTalismans = TalismanTable.Load(boosterConfig.TalismanTable, boosterItems);
        var boosterScrolls = ReviveScrollTable.Load(
            boosterConfig.ReviveScrollTable, "config/classes.json",
            boosterItems, boosterTalismans);
        var table = ExpBoosterTable.Load(
            boosterConfigPath ?? boosterConfig.ExpBoosterTable,
            boosterItems, boosterTalismans, boosterScrolls);
        Console.WriteLine("exp_booster_list_state=ok version=" + table.Version
            + " item=" + table.ExpBooster.ItemId
            + " name=" + table.ExpBooster.Name
            + " duration_ms=" + table.ExpBooster.DurationMs
            + " gain_pct=" + table.ExpBooster.GainPct);
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// P1-5d 内部充值商品表只读核对：与启动共用同一套校验（schema / version /
// 编号唯一 / 元宝与金额为正），不连库也不启动进程；临时表用 --recharge-config 指过去。
if (rechargeList)
{
    try
    {
        var rechargeConfig = CharacterConfig.Load(configPath);
        var table = RechargeTable.Load(rechargeConfigPath ?? rechargeConfig.RechargeTable);
        Console.WriteLine("recharge_list_state=ok version=" + table.Version
            + " products=" + table.Products.Count
            + " product=" + string.Join(",", table.Products
                .Select(p => p.Id + ":" + p.Yuanbao + ":" + p.RmbCents)));
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// P1-5c8 解绑道具表只读核对：与启动共用同一套校验（物品在 items.json 里、
// 是消耗品、到账即绑、不与符 / 卷轴 / 经验道具同编号、档位覆盖 0 小时、
// 张数随时长不增）。不连库也不启动进程；临时表用 --unbind-config 指过去。
if (unbindList)
{
    try
    {
        var unbindConfig = CharacterConfig.Load(configPath);
        var unbindItems = ItemTable.Load("config/items.json");
        var unbindTalismans = TalismanTable.Load(unbindConfig.TalismanTable, unbindItems);
        var unbindScrolls = ReviveScrollTable.Load(
            unbindConfig.ReviveScrollTable, "config/classes.json",
            unbindItems, unbindTalismans);
        var unbindBoosters = ExpBoosterTable.Load(
            unbindConfig.ExpBoosterTable, unbindItems, unbindTalismans, unbindScrolls);
        var table = UnbindTable.Load(
            unbindConfigPath ?? unbindConfig.UnbindTable, unbindItems,
            unbindTalismans, unbindScrolls, unbindBoosters);
        Console.WriteLine("unbind_list_state=ok version=" + table.Version
            + " item=" + table.Unbind.ItemId
            + " name=" + table.Unbind.Name
            + " tiers=" + table.Unbind.Tiers.Count
            + " floor=" + table.FloorCost
            + " tier=" + string.Join(",", table.Unbind.Tiers
                .Select(t => t.MinBoundHours + "h:" + t.Cost)));
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

if (weaponRules)
{
    try
    {
        var itemTable = ItemTable.Load(weaponRulesItemPath ?? "config/items.json");
        var branchTable = BranchTable.Load(weaponRulesBranchPath ?? "config/branches.json");
        Console.WriteLine("weapon_lock_level=" + branchTable.WeaponLockLevel);
        foreach (var item in itemTable.All.Where(i => i.IsWeapon).OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            Console.WriteLine("weapon=" + item.Id + " kind=" + item.WeaponKind
                + " require_level=" + item.RequireLevel);
        }
        foreach (var cls in branchTable.All.Select(b => b.ClassId)
            .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal))
        {
            Console.WriteLine("class=" + cls + " kinds=" + string.Join(",", branchTable.WeaponKindsOf(cls)));
        }
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

// P3-2a 灵石兑换表只读核对：与启动共用同一 StoneExchangeTable.Load，不连库、
// 不启动进程。坏配置（缺字段、档位不在币种表、最高一档中文名与币种表不一致、
// 比例产出越界、最高一档与充值货币混档）在这一步就能看出来。
if (stoneExchangeTableState)
{
    try
    {
        var stoneConfig = CharacterConfig.Load(configPath);
        var stoneCurrencies = CurrencyTable.Load(
            stoneExchangeCurrencyPath ?? "config/currency.json");
        var stoneTable = StoneExchangeTable.Load(
            stoneExchangeTablePath ?? stoneConfig.StoneExchangeTable, stoneCurrencies);
        Console.WriteLine("stone_exchange_state=ok version=" + stoneTable.Version
            + " tiers=" + stoneTable.Tiers.Count
            + " ratio=" + stoneTable.Ratio
            + " gain=" + stoneTable.Gain
            + " highest=" + stoneTable.HighestKind + "/" + stoneTable.HighestName
            + " ingot=" + stoneTable.IngotKind
            + " tiers_list=" + string.Join(",", stoneTable.Tiers));
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// P3-2a 城池表只读核对：与启动共用同一 CityTable.Load，不连库、不启动进程。
// 坏配置（vein_id 对不上 gather.json 的 spirit_stone 节点、gather 里没有矿脉节点、
// city 里夹带个人采集点或商品字段）在这一步就能看出来。
if (cityTableState)
{
    try
    {
        var cityConfig = CharacterConfig.Load(configPath);
        var cityTable = CityTable.Load(cityTablePath ?? cityConfig.CityTable);
        Console.WriteLine("city_table_state=ok version=" + cityTable.Version
            + " cities=" + cityTable.Cities.Count
            + " gather_table=" + cityTable.GatherTable);
        foreach (var city in cityTable.Cities)
        {
            Console.WriteLine("city=" + city.CityId
                + " name=" + city.Name
                + " vein=" + city.VeinId
                + " tax_pct=" + city.TaxPct
                + " owner=" + (city.OwnerSect ?? ""));
        }
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// P3-3a4 领地表只读核对：不连库、不启动进程，与启动共用 SiegeTerritoryPolicy.Load。
// 坏配置（税率越界、城池编号对不上 city.json、配置里预写归属）在这一步就能看出来。
if (siegeTerritoryTableState)
{
    try
    {
        var cfg = CharacterConfig.Load(configPath);
        var cityTable = CityTable.Load(cfg.CityTable);
        var policy = SiegeTerritoryPolicy.Load(
            siegeTerritoryTablePath ?? cfg.SiegeTerritoryTable, cityTable);
        Console.WriteLine("siege_territory_table_state=ok version=" + policy.Version
            + " pending=" + (policy.Pending ? "true" : "false"));
        foreach (var city in cityTable.Cities)
        {
            Console.WriteLine("siege_territory_city=city=" + city.CityId
                + " tax_pct=" + policy.TaxPctOf(city.CityId));
        }
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
    catch (IOException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }

    return 0;
}

// 只读核对与清理走这里；正常启动不经过（gRPC 服务在下面）。
if (reportName != null || deleteName != null || stats || migrate || rawLevelWrite != 0 || levelOf != 0
    || branchStateOf != 0 || advanceBranchId != 0 || canEquipOf != 0
    || bindingOf != null || rawCurrencyId != 0 || txnCount != null || itemCountOf != 0
    || reservesOf != 0 || entryCountOf != 0 || fatigueStateOf != 0 || setFatigueDay != null
    || rawFatigueId != 0 || shareStateOf != 0 || setWeekDay != null
    || rawShareId != 0 || sharesTotalDungeon != null || expStateOf != 0 || rawExpId != 0
    || enhanceLevelOf != 0 || enhanceTxnCount != null || couponCountOf != 0 || rawEnhanceId != 0
    || transferStateOf != 0 || transferTxnCount != null
    || crystalStateOf != 0 || crystalTxnCount != null || auctionBuyerOf != 0
    || ingotBuyerOf != 0 || shopStateOf != 0 || shopLedgerOf != 0 || talismanStateOf != 0
    || scrollStateOf != 0 || scrollResetOf != 0
    || boosterStateOf != 0 || boosterResetOf != 0 || boosterExpireOf != 0
    || unbindQuoteOf != 0 || unbindStateOf != 0 || rawBoundAtOf != 0
    || hardEntryStateOf != 0 || shiftEntryWeekOf != 0
    || rechargeStateOf != null || realmStateOf != 0 || rawRealmId != 0
    || rawDrillId != 0 || rawMainStageId != 0 || completeQuestOf != 0
    || realmBreakStateOf != 0 || advanceRealmOf != 0 || settleDrillOf != 0
    || exchangeStoneOf != 0 || stoneExchangeStateOf != 0 || cityState || stoneListingState
    || siegeResultState || applySiegeResultTxn != null
    || seasonPassStateOf != 0 || buySeasonPassOf != 0 || claimSeasonPassOf != 0
    || characterSlotsOf != null || wardrobeOf != null || rawSlotAccount != null
    || mainlineStateOf != 0 || mainlineTurnInOf != 0 || mainlineBreakthroughOf != 0
    || mainlineSeedSettlementOf != 0)
{
    var connectionString = DbEnv.Resolve();
    if (connectionString == null)
    {
        Console.WriteLine("db_env=missing 需要 " + DbEnv.EnvName + " 或 " + DbEnv.DefaultEnvFile);
        return 3;
    }

    Console.WriteLine("db_env=" + DbEnv.Source());
    await using var dataSource = NpgsqlDataSource.Create(DbEnv.WithPoolLimit(connectionString, 2));
    var characters = new CharacterRepository(dataSource);

    try
    {
        if (migrate)
        {
            var sql = File.ReadAllText(sqlPath);
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("migrate=ok sql=" + sqlPath);
        }

        if (scrollResetOf != 0)
        {
            // 只给核对用：清掉全局冷却，让同一轮能连着验「用掉」「经验为零」等场景。
            await using var reset = dataSource.CreateCommand(
                "delete from \"角色\".revive_scroll_cooldown where character_id = $1");
            reset.Parameters.AddWithValue(scrollResetOf);
            var rows = await reset.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("scroll_cooldown_reset=ok id=" + scrollResetOf + " rows=" + rows);
        }

        if (boosterResetOf != 0)
        {
            // 只给核对用：删掉这一行，回到「从来没用过」，不碰背包与经验。
            await using var reset = dataSource.CreateCommand(
                "delete from \"角色\".exp_booster where character_id = $1");
            reset.Parameters.AddWithValue(boosterResetOf);
            var rows = await reset.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("exp_booster_reset=ok id=" + boosterResetOf + " rows=" + rows);
        }

        if (boosterExpireOf != 0)
        {
            // 只给核对用：把到期时刻挪到过去，验「到点后经验恢复原倍率」，
            // 不用真等配置里那一段时长。只动 expires_at，别的列一概不写。
            await using var expire = dataSource.CreateCommand(
                "update \"角色\".exp_booster set expires_at = now() - interval '1 second' "
                + "where character_id = $1");
            expire.Parameters.AddWithValue(boosterExpireOf);
            var rows = await expire.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("exp_booster_expire=ok id=" + boosterExpireOf + " rows=" + rows);
        }

        if (deleteName != null)
        {
            var removed = await characters.DeleteAsync(deleteName, CancellationToken.None);
            Console.WriteLine("delete=ok name=" + deleteName + " rows=" + removed);
        }

        if (stats)
        {
            Console.WriteLine("characters=" + await characters.CountAsync(CancellationToken.None));
        }

        if (rawLevelWrite != 0)
        {
            try
            {
                await characters.RawWriteLevelAsync(rawLevelWrite, rawLevelValue, CancellationToken.None);
                Console.WriteLine("raw_level=written id=" + rawLevelWrite + " level=" + rawLevelValue);
            }
            catch (PostgresException ex)
            {
                // 核对要看到的就是这个：库层的 CHECK 把越界值挡住了。
                Console.WriteLine("raw_level=rejected id=" + rawLevelWrite
                    + " level=" + rawLevelValue + " sqlstate=" + ex.SqlState);
            }
        }

        if (rawRealmId != 0)
        {
            await characters.RawWriteRealmAsync(rawRealmId, rawRealmValue!, CancellationToken.None);
            Console.WriteLine("raw_realm=written id=" + rawRealmId + " realm=" + rawRealmValue);
        }

        if (realmStateOf != 0)
        {
            var realm = await characters.RealmOfAsync(realmStateOf, CancellationToken.None);
            if (realm is null)
            {
                Console.WriteLine("realm_state=missing id=" + realmStateOf);
            }
            else
            {
                var realmConfig = CharacterConfig.Load(configPath);
                var realmDungeons = DungeonTable.Load(realmConfig.DungeonTable);
                var realmCurrencies = CurrencyTable.Load("config/currency.json");
                var table = RealmTable.Load(realmConfig.RealmTable, realmDungeons, realmCurrencies);
                var advance = table.CheckAdvance(realm);
                Console.WriteLine("realm_state=ok id=" + realmStateOf
                    + " realm=" + advance.From
                    + " rank=" + table.Rank(realm)
                    + " next=" + (advance.Next.Length == 0 ? "none" : advance.Next)
                    + " next_open=" + (advance.NextOpen ? "true" : "false")
                    + " advance=" + (advance.Ok ? "ok" : advance.Reason));
            }
        }

        // C1：主线十二场的核对入口。只给核对用；玩法路径走网关与 gRPC。
        if (mainlineStateOf != 0 || mainlineTurnInOf != 0 || mainlineBreakthroughOf != 0)
        {
            var mlConfig = CharacterConfig.Load(configPath);
            var mlCurrencies = CurrencyTable.Load("config/currency.json");
            var mlDungeons = DungeonTable.Load(mlConfig.DungeonTable);
            var mlRealms = RealmTable.Load(mlConfig.RealmTable, mlDungeons, mlCurrencies);
            var mlTable = File.Exists(mlConfig.MainlineTable)
                ? MainlineTable.Load(mlConfig.MainlineTable, mlDungeons, mlRealms)
                : MainlineTable.Missing();
            var mlItems = ItemTable.Load("config/items.json");
            var mlDropPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(configPath))!, "mainline_drops.json"));
            var mlDropTable = MainlineDropTable.LoadIfPresent(mlDropPath, mlItems, mlCurrencies);
            var mlGrants = new GrantRepository(dataSource, mlItems, mlCurrencies, mlConfig);
            var mlRepo = new MainlineRepository(
                dataSource, mlTable, mlDropTable, mlGrants, mlItems, mlConfig);

            if (mainlineSeedSettlementOf != 0)
            {
                var seedTxn = (mainlineSeedSettlementTxn ?? "").Trim();
                await using var seed = dataSource.CreateCommand(
                    "insert into \"角色\".exp_settlement "
                    + "(txn_id, character_id, source, party_size, base_exp, party_bonus, "
                    + "same_sect_bonus, exp_gain, level_before, level_after) "
                    + "values ($1, $2, 'dungeon', 1, 0, 0, 0, 0, 1, 1) "
                    + "on conflict (txn_id) do nothing");
                seed.Parameters.AddWithValue(seedTxn);
                seed.Parameters.AddWithValue(mainlineSeedSettlementOf);
                var written = await seed.ExecuteNonQueryAsync(CancellationToken.None);
                Console.WriteLine("mainline_seed_settlement=written id=" + mainlineSeedSettlementOf
                    + " txn=" + seedTxn + " rows=" + written);
            }

            if (mainlineBreakthroughOf != 0)
            {
                // 注入一条「某段已突破成功」的事实，只用来验第 10/11/12 场的门槛分支。
                // 正常路径由 P3-1a2 的 AdvanceRealm 写，版本开关与灵石消耗都归它管。
                await using var seed = dataSource.CreateCommand(
                    "insert into \"角色\".realm_breakthrough "
                    + "(txn_id, character_id, from_realm, to_realm, success, drill_cost, stone_kind, stone_cost) "
                    + "values ($1, $2, $3, $4, true, 0, 'stone_1', 1) "
                    + "on conflict (txn_id) do nothing");
                seed.Parameters.AddWithValue("check:break:" + mainlineBreakthroughOf + ":" + mainlineFrom);
                seed.Parameters.AddWithValue(mainlineBreakthroughOf);
                seed.Parameters.AddWithValue(mainlineFrom ?? "");
                seed.Parameters.AddWithValue(mlRealms.Next(mainlineFrom) ?? "");
                var written = await seed.ExecuteNonQueryAsync(CancellationToken.None);
                Console.WriteLine("mainline_breakthrough=written id=" + mainlineBreakthroughOf
                    + " from=" + mainlineFrom + " rows=" + written);
            }

            if (mainlineTurnInOf != 0)
            {
                var result = await mlRepo.TurnInAsync(
                    mainlineTurnInOf, null, mainlineTxn ?? "", mainlineStage,
                    mainlineAlignment ?? "", CancellationToken.None);
                Console.WriteLine("mainline_turn_in=ok=" + (result.Ok ? "true" : "false")
                    + " reason=" + result.Reason
                    + " duplicate=" + (result.Duplicate ? "true" : "false")
                    + " stage=" + result.Stage
                    + " main_stage=" + result.MainStage
                    + " alignment=" + result.Alignment
                    + " drop_lines=" + result.DropLines
                    + " txn=" + (mainlineTxn ?? ""));
            }

            if (mainlineStateOf != 0)
            {
                var state = await mlRepo.StateAsync(mainlineStateOf, null, CancellationToken.None);
                if (!state.Found)
                {
                    Console.WriteLine("mainline_state=missing id=" + mainlineStateOf);
                }
                else
                {
                    Console.WriteLine("mainline_state=ok id=" + mainlineStateOf
                        + " main_stage=" + state.MainStage
                        + " alignment=" + state.Alignment
                        + " level=" + state.Level
                        + " realm=" + state.Realm);
                }
            }
        }

        // P3-1a2：突破与历练结算的核对入口。只给核对用；玩法路径走 gRPC。
        if (rawDrillId != 0 || rawMainStageId != 0 || completeQuestOf != 0
            || realmBreakStateOf != 0 || advanceRealmOf != 0 || settleDrillOf != 0)
        {
            var realmConfig = CharacterConfig.Load(configPath);
            var realmDungeons = DungeonTable.Load(realmConfig.DungeonTable);
            var realmCurrencies = CurrencyTable.Load("config/currency.json");
            var realmTable = RealmTable.Load(realmConfig.RealmTable, realmDungeons, realmCurrencies);
            var realmAdvances = new RealmAdvanceRepository(dataSource, realmTable);

            if (rawDrillId != 0)
            {
                try
                {
                    await realmAdvances.RawWriteDrillAsync(rawDrillId, rawDrillValue, CancellationToken.None);
                    Console.WriteLine("raw_drill=written id=" + rawDrillId + " drill=" + rawDrillValue);
                }
                catch (PostgresException ex)
                {
                    Console.WriteLine("raw_drill=rejected id=" + rawDrillId
                        + " drill=" + rawDrillValue + " sqlstate=" + ex.SqlState);
                }
            }

            if (rawMainStageId != 0)
            {
                await realmAdvances.RawWriteMainStageAsync(
                    rawMainStageId, rawMainStageValue, CancellationToken.None);
                Console.WriteLine("raw_main_stage=written id=" + rawMainStageId
                    + " stage=" + rawMainStageValue);
            }

            if (completeQuestOf != 0)
            {
                await realmAdvances.CompleteQuestAsync(
                    completeQuestOf, completeQuestId!, CancellationToken.None);
                Console.WriteLine("quest_complete=ok id=" + completeQuestOf
                    + " quest=" + completeQuestId);
            }

            if (realmBreakStateOf != 0)
            {
                var state = await realmAdvances.StateAsync(realmBreakStateOf, CancellationToken.None);
                if (state is null)
                {
                    Console.WriteLine("realm_break_state=missing id=" + realmBreakStateOf);
                }
                else
                {
                    Console.WriteLine("realm_break_state=ok id=" + realmBreakStateOf
                        + " realm=" + state.Value.Realm
                        + " drill=" + state.Value.Drill
                        + " main_stage=" + state.Value.MainStage
                        + " quests=" + string.Join(",", state.Value.Quests));
                }
            }

            if (advanceRealmOf != 0)
            {
                var outcome = await realmAdvances.AdvanceAsync(
                    advanceRealmOf, null, advanceRealmTxn!, CancellationToken.None);
                Console.WriteLine("advance_realm=done id=" + advanceRealmOf
                    + " executed=" + (outcome.Executed ? "true" : "false")
                    + " duplicate=" + (outcome.Duplicate ? "true" : "false")
                    + " success=" + (outcome.Success ? "true" : "false")
                    + " from=" + outcome.From
                    + " next=" + outcome.Next
                    + " realm=" + outcome.Realm
                    + " reason=" + outcome.Reason
                    + " drill=" + outcome.DrillBefore
                    + " drill_required=" + outcome.DrillRequired
                    + " drill_cost=" + outcome.DrillCost
                    + " drill_after=" + outcome.DrillAfter
                    + " stone=" + outcome.StoneKind
                    + " stone_cost=" + outcome.StoneCost
                    + " stone_left=" + outcome.StoneLeft
                    + " main_stage=" + outcome.MainStage
                    + " main_required=" + outcome.MainStageRequired
                    + " missing=" + string.Join("+", outcome.MissingQuests)
                    + " success_pct=" + outcome.SuccessPct);
            }

            if (settleDrillOf != 0)
            {
                var dungeon = realmDungeons.Find(settleDrillDungeon!);
                if (dungeon is null)
                {
                    Console.WriteLine("settle_drill=unknown_dungeon id=" + settleDrillOf
                        + " dungeon=" + settleDrillDungeon);
                }
                else
                {
                    var outcome = await realmAdvances.SettleDrillAsync(
                        settleDrillOf, null, dungeon.Id, dungeon.Realm, dungeon.Drill,
                        settleDrillTxn!, CancellationToken.None);
                    Console.WriteLine("settle_drill=done id=" + settleDrillOf
                        + " dungeon=" + outcome.DungeonId
                        + " gain=" + outcome.Gain
                        + " drill=" + outcome.Drill
                        + " duplicate=" + (outcome.Duplicate ? "true" : "false")
                        + " reason=" + outcome.Reason);
                }
            }
        }

        if (shopStateOf != 0)
        {
            var shopMoney = new CurrencyRepository(dataSource);
            var shopBalances = await shopMoney.AllAsync(shopStateOf, CancellationToken.None);
            var shopCurrencies = CurrencyTable.Load("config/currency.json");
            var shopItemsForState = ItemTable.Load("config/items.json");
            var shopCharConfig = CharacterConfig.Load(configPath);
            var shopEnhanceForState = EnhanceTable.Load(
                shopCharConfig.EnhanceTable, shopItemsForState, shopCurrencies);
            var shopTalismansForState = TalismanTable.Load(
                shopCharConfig.TalismanTable, shopItemsForState);
            // 与启动、--shop-list 走同一套 Load：卷轴发货物必须就是那一个编号。
            var shopScrollsForState = ReviveScrollTable.Load(
                shopCharConfig.ReviveScrollTable, "config/classes.json",
                shopItemsForState, shopTalismansForState);
            // 与启动、--shop-list 走同一套 Load：经验道具发货物必须就是那一个编号。
            var shopBoostersForState = ExpBoosterTable.Load(
                shopCharConfig.ExpBoosterTable, shopItemsForState,
                shopTalismansForState, shopScrollsForState);
            // 与启动、--shop-list 走同一套 Load：解绑道具发货物必须就是那一个编号。
            var shopUnbindsForState = UnbindTable.Load(
                shopCharConfig.UnbindTable, shopItemsForState,
                shopTalismansForState, shopScrollsForState, shopBoostersForState);
            var shopTableForState = ShopTable.Load(
                shopCharConfig.ShopTable, shopCurrencies, shopItemsForState, shopEnhanceForState,
                shopTalismansForState, shopScrollsForState, shopBoostersForState,
                shopUnbindsForState);

            // 券的张数与绑定态都从背包里读出来，不另算。绑定的券不能上架（P1-5a 的 bound_item）。
            var shopInventory = new InventoryRepository(dataSource);
            var shopSlots = await shopInventory.ListAsync(shopStateOf, CancellationToken.None);
            var ticketItem = shopTableForState.Items
                .FirstOrDefault(i => string.Equals(i.Kind, "protect_ticket", StringComparison.Ordinal));
            var ticketSlots = shopSlots
                .Where(s => ticketItem is not null
                    && string.Equals(s.ItemId, ticketItem.DeliveryItemId, StringComparison.Ordinal))
                .ToList();
            var ticketCount = ticketSlots.Sum(s => s.Count);
            var ticketAllBound = ticketSlots.Count > 0 && ticketSlots.All(s => s.Bound);
            var ticketSlotList = ticketSlots.Count == 0
                ? "-"
                : string.Join(",", ticketSlots.Select(s => s.Slot + ":" + s.Count));

            Console.WriteLine("shop_state=ok id=" + shopStateOf
                + " currency=" + ShopTable.WrittenCurrency
                + " balance=" + shopBalances.GetValueOrDefault(ShopTable.WrittenCurrency, 0L)
                + " balances=" + string.Join(",", shopCurrencies.All.Select(
                    c => c.Id + ":" + shopBalances.GetValueOrDefault(c.Id, 0L)))
                + " goods=" + (ticketItem?.DeliveryItemId ?? "-")
                + " tickets=" + ticketCount
                + " ticket_bound=" + (ticketAllBound ? "true" : "false")
                + " ticket_slots=" + ticketSlotList
                + " used_slots=" + shopSlots.Count);
        }

        if (shopLedgerOf != 0)
        {
            // 商城买过几笔：看事务表里这个角色名下、前缀是 shop_buy: 的行。
            var ledger = new GrantRepository(
                dataSource,
                ItemTable.Load("config/items.json"),
                CurrencyTable.Load("config/currency.json"),
                CharacterConfig.Load(configPath));
            var rows = await ledger.ShopTxnCountAsync(shopLedgerOf, CancellationToken.None);
            Console.WriteLine("shop_ledger=ok id=" + shopLedgerOf
                + " txn_prefix=shop_buy:"
                + " orders=" + rows);
        }

        if (talismanStateOf != 0)
        {
            // 红符的张数与绑定态都从背包里读出来，不另算；回复值与上限读配置。
            // 张数是事实值——用掉一张之后这里少一张，就是这一卡的落点。
            var talismanCharConfig = CharacterConfig.Load(configPath);
            var talismanItemsForState = ItemTable.Load("config/items.json");
            var talismanTableForState = TalismanTable.Load(
                talismanCharConfig.TalismanTable, talismanItemsForState);
            var redForState = talismanTableForState.RedTalisman;
            var blueForState = talismanTableForState.BlueTalisman;
            var talismanInventory = new InventoryRepository(dataSource);
            var talismanSlots = await talismanInventory.ListAsync(
                talismanStateOf, CancellationToken.None);
            var redSlots = redForState is null
                ? new List<BagSlot>()
                : talismanSlots
                    .Where(s => string.Equals(s.ItemId, redForState.ItemId, StringComparison.Ordinal))
                    .ToList();
            var redCount = redSlots.Sum(s => s.Count);
            var redAllBound = redSlots.Count > 0 && redSlots.All(s => s.Bound);
            var blueSlots = blueForState is null
                ? new List<BagSlot>()
                : talismanSlots
                    .Where(s => string.Equals(s.ItemId, blueForState.ItemId, StringComparison.Ordinal))
                    .ToList();
            var blueCount = blueSlots.Sum(s => s.Count);
            var blueAllBound = blueSlots.Count > 0 && blueSlots.All(s => s.Bound);

            Console.WriteLine("talisman_state=ok id=" + talismanStateOf
                + " red_item=" + (redForState?.ItemId ?? "-")
                + " count=" + redCount
                + " bound=" + (redAllBound ? "true" : "false")
                + " slots=" + (redSlots.Count == 0
                    ? "-"
                    : string.Join(",", redSlots.Select(s => s.Slot + ":" + s.Count)))
                + " hp_restore=" + (redForState?.HpRestore ?? 0)
                + " potion_hp_restore=" + (redForState?.PotionHpRestore ?? 0)
                + " cooldown_group=" + (redForState?.CooldownGroup ?? "-")
                + " cooldown_ms=" + (redForState?.CooldownMs ?? 0)
                + " blue_item=" + (blueForState?.ItemId ?? "-")
                + " blue_count=" + blueCount
                + " blue_bound=" + (blueAllBound ? "true" : "false")
                + " blue_slots=" + (blueSlots.Count == 0
                    ? "-"
                    : string.Join(",", blueSlots.Select(s => s.Slot + ":" + s.Count)))
                + " mana_restore=" + (blueForState?.ManaRestore ?? 0)
                + " potion_mana_restore=" + (blueForState?.PotionManaRestore ?? 0)
                + " blue_group=" + (blueForState?.CooldownGroup ?? "-")
                + " blue_cooldown_ms=" + (blueForState?.CooldownMs ?? 0));
        }

        if (scrollStateOf != 0)
        {
            // P1-5c6：从库里独立读回冷却到期、当前级经验、等级与卷轴张数。
            // 冷却到期是角色库那一行，不在任何进程内存里；换一场后读到的还是它。
            var scrollConfigForState = CharacterConfig.Load(configPath);
            var scrollItemsForState = ItemTable.Load("config/items.json");
            var scrollTalismansForState = TalismanTable.Load(
                scrollConfigForState.TalismanTable, scrollItemsForState);
            var scrollTableForState = ReviveScrollTable.Load(
                scrollConfigForState.ReviveScrollTable, "config/classes.json",
                scrollItemsForState, scrollTalismansForState);
            var scrollRepo = new ReviveScrollRepository(dataSource);
            var scrollState = await scrollRepo.StateAsync(
                scrollStateOf, scrollTableForState.ReviveScroll.ItemId, CancellationToken.None);
            Console.WriteLine("revive_scroll_state=ok id=" + scrollStateOf
                + " found=" + (scrollState.Found ? "true" : "false")
                + " item=" + scrollTableForState.ReviveScroll.ItemId
                + " count=" + scrollState.Count
                + " bound=" + (scrollState.AllBound ? "true" : "false")
                + " level=" + scrollState.Level
                + " exp=" + scrollState.Exp
                + " cooldown_ms=" + scrollTableForState.ReviveScroll.CooldownMs
                + " hp_restore_pct=" + scrollTableForState.HpRestorePct
                + " exp_cost_pct=" + scrollTableForState.ExpCostPct
                + " ready_at_ms=" + scrollState.ReadyAtMs
                + " now_ms=" + scrollState.NowMs
                + " cooling=" + (scrollState.Cooling ? "true" : "false"));
        }

        if (boosterStateOf != 0)
        {
            // P1-5c7：从库里独立读回到期时刻、剩余时间、绑定态与背包张数。
            // 到期时刻是角色库那一行，不在任何进程内存里；换进程后读到的还是它。
            var boosterConfigForState = CharacterConfig.Load(configPath);
            var boosterItemsForState = ItemTable.Load("config/items.json");
            var boosterTalismansForState = TalismanTable.Load(
                boosterConfigForState.TalismanTable, boosterItemsForState);
            var boosterScrollsForState = ReviveScrollTable.Load(
                boosterConfigForState.ReviveScrollTable, "config/classes.json",
                boosterItemsForState, boosterTalismansForState);
            var boosterTableForState = ExpBoosterTable.Load(
                boosterConfigForState.ExpBoosterTable, boosterItemsForState,
                boosterTalismansForState, boosterScrollsForState);
            var boosterRepo = new ExpBoosterRepository(dataSource);
            var boosterState = await boosterRepo.StateAsync(
                boosterStateOf, boosterTableForState.ExpBooster.ItemId, CancellationToken.None);
            // 「同时只生效一个」要能从库里数出来：用了两张之后仍只有一行。
            long boosterRows = 0;
            await using (var rowCount = dataSource.CreateCommand(
                "select count(*) from \"角色\".exp_booster where character_id = $1"))
            {
                rowCount.Parameters.AddWithValue(boosterStateOf);
                boosterRows = Convert.ToInt64(
                    await rowCount.ExecuteScalarAsync(CancellationToken.None));
            }
            Console.WriteLine("exp_booster_state=ok id=" + boosterStateOf
                + " rows=" + boosterRows
                + " found=" + (boosterState.Found ? "true" : "false")
                + " item=" + boosterTableForState.ExpBooster.ItemId
                + " count=" + boosterState.Count
                + " bound=" + (boosterState.AllBound ? "true" : "false")
                + " duration_ms=" + boosterTableForState.ExpBooster.DurationMs
                + " gain_pct=" + boosterTableForState.ExpBooster.GainPct
                + " expires_at_ms=" + boosterState.ExpiresAtMs
                + " used_at_ms=" + boosterState.UsedAtMs
                + " now_ms=" + boosterState.NowMs
                + " remaining_ms=" + boosterState.RemainingMs
                + " active=" + (boosterState.Active ? "true" : "false"));
        }

        if (rawBoundAtOf != 0)
        {
            // 只给核对用：把 bound_at 挪到 N 小时前（并保证 bound=true），验年龄档位。
            // 只动这两列，不动数量与物品编号；玩法代码不调用这个入口。
            await using var write = dataSource.CreateCommand(
                "update \"角色\".inventory set bound = true, "
                + "bound_at = now() - ($3::double precision * interval '1 hour') "
                + "where character_id = $1 and slot = $2");
            write.Parameters.AddWithValue(rawBoundAtOf);
            write.Parameters.AddWithValue((short)rawBoundAtSlot);
            write.Parameters.AddWithValue(rawBoundAtHours);
            var rows = await write.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("raw_bound_at=written id=" + rawBoundAtOf
                + " slot=" + rawBoundAtSlot + " hours=" + rawBoundAtHours + " rows=" + rows);
        }

        if (unbindQuoteOf != 0 || unbindStateOf != 0)
        {
            var unbindConfigForRead = CharacterConfig.Load(configPath);
            var unbindItemsForRead = ItemTable.Load("config/items.json");
            var unbindTalismansForRead = TalismanTable.Load(
                unbindConfigForRead.TalismanTable, unbindItemsForRead);
            var unbindScrollsForRead = ReviveScrollTable.Load(
                unbindConfigForRead.ReviveScrollTable, "config/classes.json",
                unbindItemsForRead, unbindTalismansForRead);
            var unbindBoostersForRead = ExpBoosterTable.Load(
                unbindConfigForRead.ExpBoosterTable, unbindItemsForRead,
                unbindTalismansForRead, unbindScrollsForRead);
            var unbindTableForRead = UnbindTable.Load(
                unbindConfigForRead.UnbindTable, unbindItemsForRead,
                unbindTalismansForRead, unbindScrollsForRead, unbindBoostersForRead);
            var unbindRepoForRead = new UnbindRepository(
                dataSource, unbindItemsForRead, unbindTableForRead);

            if (unbindQuoteOf != 0)
            {
                // 只读报价：这一格是什么、绑了多久、按配置要几张、背包里现有几张。
                var quote = await unbindRepoForRead.QuoteAsync(
                    unbindQuoteOf, unbindQuoteSlot, unbindTableForRead.Unbind.ItemId,
                    CancellationToken.None);
                Console.WriteLine("unbind_quote=ok id=" + unbindQuoteOf
                    + " slot=" + unbindQuoteSlot
                    + " target=" + quote.TargetItemId
                    + " bound=" + (quote.Bound ? "true" : "false")
                    + " age_hours=" + quote.AgeHours
                    + " cost=" + quote.Cost
                    + " floor=" + unbindTableForRead.FloorCost
                    + " floor_hit=" + (quote.FloorHit ? "true" : "false")
                    + " count=" + quote.UnbindCount
                    + " reason=" + quote.Reason);
            }

            if (unbindStateOf != 0)
            {
                // 从库里独立读回这一格的绑定态、绑定时长与解绑道具张数。
                var state = await unbindRepoForRead.QuoteAsync(
                    unbindStateOf, unbindStateSlot, unbindTableForRead.Unbind.ItemId,
                    CancellationToken.None);
                Console.WriteLine("unbind_state=ok id=" + unbindStateOf
                    + " slot=" + unbindStateSlot
                    + " target=" + state.TargetItemId
                    + " bound=" + (state.Bound ? "true" : "false")
                    + " age_hours=" + state.AgeHours
                    + " count=" + state.UnbindCount
                    + " item=" + unbindTableForRead.Unbind.ItemId
                    + " floor=" + unbindTableForRead.FloorCost);
            }
        }

        if (rechargeStateOf != null)
        {
            // 从库里独立读回这张订单的状态、金额、发放时刻与余额。
            var rechargeRepo = new RechargeRepository(dataSource);
            var order = await rechargeRepo.StateAsync(rechargeStateOf, CancellationToken.None);
            Console.WriteLine(order.Found
                ? "recharge_state=ok order=" + order.OrderId
                  + " id=" + order.CharacterId
                  + " product=" + order.ProductId
                  + " yuanbao=" + order.Yuanbao
                  + " status=" + order.Status
                  + " paid=" + (order.Paid ? "true" : "false")
                  + " paid_at_ms=" + order.PaidAtMs
                  + " now_ms=" + order.NowMs
                  + " balance=" + order.Balance
                : "recharge_state=missing order=" + rechargeStateOf);
        }

        if (branchStateOf != 0)
        {
            var state = await characters.BranchStateAsync(branchStateOf, CancellationToken.None);
            Console.WriteLine(state is null
                ? "branch_state=missing id=" + branchStateOf
                : "branch_state=ok id=" + branchStateOf + " class=" + state.Value.ClassId
                  + " branch=" + state.Value.Branch + " branch_step=" + state.Value.Step
                  + " level=" + state.Value.Level);
        }

        if (advanceBranchId != 0)
        {
            var branchTable = BranchTable.Load("config/branches.json");
            var identity = await characters.BattleIdentityAsync(advanceBranchId, CancellationToken.None);
            if (identity is null)
            {
                Console.WriteLine("advance_branch=character_not_found id=" + advanceBranchId);
            }
            else if (!branchTable.BelongsTo(advanceBranchTo!, identity.Value.ClassId))
            {
                Console.WriteLine("advance_branch=reason=branch_not_found id=" + advanceBranchId
                    + " branch=" + advanceBranchTo);
            }
            else
            {
                var outcome = await characters.AdvanceBranchAsync(
                    advanceBranchId, advanceBranchTo!, CancellationToken.None);
                Console.WriteLine("advance_branch=ok id=" + advanceBranchId
                    + " reason=" + (outcome.Reason.Length == 0 ? "none" : outcome.Reason)
                    + " branch=" + outcome.Branch + " branch_step=" + outcome.Step
                    + " level=" + outcome.Level
                    + " duplicate=" + (outcome.Duplicate ? "true" : "false"));
            }
        }

        if (canEquipOf != 0)
        {
            var itemTable = ItemTable.Load("config/items.json");
            var branchTable = BranchTable.Load("config/branches.json");
            var identity = await characters.BattleIdentityAsync(canEquipOf, CancellationToken.None);
            if (identity is null)
            {
                Console.WriteLine("can_equip=ok=false reason=character_missing id=" + canEquipOf);
            }
            else
            {
                var item = itemTable.Find(canEquipItem!);
                if (item is null)
                {
                    Console.WriteLine("can_equip=ok=false reason=item_not_found id=" + canEquipOf
                        + " item=" + canEquipItem);
                }
                else
                {
                    var reason = EquipRules.Check(item, identity.Value.Level, identity.Value.ClassId, branchTable);
                    Console.WriteLine("can_equip=ok=" + (reason.Length == 0 ? "true" : "false")
                        + " reason=" + (reason.Length == 0 ? "none" : reason)
                        + " id=" + canEquipOf + " item=" + item.Id
                        + " weapon_kind=" + item.WeaponKind + " require_level=" + item.RequireLevel
                        + " level=" + identity.Value.Level + " class=" + identity.Value.ClassId);
                }
            }
        }

        if (levelOf != 0)
        {
            var found = await characters.LevelOfAsync(levelOf, CancellationToken.None);
            Console.WriteLine(found is null
                ? "level=missing id=" + levelOf
                : "level=ok id=" + levelOf + " account=" + found.Value.Account + " level=" + found.Value.Level);
        }

        if (bindingOf != null)
        {
            var inventory = new InventoryRepository(dataSource);
            var found = await inventory.BindingOfAsync(bindingOf, bindingSlot, CancellationToken.None);
            Console.WriteLine(found is null
                ? "binding=missing name=" + bindingOf + " slot=" + bindingSlot
                : "binding=ok name=" + bindingOf + " slot=" + bindingSlot
                  + " bound=" + found.Value.Bound + " bound_at_present=" + found.Value.BoundAtPresent);
        }

        if (rawCurrencyId != 0 && rawCurrencyKind != null)
        {
            var money = new CurrencyRepository(dataSource);
            try
            {
                await money.RawWriteAsync(rawCurrencyId, rawCurrencyKind, rawCurrencyAmount, CancellationToken.None);
                Console.WriteLine("raw_currency=written id=" + rawCurrencyId
                    + " kind=" + rawCurrencyKind + " amount=" + rawCurrencyAmount);
            }
            catch (PostgresException ex)
            {
                // 核对要看到的就是这个：库层的 CHECK 把负数挡住了。
                Console.WriteLine("raw_currency=rejected id=" + rawCurrencyId
                    + " kind=" + rawCurrencyKind + " amount=" + rawCurrencyAmount
                    + " sqlstate=" + ex.SqlState);
            }
        }

        if (txnCount != null)
        {
            // 只数行，不经过发奖逻辑，免得核对自己去抢号。
            await using var command = dataSource.CreateCommand(
                "select count(*) from \"角色\".grant_txn where txn_id = $1");
            command.Parameters.AddWithValue(txnCount);
            var value = await command.ExecuteScalarAsync(CancellationToken.None);
            Console.WriteLine("txn_count=" + Convert.ToInt32(value) + " txn=" + txnCount);
        }

        if (itemCountOf != 0 && itemCountItem != null)
        {
            await using var command = dataSource.CreateCommand(
                "select coalesce(sum(count), 0) from \"角色\".inventory "
                + "where character_id = $1 and item_id = $2");
            command.Parameters.AddWithValue(itemCountOf);
            command.Parameters.AddWithValue(itemCountItem);
            var value = await command.ExecuteScalarAsync(CancellationToken.None);
            Console.WriteLine("item_count=" + Convert.ToInt64(value)
                + " id=" + itemCountOf + " item=" + itemCountItem);
        }

        if (reservesOf != 0)
        {
            var found = await characters.ReservesAsync(reservesOf, CancellationToken.None);
            Console.WriteLine(found is null
                ? "reserves=missing id=" + reservesOf
                : "reserves=ok id=" + reservesOf
                  + " fatigue=" + found.Value.Fatigue
                  + " weekly_share=" + found.Value.WeeklyShare);
        }

        if (entryCountOf != 0)
        {
            await using var command = dataSource.CreateCommand(
                "select count(*) from \"角色\".dungeon_entry where character_id = $1");
            command.Parameters.AddWithValue(entryCountOf);
            var value = await command.ExecuteScalarAsync(CancellationToken.None);
            Console.WriteLine("entry_count=" + Convert.ToInt32(value) + " id=" + entryCountOf);
        }

        if (hardEntryStateOf != 0 || shiftEntryWeekOf != 0)
        {
            // P2-2a：高难次数只从「角色」.dungeon_entry 数，不经过进本逻辑。
            var hardEntries = new HardEntryRepository(dataSource);

            if (hardEntryStateOf != 0 && hardEntryDungeon != null)
            {
                // 上限从同一个副本表读，和跑着的角色服看到的是同一份。
                var hardDungeon = DungeonTable.Load(
                    CharacterConfig.Load(configPath).DungeonTable).Find(hardEntryDungeon);
                var cap = hardDungeon?.EntriesPerWeek ?? 0;
                var state = await hardEntries.HardEntryStateAsync(
                    hardEntryStateOf, hardEntryDungeon, cap, CancellationToken.None);
                Console.WriteLine(state is null
                    ? "hard_entry_state=missing id=" + hardEntryStateOf
                      + " dungeon=" + hardEntryDungeon
                    : "hard_entry_state=ok id=" + hardEntryStateOf
                      + " dungeon=" + hardEntryDungeon
                      + " used=" + state.Value.Used
                      + " cap=" + state.Value.Cap
                      + " remaining=" + state.Value.Remaining);
            }

            if (shiftEntryWeekOf != 0 && shiftEntryWeekDungeon != null)
            {
                // 只给核对用：把进本记录挪到上一周，验新的一周从 0 起算。
                await hardEntries.ShiftEntryWeekAsync(
                    shiftEntryWeekOf, shiftEntryWeekDungeon, CancellationToken.None);
                Console.WriteLine("shift_entry_week=ok id=" + shiftEntryWeekOf
                    + " dungeon=" + shiftEntryWeekDungeon);
            }
        }

        if (fatigueStateOf != 0 || setFatigueDay != null || rawFatigueId != 0)
        {
            var dungeonEntries = new DungeonEntryRepository(dataSource);

            if (fatigueStateOf != 0)
            {
                var state = await dungeonEntries.FatigueStateAsync(fatigueStateOf, CancellationToken.None);
                Console.WriteLine(state is null
                    ? "fatigue_state=missing id=" + fatigueStateOf
                    : "fatigue_state=ok id=" + fatigueStateOf
                      + " fatigue=" + state.Value.Fatigue
                      + " weekly_share=" + state.Value.WeeklyShare
                      + " day=" + state.Value.Day.ToString("yyyy-MM-dd"));
            }

            if (setFatigueDay != null && setFatigueDayId != 0
                && DateOnly.TryParse(setFatigueDay, out var day))
            {
                await dungeonEntries.SetFatigueDayAsync(setFatigueDayId, day, CancellationToken.None);
                Console.WriteLine("fatigue_day=set id=" + setFatigueDayId + " day=" + day.ToString("yyyy-MM-dd"));
            }

            if (rawFatigueId != 0)
            {
                try
                {
                    await dungeonEntries.RawWriteFatigueAsync(
                        rawFatigueId, rawFatigueValue, CancellationToken.None);
                    Console.WriteLine("raw_fatigue=written id=" + rawFatigueId
                        + " fatigue=" + rawFatigueValue);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层的 CHECK 把超过日上限的值挡住了。
                    Console.WriteLine("raw_fatigue=rejected id=" + rawFatigueId
                        + " fatigue=" + rawFatigueValue + " sqlstate=" + ex.SqlState);
                }
            }
        }

        if (shareStateOf != 0 || setWeekDay != null || rawShareId != 0 || sharesTotalDungeon != null)
        {
            var weeklyShares = new WeeklyShareRepository(dataSource);

            if (shareStateOf != 0)
            {
                // 上限从同一个副本表读，和跑着的角色服看到的是同一份。
                var weeklyCap = DungeonTable.Load(CharacterConfig.Load(configPath).DungeonTable).WeeklyShareCap;
                var state = await weeklyShares.ShareStateAsync(shareStateOf, weeklyCap, CancellationToken.None);
                Console.WriteLine(state is null
                    ? "share_state=missing id=" + shareStateOf
                    : "share_state=ok id=" + shareStateOf
                      + " used=" + state.Value.Used
                      + " remaining=" + state.Value.Remaining
                      + " fatigue=" + state.Value.Fatigue
                      + " week=" + state.Value.Week.ToString("yyyy-MM-dd"));
            }

            if (setWeekDay != null && setWeekDayId != 0
                && DateOnly.TryParse(setWeekDay, out var week))
            {
                await weeklyShares.SetWeeklyShareDayAsync(setWeekDayId, week, CancellationToken.None);
                Console.WriteLine("week_day=set id=" + setWeekDayId + " day=" + week.ToString("yyyy-MM-dd"));
            }

            if (rawShareId != 0)
            {
                try
                {
                    await weeklyShares.RawWriteShareAsync(rawShareId, rawShareValue, CancellationToken.None);
                    Console.WriteLine("raw_share=written id=" + rawShareId + " used=" + rawShareValue);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层 CHECK 把负份额挡住了。
                    Console.WriteLine("raw_share=rejected id=" + rawShareId
                        + " used=" + rawShareValue + " sqlstate=" + ex.SqlState);
                }
            }

            if (sharesTotalDungeon != null && sharesTotalOf != 0)
            {
                var total = await weeklyShares.SharesTotalAsync(
                    sharesTotalOf, sharesTotalDungeon, CancellationToken.None);
                Console.WriteLine("shares_total=" + total + " id=" + sharesTotalOf
                    + " dungeon=" + sharesTotalDungeon);
            }
        }

        if (expStateOf != 0 || rawExpId != 0)
        {
            var expSettlements = new ExpSettlementRepository(dataSource);

            if (expStateOf != 0)
            {
                var state = await expSettlements.ExpStateAsync(expStateOf, CancellationToken.None);
                Console.WriteLine(state is null
                    ? "exp_state=missing id=" + expStateOf
                    : "exp_state=ok id=" + expStateOf
                      + " level=" + state.Value.Level
                      + " exp=" + state.Value.Exp
                      + " rows=" + state.Value.Rows
                      + " total_gain=" + state.Value.TotalGain);
            }

            if (rawExpId != 0)
            {
                try
                {
                    await expSettlements.RawWriteExpAsync(rawExpId, rawExpValue, CancellationToken.None);
                    Console.WriteLine("raw_exp=written id=" + rawExpId + " exp=" + rawExpValue);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层 CHECK 把负经验挡住了。
                    Console.WriteLine("raw_exp=rejected id=" + rawExpId
                        + " exp=" + rawExpValue + " sqlstate=" + ex.SqlState);
                }
            }
        }

        if (enhanceLevelOf != 0 || enhanceTxnCount != null || couponCountOf != 0
            || rawEnhanceId != 0)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var enhanceTable = EnhanceTable.Load(
                enhanceConfigPath ?? characterConfig.EnhanceTable,
                items, CurrencyTable.Load("config/currency.json"));
            var enhances = new EnhanceRepository(dataSource, enhanceTable, items);

            if (rawEnhanceId != 0)
            {
                try
                {
                    await enhances.RawWriteLevelAsync(
                        rawEnhanceId, rawEnhanceSlot, rawEnhanceLevel, CancellationToken.None);
                    Console.WriteLine("raw_enhance=written id=" + rawEnhanceId
                        + " slot=" + rawEnhanceSlot + " level=" + rawEnhanceLevel);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层 CHECK 把越界的强化等级挡住了。
                    Console.WriteLine("raw_enhance=rejected id=" + rawEnhanceId
                        + " level=" + rawEnhanceLevel + " sqlstate=" + ex.SqlState);
                }
            }

            if (enhanceLevelOf != 0)
            {
                var state = await enhances.StateAsync(enhanceLevelOf, enhanceSlotOf, CancellationToken.None);
                Console.WriteLine("enhance_state=ok id=" + enhanceLevelOf
                    + " slot=" + enhanceSlotOf
                    + " level=" + state.Level
                    + " gold=" + state.Gold
                    + " low=" + state.Low
                    + " mid=" + state.Mid
                    + " high=" + state.High);
            }

            if (enhanceTxnCount != null)
            {
                Console.WriteLine("enhance_txn_count="
                    + await enhances.TxnCountAsync(enhanceTxnCount, CancellationToken.None)
                    + " txn=" + enhanceTxnCount);
            }

            if (couponCountOf != 0)
            {
                Console.WriteLine("coupon_count="
                    + await enhances.CouponCountAsync(couponCountOf, CancellationToken.None)
                    + " id=" + couponCountOf
                    + " item=" + enhanceTable.ProtectItemId);
            }
        }

        if (transferStateOf != 0 || transferTxnCount != null)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var currencies = CurrencyTable.Load("config/currency.json");
            var enhanceTable = EnhanceTable.Load(
                enhanceConfigPath ?? characterConfig.EnhanceTable, items, currencies);
            var transferTable = TransferTable.Load(
                transferConfigPath ?? characterConfig.TransferTable,
                items, currencies, enhanceTable);
            var transfers = new TransferRepository(dataSource, transferTable, items);

            if (transferStateOf != 0)
            {
                var state = await transfers.StateAsync(
                    transferStateOf, transferFromSlot, transferToSlot, CancellationToken.None);
                Console.WriteLine("transfer_state=ok id=" + transferStateOf
                    + " from=" + transferFromSlot + " to=" + transferToSlot
                    + " from_level=" + state.FromLevel
                    + " to_level=" + state.ToLevel
                    // 显式输出小写，和网关回包以及核对脚本读到的布尔形状一致。
                    + " from_bound=" + (state.FromBound ? "true" : "false")
                    + " to_bound=" + (state.ToBound ? "true" : "false")
                    + " gold=" + state.Gold
                    + " stone=" + state.StoneLeft);
            }

            if (transferTxnCount != null)
            {
                Console.WriteLine("transfer_txn_count="
                    + await transfers.TxnCountAsync(transferTxnCount, CancellationToken.None)
                    + " txn=" + transferTxnCount);
            }
        }

        if (crystalStateOf != 0 || crystalTxnCount != null)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var crystalTable = CrystalTable.Load(
                crystalConfigPath ?? characterConfig.CrystalTable, items);
            var crystals = new CrystalRepository(dataSource, crystalTable, items, characterConfig);

            if (crystalStateOf != 0)
            {
                var state = await crystals.StateAsync(
                    crystalStateOf, crystalSlotOf, crystalItemOf ?? "", CancellationToken.None);
                Console.WriteLine("crystal_state=ok id=" + crystalStateOf
                    + " slot=" + crystalSlotOf
                    + " filled=" + state.Filled
                    + " installed=" + state.Installed
                    + " left=" + state.CrystalLeft
                    + " gold=" + state.Gold
                    + " enhance=" + state.EnhanceLevel
                    // 显式输出小写，和网关回包以及核对脚本读到的布尔形状一致。
                    + " bound=" + (state.Bound ? "true" : "false"));
            }

            if (crystalTxnCount != null)
            {
                Console.WriteLine("crystal_txn_count="
                    + await crystals.TxnCountAsync(crystalTxnCount, CancellationToken.None)
                    + " txn=" + crystalTxnCount);
            }
        }

        if (auctionBuyerOf != 0)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var auctionItems = ItemTable.Load("config/items.json");
            var auctionCurrencies = CurrencyTable.Load("config/currency.json");
            var auctionTable = AuctionTable.Load(
                auctionConfigPath ?? characterConfig.AuctionTable, auctionCurrencies);
            var auctions = new AuctionRepository(
                dataSource, auctionTable, auctionItems, characterConfig);
            var ledger = await auctions.StateAsync(
                auctionBuyerOf, auctionSellerOf, auctionListingOf,
                auctionTxnOf ?? "", CancellationToken.None);
            Console.WriteLine("auction_state=ok buyer=" + auctionBuyerOf
                + " buyer_gold=" + ledger.BuyerGold
                + " seller=" + auctionSellerOf
                + " seller_gold=" + ledger.SellerGold
                + " total_gold=" + ledger.TotalGold
                + " listing=" + auctionListingOf
                + " state=" + ledger.ListingState
                + " price=" + ledger.Price
                + " tax=" + ledger.Tax
                + " net=" + ledger.Net
                + " settlements=" + ledger.Settlements
                + " txn=" + ledger.Txns
                + " buyer_items=" + ledger.BuyerItems
                + " seller_items=" + ledger.SellerItems
                + " total_items=" + ledger.TotalItems
                + " accounts=" + ledger.Accounts);
        }

        if (ingotBuyerOf != 0)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var ingotCurrencies = CurrencyTable.Load("config/currency.json");
            var ingotTable = AuctionTable.Load(
                auctionConfigPath ?? characterConfig.AuctionTable, ingotCurrencies);
            var ingots = new IngotRepository(dataSource, ingotTable);
            var ledger = await ingots.StateAsync(
                ingotBuyerOf, ingotSellerOf, ingotListingOf,
                ingotTxnOf ?? "", CancellationToken.None);
            Console.WriteLine("ingot_state=ok buyer=" + ingotBuyerOf
                + " buyer_gold=" + ledger.BuyerGold
                + " seller=" + ingotSellerOf
                + " seller_gold=" + ledger.SellerGold
                + " total_gold=" + ledger.TotalGold
                + " buyer_ingot=" + ledger.BuyerIngot
                + " seller_ingot=" + ledger.SellerIngot
                + " total_ingot=" + ledger.TotalIngot
                + " listing=" + ingotListingOf
                + " state=" + ledger.ListingState
                + " price=" + ledger.Price
                + " tax=" + ledger.Tax
                + " net=" + ledger.Net
                + " settlements=" + ledger.Settlements
                + " txn=" + ledger.Txns
                + " accounts=" + ledger.Accounts);
        }

        // P3-2a：直接走一次相邻档兑换，不经过网关、也不经过拍卖。
        if (exchangeStoneOf != 0)
        {
            var exchangeConfig = CharacterConfig.Load(configPath);
            var exchangeCurrencies = CurrencyTable.Load("config/currency.json");
            var exchangeTable = StoneExchangeTable.Load(
                exchangeConfig.StoneExchangeTable, exchangeCurrencies);
            var exchangeRepo = new StoneExchangeRepository(
                dataSource, exchangeCurrencies, exchangeTable, exchangeConfig);
            var outcome = await exchangeRepo.ExchangeAsync(
                exchangeStoneOf, exchangeStoneKind ?? "", exchangeStoneTxn ?? "",
                CancellationToken.None);
            if (outcome.Ok)
            {
                Console.WriteLine("stone_exchange=ok id=" + exchangeStoneOf
                    + " from=" + outcome.FromKind
                    + " to=" + outcome.ToKind
                    + " from_cost=" + outcome.FromCost
                    + " to_gain=" + outcome.ToGain
                    + " from_amount=" + outcome.FromAmount
                    + " to_amount=" + outcome.ToAmount
                    + " ratio=" + outcome.Ratio
                    + " highest=" + outcome.HighestName
                    + " duplicate=" + (outcome.Duplicate ? "true" : "false"));
            }
            else
            {
                Console.WriteLine("stone_exchange=" + outcome.Reason);
            }
        }

        // P3-2a：从库里独立读回五档余额与兑换笔数，证「兑换不经过拍卖」。
        if (stoneExchangeStateOf != 0)
        {
            var stateMoney = new CurrencyRepository(dataSource);
            var stateBalances = await stateMoney.AllAsync(
                stoneExchangeStateOf, CancellationToken.None);
            var stateConfig = CharacterConfig.Load(configPath);
            var stateTable = StoneExchangeTable.Load(
                stateConfig.StoneExchangeTable, CurrencyTable.Load("config/currency.json"));
            var tiers = string.Join(" ", stateTable.Tiers.Select(
                t => t + "=" + stateBalances.GetValueOrDefault(t, 0L)));
            long exchangeTxns;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"角色\".stone_exchange_txn where character_id = $1"))
            {
                read.Parameters.AddWithValue(stoneExchangeStateOf);
                exchangeTxns = Convert.ToInt64(
                    await read.ExecuteScalarAsync(CancellationToken.None));
            }

            Console.WriteLine("stone_exchange_state=ok id=" + stoneExchangeStateOf
                + " " + tiers
                + " exchange_txns=" + exchangeTxns);
        }

        // P3-2a：城池税收账本与拍卖销毁税是分开的两张表，各数各的。
        if (cityState)
        {
            long cityRows;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"社交\".city"))
            {
                cityRows = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }

            long cityTaxRows;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"社交\".city_tax_ledger"))
            {
                cityTaxRows = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }

            long auctionTaxRows;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"交易\".auction_settlement"))
            {
                auctionTaxRows = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }

            long auctionTaxSum;
            await using (var read = dataSource.CreateCommand(
                "select coalesce(sum(tax_gold), 0) from \"交易\".auction_settlement"))
            {
                auctionTaxSum = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }

            long stoneListingRows;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"交易\".stone_listing"))
            {
                stoneListingRows = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }

            Console.WriteLine("city_state=ok city_rows=" + cityRows
                + " city_tax_rows=" + cityTaxRows
                + " auction_tax_rows=" + auctionTaxRows
                + " auction_tax_sum=" + auctionTaxSum
                + " stone_listing_rows=" + stoneListingRows);
        }

        // P3-3a4：与真实服务共用同一个 Repository，验重放幂等与各类拒绝路径，
        // 不必每验一次都打一场城战。事务号、宗门与队伍都由核对脚本给，
        // 与生产链路走的是同一笔事务，不是另一套写入。
        if (applySiegeResultTxn != null)
        {
            var cfg = CharacterConfig.Load(configPath);
            var cityTable = CityTable.Load(cfg.CityTable);
            var policy = SiegeTerritoryPolicy.Load(cfg.SiegeTerritoryTable, cityTable);
            var repo = new SiegeTerritoryRepository(dataSource, cityTable, policy);
            var outcome = await repo.ApplyAsync(
                applySiegeResultTxn, applySiegeResultCity, applySiegeResultMatch,
                applySiegeResultTeam, applySiegeResultSect, CancellationToken.None);
            Console.WriteLine("siege_result_apply=txn=" + applySiegeResultTxn
                + " city=" + outcome.CityId
                + " ok=" + (outcome.Ok ? "true" : "false")
                + " reason=" + outcome.Reason
                + " duplicate=" + (outcome.Duplicate ? "true" : "false")
                + " owner=" + outcome.OwnerSect
                + " tax_pct=" + outcome.TaxPct);
        }

        // P3-3a4：写回之后从库里独立读回城池事实值，不看回包。
        if (siegeResultState)
        {
            long rows;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"社交\".siege_result"))
            {
                rows = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }
            Console.WriteLine("siege_result_rows=" + rows);

            await using (var read = dataSource.CreateCommand(
                "select city_id, coalesce(owner_sect, ''), tax_pct from \"社交\".city order by city_id"))
            {
                await using var reader = await read.ExecuteReaderAsync(CancellationToken.None);
                while (await reader.ReadAsync(CancellationToken.None))
                {
                    Console.WriteLine("siege_result_city=city=" + reader.GetString(0)
                        + " owner=" + reader.GetString(1)
                        + " tax_pct=" + reader.GetInt32(2));
                }
            }

            await using (var read = dataSource.CreateCommand(
                "select txn_id, match_id, winner_team, winner_sect, tax_pct "
                + "from \"社交\".siege_result order by recorded_at desc, txn_id desc limit 1"))
            {
                await using var reader = await read.ExecuteReaderAsync(CancellationToken.None);
                if (await reader.ReadAsync(CancellationToken.None))
                {
                    Console.WriteLine("siege_result_last=txn=" + reader.GetString(0)
                        + " match=" + reader.GetString(1)
                        + " winner_team=" + reader.GetInt32(2)
                        + " winner_sect=" + reader.GetString(3)
                        + " tax_pct=" + reader.GetInt32(4));
                }
            }
        }

        // P3-2a：给核对脚本看「兑换不经过拍卖」——兑换前后挂单数不变。
        if (stoneListingState)
        {
            long listed = 0;
            long sold = 0;
            await using (var read = dataSource.CreateCommand(
                "select state, count(*) from \"交易\".stone_listing group by state"))
            {
                await using var reader = await read.ExecuteReaderAsync(CancellationToken.None);
                while (await reader.ReadAsync(CancellationToken.None))
                {
                    var state = reader.GetString(0);
                    var count = reader.GetInt64(1);
                    if (string.Equals(state, "listed", StringComparison.Ordinal))
                    {
                        listed = count;
                    }
                    else if (string.Equals(state, "sold", StringComparison.Ordinal))
                    {
                        sold = count;
                    }
                }
            }

            Console.WriteLine("stone_listing=listed listed=" + listed + " sold=" + sold);
        }

        if (reportName != null)
        {
            var report = await characters.ReportAsync(reportName, CancellationToken.None);
            if (report is null)
            {
                Console.WriteLine("report=missing name=" + reportName);
            }
            else
            {
                Console.WriteLine("report=ok name=" + reportName
                    + " id=" + report.Id
                    + " race=" + report.Race
                    + " class=" + report.ClassId
                    + " branch=" + report.Branch
                    + " level=" + report.Level
                    + " realm=" + report.Realm
                    + " main_stage_null=" + report.MainStageEmpty
                    + " alignment_null=" + report.AlignmentEmpty
                    + " appearance_len=" + report.AppearanceLength);
            }
        }

        // P3-4a2：通行证。三条入口共用同一份配置加载与同一个仓储：
        // state 只读；买与领各一笔事务——和网关那条路走的是同一段仓储代码。
        if (seasonPassStateOf != 0 || buySeasonPassOf != 0 || claimSeasonPassOf != 0)
        {
            var passConfig = CharacterConfig.Load(configPath);
            var (passTable, passItems) = LoadSeasonPass(passConfig, seasonPassPath);
            var passSeason = (seasonPassSeason ?? "").Trim();
            var seasonPassRepo = new SeasonPassRepository(
                dataSource, passTable, passItems, passConfig);

            if (seasonPassStateOf != 0)
            {
                var state = await seasonPassRepo.StateAsync(
                    seasonPassStateOf, passSeason, CancellationToken.None);
                Console.WriteLine("season_pass_state=ok id=" + seasonPassStateOf
                    + " season=" + state.SeasonId
                    + " paid=" + (state.Paid ? "true" : "false")
                    + " claims=" + state.Claims.Count
                    + " claimed=" + (state.Claims.Count == 0
                        ? "-"
                        : string.Join(",", state.Claims.Select(
                            c => c.Track + ":" + c.Stage + ":" + c.ItemId + "x" + c.Count))));

                // 包里那几件通行证奖励：编号、数量、绑定态都从库里读，不另算。
                var passDef = passTable.Find(state.SeasonId);
                var rewardIds = new HashSet<string>(StringComparer.Ordinal);
                if (passDef is not null)
                {
                    foreach (var stage in passDef.Free)
                    {
                        rewardIds.Add(stage.Rewards[0].ItemId);
                    }

                    foreach (var stage in passDef.Paid)
                    {
                        rewardIds.Add(stage.Rewards[0].ItemId);
                    }
                }

                var passInventory = new InventoryRepository(dataSource);
                var passSlots = await passInventory.ListAsync(
                    seasonPassStateOf, CancellationToken.None);
                var held = passSlots.Where(x => rewardIds.Contains(x.ItemId)).ToList();
                Console.WriteLine("season_pass_bag=" + (held.Count == 0
                    ? "-"
                    : string.Join(",", held.Select(
                        x => x.ItemId + "x" + x.Count + (x.Bound ? "@bound" : "@free")
                            + ":slot" + x.Slot))));
            }

            if (buySeasonPassOf != 0)
            {
                var outcome = await seasonPassRepo.BuyAsync(
                    buySeasonPassOf, passSeason, buySeasonPassTxn ?? "", CancellationToken.None);
                Console.WriteLine("season_pass_buy=done id=" + buySeasonPassOf
                    + " season=" + outcome.SeasonId
                    + " ok=" + (outcome.Ok ? "true" : "false")
                    + " reason=" + outcome.Reason
                    + " price=" + outcome.Price
                    + " balance=" + outcome.Balance
                    + " paid=" + (outcome.Paid ? "true" : "false")
                    + " duplicate=" + (outcome.Duplicate ? "true" : "false"));
            }

            if (claimSeasonPassOf != 0)
            {
                var outcome = await seasonPassRepo.ClaimAsync(
                    claimSeasonPassOf, passSeason, claimSeasonPassTrack ?? "",
                    claimSeasonPassStage, claimSeasonPassTxn ?? "", CancellationToken.None);
                Console.WriteLine("season_pass_claim=done id=" + claimSeasonPassOf
                    + " season=" + outcome.SeasonId
                    + " track=" + outcome.Track
                    + " stage=" + outcome.Stage
                    + " ok=" + (outcome.Ok ? "true" : "false")
                    + " reason=" + outcome.Reason
                    + " item=" + outcome.ItemId
                    + " count=" + outcome.Count
                    + " slot=" + outcome.Slot
                    + " duplicate=" + (outcome.Duplicate ? "true" : "false")
                    + " claimed=" + (outcome.Claimed ? "true" : "false"));
            }
        }

        // P3-4b：第二角色栏与外观收藏。名额的事实值在库里那一行（没有就按配置的基础位），
        // 外观收藏按账号把名下所有角色持有的外观类物品取并集——三个入口都只读，
        // 只有 --raw-grant-character-slot 会写，而它只给种子 / 核对用，不扣钱也不推进产品路径。
        if (characterSlotsOf != null || wardrobeOf != null || rawSlotAccount != null)
        {
            var slotConfig = CharacterConfig.Load(configPath);
            var slotTable = LoadCharacterSlot(slotConfig, characterSlotPath);
            var slotRepo = new CharacterSlotRepository(dataSource, slotTable);
            var wardrobeRepo = new WardrobeRepository(
                dataSource, ItemTable.Load("config/items.json"));

            if (rawSlotAccount != null)
            {
                var granted = await slotRepo.RawGrantAsync(
                    rawSlotAccount, rawSlotValue, "raw-slot-grant", CancellationToken.None);
                Console.WriteLine("character_slot_raw=done account=" + rawSlotAccount
                    + " slots=" + rawSlotValue
                    + " ok=" + (granted ? "true" : "false"));
            }

            if (characterSlotsOf != null)
            {
                var state = await slotRepo.DescribeAsync(characterSlotsOf, CancellationToken.None);
                Console.WriteLine("character_slots=ok account=" + state.Account
                    + " found=" + (state.Found ? "true" : "false")
                    + " base_slots=" + state.BaseSlots
                    + " max_slots=" + state.MaxSlots
                    + " slots=" + state.Slots
                    + " used=" + state.Used
                    + " product=" + state.ProductId
                    + " price=" + state.PriceYuanbao);
            }

            if (wardrobeOf != null)
            {
                var (found, entries) = await wardrobeRepo.DescribeAsync(
                    wardrobeOf, CancellationToken.None);
                Console.WriteLine("wardrobe=ok account=" + wardrobeOf
                    + " found=" + (found ? "true" : "false")
                    + " entries=" + entries.Count
                    + " items=" + (entries.Count == 0
                        ? "-"
                        : string.Join(",", entries.Select(e => e.ItemId)))
                    + " owners=" + (entries.Count == 0
                        ? "-"
                        : string.Join(",", entries.Select(
                            e => e.ItemId + ":" + string.Join("+", e.CharacterIds)))));
            }
        }
    }
    catch (Exception ex) when (ex is NpgsqlException or IOException)
    {
        Console.WriteLine("db_error=" + ex.GetType().Name + " " + ex.Message);
        return 5;
    }

    return 0;
}

// P3-4a2：通行证表的加载链。--season-pass-list 与三条连库入口共用这一份，
// 免得「只读入口」与「真实启动」各校验一套（preflight 第 3 道闸门的规矩）。
static (SeasonPassTable Table, ItemTable Items) LoadSeasonPass(CharacterConfig cfg, string? path)
{
    var currencies = CurrencyTable.Load("config/currency.json");
    var items = ItemTable.Load("config/items.json");
    var enhance = EnhanceTable.Load(cfg.EnhanceTable, items, currencies);
    var talismans = TalismanTable.Load(cfg.TalismanTable, items);
    var scrolls = ReviveScrollTable.Load(
        cfg.ReviveScrollTable, "config/classes.json", items, talismans);
    var boosters = ExpBoosterTable.Load(
        cfg.ExpBoosterTable, items, talismans, scrolls);
    var unbinds = UnbindTable.Load(
        cfg.UnbindTable, items, talismans, scrolls, boosters);
    var shop = ShopTable.Load(
        cfg.ShopTable, currencies, items, enhance, talismans, scrolls, boosters, unbinds);
    var table = SeasonPassTable.Load(
        path ?? cfg.SeasonPassTable, File.ReadAllText(cfg.SeasonTable), shop, items, enhance);
    return (table, items);
}

// C1：主线十二场的加载链。与真实启动同一份 MainlineTable.Load：
// 十二场挂的 main 必须是 1 人 / 不扣疲劳 / 不占份额，等突破的三段必须在首发里。
static MainlineTable LoadMainline(CharacterConfig cfg, string? path)
{
    var currencies = CurrencyTable.Load("config/currency.json");
    var dungeons = DungeonTable.Load(cfg.DungeonTable);
    var realms = RealmTable.Load(cfg.RealmTable, dungeons, currencies);
    return MainlineTable.Load(path ?? cfg.MainlineTable, dungeons, realms);
}

// P3-4b：月叠表的加载链。与真实启动同一份 MonthlyBundleTable.Load：
// 商品与标价、物品与堆叠上限、红符编号都在这条链上交叉校验。
static MonthlyBundleTable LoadMonthlyBundle(CharacterConfig cfg, string? path)
{
    var currencies = CurrencyTable.Load("config/currency.json");
    var items = ItemTable.Load("config/items.json");
    var enhance = EnhanceTable.Load(cfg.EnhanceTable, items, currencies);
    var talismans = TalismanTable.Load(cfg.TalismanTable, items);
    var scrolls = ReviveScrollTable.Load(
        cfg.ReviveScrollTable, "config/classes.json", items, talismans);
    var boosters = ExpBoosterTable.Load(cfg.ExpBoosterTable, items, talismans, scrolls);
    var unbinds = UnbindTable.Load(cfg.UnbindTable, items, talismans, scrolls, boosters);
    var shop = ShopTable.Load(
        cfg.ShopTable, currencies, items, enhance, talismans, scrolls, boosters, unbinds);
    return MonthlyBundleTable.Load(path ?? cfg.MonthlyBundleTable, shop, items, talismans);
}

// P3-4b：角色栏表的加载链。商城清单是同一份正本，商品与标价都从那里读。
static CharacterSlotTable LoadCharacterSlot(CharacterConfig cfg, string? path)
{
    var currencies = CurrencyTable.Load("config/currency.json");
    var items = ItemTable.Load("config/items.json");
    var enhance = EnhanceTable.Load(cfg.EnhanceTable, items, currencies);
    var talismans = TalismanTable.Load(cfg.TalismanTable, items);
    var scrolls = ReviveScrollTable.Load(
        cfg.ReviveScrollTable, "config/classes.json", items, talismans);
    var boosters = ExpBoosterTable.Load(cfg.ExpBoosterTable, items, talismans, scrolls);
    var unbinds = UnbindTable.Load(cfg.UnbindTable, items, talismans, scrolls, boosters);
    var shop = ShopTable.Load(
        cfg.ShopTable, currencies, items, enhance, talismans, scrolls, boosters, unbinds);
    return CharacterSlotTable.Load(path ?? cfg.CharacterSlotTable, shop);
}

using var stop = new CancellationTokenSource();
PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.Cancel();
});
PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    stop.Cancel();
});

return await new CharacterProcess(configPath).RunAsync(stop.Token);
