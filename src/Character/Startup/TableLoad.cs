namespace Character;

/// <summary>
/// 启动装配用到的一整套配置表（D2d：从 CharacterProcess 里原样搬出来）。
/// 加载顺序与校验都在 <see cref="TableLoad.Load"/> 里，缺一样就起不来。
/// </summary>
public sealed class CharacterTables
{
    public required CalendarTable Calendar { get; init; }
    public required RaceTable Races { get; init; }
    public required BranchTable Branches { get; init; }
    public required ClassIds Classes { get; init; }
    public required ItemTable Items { get; init; }
    public required CurrencyTable Currencies { get; init; }
    public required DungeonTable Dungeons { get; init; }
    public required RealmTable Realms { get; init; }
    public required ExpTable ExpTable { get; init; }
    public required EnhanceTable EnhanceTable { get; init; }
    public required TransferTable TransferTable { get; init; }
    public required CrystalTable CrystalTable { get; init; }
    public required GradeTable Grades { get; init; }
    public required SetTable Sets { get; init; }
    public required AuctionTable AuctionTable { get; init; }
    public required StallTable StallTable { get; init; }
    public required ShopTable ShopTable { get; init; }
    public required NpcShopTable NpcShopTable { get; init; }
    public required HomeCoinShopTable HomeCoinShopTable { get; init; }
    public required TalismanTable TalismanTable { get; init; }
    public required PillTable PillTable { get; init; }
    public required ReviveScrollTable ScrollTable { get; init; }
    public required ExpBoosterTable BoosterTable { get; init; }
    public required UnbindTable UnbindTable { get; init; }
    public required RechargeTable RechargeTable { get; init; }
    public required StoneExchangeTable StoneExchange { get; init; }
    public required CityTable CityTable { get; init; }
    public required SiegeTerritoryPolicy SiegeTerritoryPolicy { get; init; }
    public required SeasonPassTable SeasonPassTable { get; init; }
    public required MonthlyBundleTable MonthlyBundleTable { get; init; }
    public required CharacterSlotTable CharacterSlotTable { get; init; }
    public required MainlineTable MainlineTable { get; init; }
    public required HeroCodexTable HeroCodexTable { get; init; }
    public required MountTable MountTable { get; init; }
    public required SpiritBeastTable SpiritBeastTable { get; init; }
    /// <summary>B6：觉醒门槛与「哪条分支解锁哪条技能」的正本。</summary>
    public required AwakenTable AwakenTable { get; init; }
    public required RecipeTable RecipeTable { get; init; }

    /// <summary>B3：制作掷点正本（区间 / 权重档 / 附加属性允许表）。</summary>
    public required CraftRollTable CraftRollTable { get; init; }
    public required CityTaskTable CityTaskTable { get; init; }
    public required SeasonPassProgressTable SeasonPassProgressTable { get; init; }
    public required NameRulesTable NameRulesTable { get; init; }
    public required DungeonDropTable DungeonDropTable { get; init; }
    public required MainlineDropTable MainlineDropTable { get; init; }
}

