using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>命令行解析：133 个 flag 名与取值个数保持原样，未知参数回 64。</summary>
internal static class CliParser
{
    public static int Parse(string[] args)
    {
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
            // 只给核对用：NPC / 怪物两张内容表，不连库；坏配置也走这里。
            case "--content-list": contentList = true; break;
            // 只给核对用：把物品表的品阶 / 套装 / 图标 / 说明打出来，不连库。坏配置也走这里。
            case "--item-list":
                itemList = true;
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

            case "--raw-realm-phase-write" when i + 2 < args.Length:
                long.TryParse(args[++i], out rawRealmPhaseId);
                int.TryParse(args[++i], out rawRealmPhaseValue);
                break;

            case "--realm-passive-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out realmPassiveStateOf);
                break;

            case "--hero-codex-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out heroCodexStateOf);
                break;

            case "--hero-codex-table-path" when i + 1 < args.Length:
                heroCodexTablePath = args[++i];
                break;

            case "--promote-realm-phase" when i + 2 < args.Length:
                long.TryParse(args[++i], out promoteRealmPhaseOf);
                promoteRealmPhaseTxn = args[++i];
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
            case "--city-tax-row" when i + 1 < args.Length:
                cityTaxRowTxn = args[++i];
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

        return 0;
    }
}