/// <summary>读 config/ 下的一整套表。异常照旧抛 ConfigException，由入口打 config_error 并退 2。</summary>
public static class TableLoad
{
    public static CharacterTables Load(CharacterConfig config, string configPath)
    {
        // 三张表：出身、分支、职业编号。种族与分支都不带属性，只决定外观、对白与兵器。
        RaceTable races;
        BranchTable branches;
        ClassIds classes;
        ItemTable items;
        CurrencyTable currencies;
        DungeonTable dungeons;
        RealmTable realms;
        ExpTable expTable;
        EnhanceTable enhanceTable;
        TransferTable transferTable;
        CrystalTable crystalTable;
        // B2：品阶与套装两张正本。品阶表先加载，物品表随后逐件对账。
        GradeTable grades;
        SetTable sets;
        AuctionTable auctionTable;
        StallTable stallTable;
        ShopTable shopTable;
        NpcShopTable npcShopTable;
        HomeCoinShopTable homeCoinShopTable;
        TalismanTable talismanTable;
        PillTable pillTable;
        ReviveScrollTable scrollTable;
        ExpBoosterTable boosterTable;
        UnbindTable unbindTable;
        RechargeTable rechargeTable;
        StoneExchangeTable stoneExchange;
        CityTable cityTable;
        SiegeTerritoryPolicy siegeTerritoryPolicy;
        SeasonPassTable seasonPassTable;
        MonthlyBundleTable monthlyBundleTable;
        CharacterSlotTable characterSlotTable;
        MainlineTable mainlineTable;
        HeroCodexTable heroCodexTable;
        CalendarTable calendar;
        RecipeTable recipeTable;
        CraftRollTable craftRollTable;
        MountTable mountTable;
        SpiritBeastTable spiritBeastTable;
        AwakenTable awakenTable;
        CityTaskTable cityTaskTable;
        SeasonPassProgressTable seasonPassProgressTable;
        NameRulesTable nameRulesTable;
        DungeonDropTable dungeonDropTable;
        MainlineDropTable mainlineDropTable;
            // Q3/G1：日 / 周 / 赛季边界只从这一份日历表算，先于所有仓储加载。
            calendar = CalendarTable.Load(config.CalendarTable);
            races = RaceTable.Load("config/races.json");
            branches = BranchTable.Load("config/branches.json");
            classes = new ClassIds("config/classes.json");
            // B2：品阶表是物品表的对账基准，先于 items 的逐件校验读进来。
            grades = GradeTable.Load("config/grades.json");
            items = ItemTable.Load("config/items.json");
            grades.CheckItems(items);
            // B2：套装正本。加载期与物品表交叉对账（套内件数、同套同品阶、加成门槛）。
            sets = SetTable.Load("config/sets.json", items, grades);
            // Q12/K3：名字规则。长度、唯一、改名与敏感词表路径都在这份文件里；
            // rename_item_id 必须在上面这份物品表里——加载期就挡住「配了一个不存在的卡」。
            nameRulesTable = NameRulesTable.Load(config.NameRulesTable, items);
            currencies = CurrencyTable.Load("config/currency.json");
            dungeons = DungeonTable.Load(config.DungeonTable);
            // P3-1a1：十一段按顺序开放，首发到结丹；元婴起每段一个开关。
            // 与副本表交叉校验：写 realm 的只能是首发之后的新副本，旧副本不许写。
            realms = RealmTable.Load(config.RealmTable, dungeons, currencies);
            expTable = ExpTable.Load(config.ExpTable);
            // 开发架构第 7 节：强化概率由角色服读。三档材料要在物品表里存在，
            // 扣的金币要在币种表里存在，缺一样就起不来。
            enhanceTable = EnhanceTable.Load(config.EnhanceTable, items, currencies);
            // 方案 9.3：转移石要在物品表里、费用表要照写定的上限与递增关系，
            // 缺一样就起不来。和强化表一样只读一次，改配置重启生效。
            transferTable = TransferTable.Load(config.TransferTable, items, currencies, enhanceTable);
            // 方案第 9 章：三类、部位限定与可拆卸复用由配置校验；装备部位漏配、
            // 类别被改、结晶不在物品表里，缺一样就起不来。改配置重启生效。
            crystalTable = CrystalTable.Load(config.CrystalTable, items);
            // 方案第 12 章写定的 5%–8% 抽税区间由 AuctionTable 硬校验，
            // 具体税率与取整方式在 config/auction.json，改配置重启生效。
            auctionTable = AuctionTable.Load(config.AuctionTable, currencies);
            // B5：摆摊的税率区间、格数、等级门槛与「不跨服」由 StallTable 硬校验，
            // 与拍卖分成两份文件：摆摊 5% / 拍卖 6% 各读各的，改配置重启生效。
            stallTable = StallTable.Load(config.StallTable);
            // 方案 12.1：红符是绑定的生命回复消耗，回复不超过同级红药，
            // 和红药、牧师治疗共用冷却。回复值与上限都在 config/talisman.json，
            // 超了、物品不在表里、没到账即绑，缺一样就起不来。
            talismanTable = TalismanTable.Load(config.TalismanTable, items);
            // B1：六种丹的回复值、池、冷却组与档位。加载期要三份文件互校：
            // 丹是消耗品且到账不绑（能上拍卖）、红药 / 蓝药的冷却组必须是 heal / mana、
            // 同级最强那一档丹必须等于符表里的上限、突破丹必须被 realms.json 的某一段用到。
            pillTable = PillTable.Load(config.PillTable, items, talismanTable, realms);
            // 方案 12.1：复活卷轴只拉起自己，冷却跨场保留。编号与冷却时长在
            // revive_scroll.json；拉起血量比例与经验比例读同一份 classes.json
            // （一转基础，生息与卷轴同源，二转减免只给生息），不在这里抄数。
            scrollTable = ReviveScrollTable.Load(
                config.ReviveScrollTable, "config/classes.json", items, talismanTable);
            // 方案 10 章 / 12.1：限时经验道具只乘结算这一笔，时长与倍率在
            // exp_booster.json；到期时刻落角色库，不在这里生成，也不在内存计时。
            // 与符、卷轴的编号不许撞（三种是三种东西）。
            boosterTable = ExpBoosterTable.Load(
                config.ExpBoosterTable, items, talismanTable, scrollTable);
            // 方案 12.1：解绑道具购买后绑定，只解开装备；绑定越久张数越少、下限走配置。
            // 档位与张数在 unbind_item.json；绑定状态仍是 inventory 的 bound / bound_at，
            // 不在这里另存一份。与符、卷轴、经验道具的编号不许撞（四种是四种东西）。
            unbindTable = UnbindTable.Load(
                config.UnbindTable, items, talismanTable, scrollTable, boosterTable);
            // P1-5d：内部充值商品表。P0 不接真实支付渠道，订单与回调都在本服务；
            // 元宝数只在这份配置里，一笔订单只发一次。
            rechargeTable = RechargeTable.Load(config.RechargeTable);
            // 方案 12.1 定死十八种可上架种类，商城只收元宝；清单外的编号在购买时拒绝。
            // P1-5c2 起逐种接发货：已接的种类必须指向 items.json 里的真物品，
            // 保护券还必须就是 enhance.json 那一个编号（两处发的是同一种券），
            // 红符必须就是 talisman.json 那一个编号（红符不是红药）。
            shopTable = ShopTable.Load(
                config.ShopTable, currencies, items, enhanceTable, talismanTable,
                scrollTable, boosterTable, unbindTable);
            homeCoinShopTable = HomeCoinShopTable.Load(
                "config/home_coin_shop.json", currencies, items);
            // K2+Q13：NPC 商店。货架 / 回收 / 回购 / 修理四段同一份文件；加载期与物品表、
            // 币种表、npc.json 交叉校验——装备与战力材料写进货架、价格缺失、耐久上限漏品阶，
            // 都在启动时起不来，不带着半截配置跑。
            npcShopTable = NpcShopTable.Load("config/npc_shop.json", currencies, items);
            // P3-2a：灵石五档兑换表与城池表。与启动共用同一套 Load：
            // 档位都要在币种表里、最高一档的中文名要与币种表一致；
            // 城池的 vein_id 要对应 gather.json 里的 spirit_stone 节点。
            // 缺一样就起不来，不带着半截配置跑。
            stoneExchange = StoneExchangeTable.Load(config.StoneExchangeTable, currencies);
            cityTable = CityTable.Load(config.CityTable);
            // P3-3a4：领地表与城池表是同一份正本，城池编号交叉校验，抽成比例只从这里读。
            // 城战胜负写回时角色服不另算税率，也不接受请求里带来的税率。
            siegeTerritoryPolicy = SiegeTerritoryPolicy.Load(
                config.SiegeTerritoryTable, cityTable);
            // P3-4a2：通行证两轨。免费轨只许发称号与外观碎片，付费轨只许发当季外观、
            // 洞府摆件与保护券；保护券必须就是 enhance.json 那一件（商城卖的同一种），
            // 四种奖励都要在 items.json 里且到账即绑。season.json 只用来核对季真的存在。
            seasonPassTable = SeasonPassTable.Load(
                config.SeasonPassTable, File.ReadAllText(config.SeasonTable),
                shopTable, items, enhanceTable);
            // P3-4b：月叠只许发传送符与红符（三十天份），两种编号分别等于商城那条传送符
            // 与 talisman.json 那一件；蓝符 / 卷轴 / 经验道具 / 金币材料在加载期就长不出来。
            monthlyBundleTable = MonthlyBundleTable.Load(
                config.MonthlyBundleTable, shopTable, items, talismanTable);
            // P3-4b：第二角色栏只改「一个账号能建几个角色」。基础位 1、上限 2（方案只写了
            // 「第二」这一种），商品与标价在 shop.json；装备 / 等级 / 境界仍按角色各存一行。
            characterSlotTable = CharacterSlotTable.Load(config.CharacterSlotTable, shopTable);
            // C1：主线十二场的内容正本。只加内容和配置：加载期与 dungeons.json 的 main
            // （1 人 / 不扣疲劳 / 不占份额）、realms.json 的首发三段交叉校验。
            // 老卡的临时配置目录没有这份表时降级成「没有主线内容」，不挡启动。
            mainlineTable = File.Exists(config.MainlineTable)
                ? MainlineTable.Load(config.MainlineTable, dungeons, realms)
                : MainlineTable.Missing();
            heroCodexTable = HeroCodexTable.Load("config/hero_codex.json", mainlineTable);
            // K3：生产 / 配方的唯一正本。四个副职、每条配方的材料 / 产出 / 费用 /
            // 成功率 / 耗时 / 解锁等级都在这一份文件里；加一个配方不改代码。
            recipeTable = RecipeTable.Load(config.RecipeTable, items, currencies);
            // B3：制作掷点先于配方区间的对账读进来，再逐条核 attr_min / attr_max。
            craftRollTable = CraftRollTable.Load(config.CraftRollTable, items, grades);
            craftRollTable.CheckRecipes(recipeTable, grades);
            // B4：玩法坐骑与灵兽。坐骑只差移动速度、灵兽只做收集 / 外观 / 生活协助，
            // 两份都在加载期与物品表交叉校验（kind、不可叠、档位区间），
            // 灵兽还要与图鉴合计对一次「一处上限」（hero_codex.json 的 max_total_passive_pct_bps）。
            mountTable = MountTable.Load(config.MountTable, items);
            spiritBeastTable = SpiritBeastTable.Load(config.SpiritBeastTable, items, heroCodexTable);
            // B6：觉醒。min_level 必须等于满级（exp.json 的 level_cap），
            // branch_skills 必须正好覆盖 branches.json 的全部 10 条分支；
            // 技能编号是不是本分支的觉醒技由战斗服启动期按 skills.json 交叉校验。
            awakenTable = AwakenTable.Load(config.AwakenTable, branches, expTable);
            // Q5/J2：主城修炼 / 境界任务的正本。与启动共用同一份 Load：城池编号必须在
            // config/city.json 里、奖励只能是灵石五档、境界门槛必须在 realms.json 的顺序表里；
            // 缺表降级成「没有任务」，老卡环境照常起。
            cityTaskTable = File.Exists(config.CityTaskTable)
                ? CityTaskTable.Load(config.CityTaskTable, cityTable, stoneExchange, realms)
                : CityTaskTable.Empty;
            // Q6/J4：通行证点数与来源的正本。副本来源必须真的在 dungeons.json 里，
            // daily / weekly 必须有次数上限；缺表降级成「没有来源」，老卡环境照常起。
            seasonPassProgressTable = File.Exists(config.SeasonPassProgressTable)
                ? SeasonPassProgressTable.Load(config.SeasonPassProgressTable, dungeons)
                : SeasonPassProgressTable.Missing();
            var dungeonDropPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(configPath))!, "dungeon_drops.json"));
            dungeonDropTable = DungeonDropTable.LoadIfPresent(
                dungeonDropPath,
                items.All.Select(item => item.Id),
                currencies.All.Select(currency => currency.Id),
                // B2：装备掉落要在本副本的品阶带内。
                grades.ItemGrades(items),
                grades.Ordered.ToDictionary(g => g.Id, g => g.Order, StringComparer.Ordinal));
            var mainlineDropPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(configPath))!, "mainline_drops.json"));
            mainlineDropTable = MainlineDropTable.LoadIfPresent(mainlineDropPath, items, currencies);

        return new CharacterTables
        {
            Calendar = calendar,
            Races = races,
            Branches = branches,
            Classes = classes,
            Items = items,
            Currencies = currencies,
            Dungeons = dungeons,
            Realms = realms,
            ExpTable = expTable,
            EnhanceTable = enhanceTable,
            TransferTable = transferTable,
            CrystalTable = crystalTable,
            Grades = grades,
            Sets = sets,
            AuctionTable = auctionTable,
            StallTable = stallTable,
            ShopTable = shopTable,
            NpcShopTable = npcShopTable,
            HomeCoinShopTable = homeCoinShopTable,
            TalismanTable = talismanTable,
            PillTable = pillTable,
            ScrollTable = scrollTable,
            BoosterTable = boosterTable,
            UnbindTable = unbindTable,
            RechargeTable = rechargeTable,
            StoneExchange = stoneExchange,
            CityTable = cityTable,
            SiegeTerritoryPolicy = siegeTerritoryPolicy,
            SeasonPassTable = seasonPassTable,
            MonthlyBundleTable = monthlyBundleTable,
            CharacterSlotTable = characterSlotTable,
            MainlineTable = mainlineTable,
            HeroCodexTable = heroCodexTable,
            MountTable = mountTable,
            SpiritBeastTable = spiritBeastTable,
            AwakenTable = awakenTable,
            RecipeTable = recipeTable,
            CraftRollTable = craftRollTable,
            CityTaskTable = cityTaskTable,
            SeasonPassProgressTable = seasonPassProgressTable,
            NameRulesTable = nameRulesTable,
            DungeonDropTable = dungeonDropTable,
            MainlineDropTable = mainlineDropTable,
        };
    }
}
