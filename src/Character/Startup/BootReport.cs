namespace Character;

/// <summary>
/// 启动回显（D2d：从 CharacterProcess 里原样搬出来）。每一行与只读入口同形，
/// 改配置重启后能直接核对到事实值；这里只打印，不判定。
/// </summary>
public static class BootReport
{
    public static void Print(CharacterConfig config, string configPath, CharacterTables tables)
    {
        Console.WriteLine("config=" + configPath);
        Console.WriteLine("listen=" + config.Listen);
        // Q3/G1：启动时把这一份日历的日 / 周 / 赛季边界打出来，改时区或重置小时后
        // 一眼能确认跑的是哪一套时钟；判定仍在每一笔事务里重算，不缓存。
        var bootNow = DateTimeOffset.UtcNow;
        var bootSeason = tables.Calendar.SeasonOf(bootNow);
        Console.WriteLine("calendar=ok version=" + tables.Calendar.Version
            + " timezone=" + tables.Calendar.TimeZoneId
            + " daily_reset_hour=" + tables.Calendar.DailyResetHour
            + " weekly_reset=" + tables.Calendar.WeeklyResetDay.ToString().ToLowerInvariant()
            + "@" + tables.Calendar.WeeklyResetHour
            + " season_start=" + tables.Calendar.SeasonStartDate.ToString("yyyy-MM-dd")
            + " season_length_days=" + tables.Calendar.SeasonLengthDays
            + " day=" + tables.Calendar.DayKey(bootNow).ToString("yyyy-MM-dd")
            + " week_start=" + tables.Calendar.WeekStartDate(bootNow).ToString("yyyy-MM-dd")
            + " season_index=" + bootSeason.Index);
        Console.WriteLine("social_upstream=" + config.SocialUpstream);
        Console.WriteLine("global_upstream=" + config.GlobalUpstream);
        Console.WriteLine("races=" + tables.Races.All.Count + " race_table_version=" + tables.Races.Version);
        Console.WriteLine("branches=" + tables.Branches.All.Count + " branch_table_version=" + tables.Branches.Version);
        Console.WriteLine("items=" + tables.Items.All.Count + " item_table_version=" + tables.Items.Version
            + " bag_slots=" + config.BagSlots);
        Console.WriteLine("currencies=" + tables.Currencies.All.Count + " currency_table_version=" + tables.Currencies.Version);
        Console.WriteLine("dungeons=" + tables.Dungeons.All.Count + " dungeon_table_version=" + tables.Dungeons.Version
            + " dungeons_version=" + tables.Dungeons.Version
            + " fatigue_daily_cap=" + tables.Dungeons.FatigueDailyCap
            + " hot_spring_recovery=" + tables.Dungeons.HotSpringRecovery
            + " weekly_share_cap=" + tables.Dungeons.WeeklyShareCap
            + " monster_hp_scale_per_player=" + tables.Dungeons.MonsterHpScalePerPlayer
            + " monster_damage_scale_per_player=" + tables.Dungeons.MonsterDamageScalePerPlayer);
        // 逐条列出人数区间与「每周次数 / 每人份数」，高难本的规则改一条重启后能直接核对到。
        foreach (var dungeon in tables.Dungeons.All)
        {
            Console.WriteLine("dungeon=" + dungeon.Id
                + " tier=" + dungeon.Tier
                + " min=" + dungeon.MinPlayers
                + " max=" + dungeon.MaxPlayers
                + " entries=" + dungeon.EntriesPerWeek
                + " reward=" + dungeon.RewardPerPlayer
                + " drill=" + dungeon.Drill);
        }
        // P3-1a1：十一段顺序与开关。改 tables.Realms.json 重启后这里能直接核对到。
        Console.WriteLine("realms=" + tables.Realms.Realms.Count
            + " realm_table_version=" + tables.Realms.Version
            + " launch_until=" + tables.Realms.LaunchUntil
            + " open_realms=" + string.Join(",", tables.Realms.Realms.Where(tables.Realms.IsOpen))
            + " closed_realms=" + string.Join(",", tables.Realms.Realms.Where(r => !tables.Realms.IsOpen(r))));
        foreach (var dungeon in tables.Dungeons.All.Where(d => d.Realm is not null))
        {
            Console.WriteLine("dungeon_realm=" + dungeon.Id + " realm=" + dungeon.Realm);
        }

        // P3-1a2：每一段的突破要求。改 tables.Realms.json 重启后这里能直接核对到。
        foreach (var requirement in tables.Realms.Requirements)
        {
            Console.WriteLine("realm_advance=" + requirement.From + "->" + requirement.Next
                + " drill=" + requirement.Drill
                + " stone=" + requirement.Stone
                + " stone_cost=" + requirement.StoneCost
                + " success_pct=" + requirement.SuccessPct
                + " main_stage=" + requirement.MainStage
                + " side_quests=" + string.Join("+", requirement.SideQuests));
        }
        Console.WriteLine("exp_table_version=" + tables.ExpTable.Version
            + " level_cap=" + tables.ExpTable.LevelCap
            + " base_exp=" + tables.ExpTable.BaseExp
            + " party_bonus_pct=" + tables.ExpTable.PartyBonusPct
            + " party_min_size=" + tables.ExpTable.PartyMinSize
            + " exp_per_level=" + tables.ExpTable.ExpPerLevel);
        Console.WriteLine("enhance_table_version=" + tables.EnhanceTable.Version
            + " max_level=" + tables.EnhanceTable.MaxLevel
            + " gold_kind=" + tables.EnhanceTable.GoldKind
            + " tiers=" + string.Join(",",
                tables.EnhanceTable.Tiers.Select(t => t.Id + ":" + t.FromLevel + "-" + t.ToLevel
                    + ":" + t.StoneId + ":rate=" + t.SuccessRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + ":gold=" + t.GoldCost + ":stone=" + t.StoneCost))
            + " rates=" + string.Join(",", tables.EnhanceTable.Tiers.Select(
                t => t.SuccessRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))
            + " coupon=" + tables.EnhanceTable.ProtectItemId
            + " coupon_bind=always");
        // 与 --transfer-state 只读入口同形，改金币费用重启后能直接核对到。
        Console.WriteLine("transfer_table_version=" + tables.TransferTable.Version
            + " transfer_stone=" + tables.TransferTable.StoneItemId
            + " transfer_stone_cost=" + tables.TransferTable.StoneCost
            + " transfer_gold_kind=" + tables.TransferTable.GoldKind
            + " transfer_gold=" + string.Join(",", tables.TransferTable.GoldCosts));
        // 与 --crystal-state 只读入口同形，改部位映射或镶嵌位数量重启后能直接核对到。
        Console.WriteLine("crystal_table_version=" + tables.CrystalTable.Version
            + " crystal_sockets=" + tables.CrystalTable.SocketCount
            + " crystal_categories=" + string.Join(",", CrystalTable.WrittenCategories)
            + " crystal_parts=" + string.Join(",", tables.CrystalTable.EquipmentParts
                .OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value))
            + " crystal_allowed=" + string.Join(",", CrystalTable.WrittenCategories
                .Select(c => c + ":" + string.Join("+", tables.CrystalTable.CategoryParts[c])))
            + " crystal_items=" + string.Join(",", tables.CrystalTable.Crystals
                .OrderBy(c => c.Id).Select(c => c.Id + ":" + c.Category + ":" + c.FixedValue)));
        // 与 --auction-state 只读入口同形，改税率重启后能直接核对到。
        Console.WriteLine("auction_table_version=" + tables.AuctionTable.Version
            + " tax_rate_pct=" + tables.AuctionTable.TaxRatePct
            + " tax_min=" + AuctionTable.WrittenMinRatePct
            + " tax_max=" + AuctionTable.WrittenMaxRatePct
            + " gold_kind=" + tables.AuctionTable.GoldKind
            + " rounding=" + tables.AuctionTable.Rounding
            + " ingot_kind=" + tables.AuctionTable.IngotKind);
        // B5：摆摊与拍卖分开的两份税率，重启后这里能直接核对到。
        Console.WriteLine("stall_table_version=" + tables.StallTable.Version
            + " pending=" + (tables.StallTable.Pending ? "true" : "false")
            + " tax_rate_pct=" + tables.StallTable.TaxRatePct
            + " slots=" + tables.StallTable.Slots
            + " min_level=" + tables.StallTable.MinLevel
            + " cross_server=" + (tables.StallTable.CrossServer ? "true" : "false"));
        // 与 --shop-list 只读入口同形：清单改一条，重启后这里能直接核对到。
        Console.WriteLine("npc_shop_table_version=" + tables.NpcShopTable.Version
            + " shops=" + tables.NpcShopTable.Shops.Count
            + " shelf_kinds=" + tables.NpcShopTable.ShelfKinds.Count
            + " sell_lines=" + tables.NpcShopTable.Sell.Items.Count
            + " buyback_keep=" + tables.NpcShopTable.Buyback.Keep
            + " repair_per_point=" + tables.NpcShopTable.Repair.PricePerPoint);
        Console.WriteLine("shop_table_version=" + tables.ShopTable.Version
            + " currency=" + tables.ShopTable.Currency
            + " items=" + tables.ShopTable.Items.Count
            + " kinds=" + tables.ShopTable.Categories.Count
            + " delivered_kinds=" + tables.ShopTable.DeliveredCount);
        // 与 --talisman-state 只读入口同形：回复值改一条，重启后这里能直接核对到。
        Console.WriteLine("talisman_table_version=" + tables.TalismanTable.Version
            + " red_item=" + (tables.TalismanTable.RedTalisman?.ItemId ?? "")
            + " hp_restore=" + (tables.TalismanTable.RedTalisman?.HpRestore ?? 0)
            + " potion_hp_restore=" + (tables.TalismanTable.RedTalisman?.PotionHpRestore ?? 0)
            + " cooldown_group=" + (tables.TalismanTable.RedTalisman?.CooldownGroup ?? "")
            + " cooldown_ms=" + (tables.TalismanTable.RedTalisman?.CooldownMs ?? 0)
            + " blue_item=" + (tables.TalismanTable.BlueTalisman?.ItemId ?? "")
            + " mana_restore=" + (tables.TalismanTable.BlueTalisman?.ManaRestore ?? 0)
            + " potion_mana_restore=" + (tables.TalismanTable.BlueTalisman?.PotionManaRestore ?? 0)
            + " blue_group=" + (tables.TalismanTable.BlueTalisman?.CooldownGroup ?? "")
            + " blue_cooldown_ms=" + (tables.TalismanTable.BlueTalisman?.CooldownMs ?? 0));
        Console.WriteLine("pill_table_version=" + tables.PillTable.Version
            + " pending=" + (tables.PillTable.Note.Length > 0 ? "true" : "false")
            + " pills=" + tables.PillTable.All.Count
            + " hp_top=" + tables.PillTable.Strongest(PillTable.KindHp)
            + " mana_top=" + tables.PillTable.Strongest(PillTable.KindMana)
            + " breakthrough=" + string.Join(",", tables.PillTable.BreakthroughPills.Select(x => x.ItemId)));
        // 与 --revive-scroll-list 只读入口同形：冷却时长、两个比例改一条，
        // 重启后这里能直接核对到；冷却到期时刻不在这里，在角色库那一行。
        Console.WriteLine("revive_scroll_table_version=" + tables.ScrollTable.Version
            + " item=" + tables.ScrollTable.ReviveScroll.ItemId
            + " cooldown_ms=" + tables.ScrollTable.ReviveScroll.CooldownMs
            + " hp_restore_pct=" + tables.ScrollTable.HpRestorePct
            + " exp_cost_pct=" + tables.ScrollTable.ExpCostPct);
        // 与 --exp-booster-list 只读入口同形：时长、倍率改一条，重启后这里能直接核对到。
        // 到期时刻不在这里，在角色库那一行（--exp-booster-state 读）。
        Console.WriteLine("recharge_table_version=" + tables.RechargeTable.Version
            + " products=" + tables.RechargeTable.Products.Count);
        Console.WriteLine("unbind_table_version=" + tables.UnbindTable.Version
            + " item=" + tables.UnbindTable.Unbind.ItemId
            + " tiers=" + tables.UnbindTable.Unbind.Tiers.Count
            + " floor=" + tables.UnbindTable.FloorCost);
        Console.WriteLine("exp_booster_table_version=" + tables.BoosterTable.Version
            + " item=" + tables.BoosterTable.ExpBooster.ItemId
            + " duration_ms=" + tables.BoosterTable.ExpBooster.DurationMs
            + " gain_pct=" + tables.BoosterTable.ExpBooster.GainPct);
        // P3-2a：灵石五档与比例只在配置里，启动日志能核对到全部事实值。
        Console.WriteLine("stone_exchange=tiers=" + tables.StoneExchange.Tiers.Count
            + " ratio=" + tables.StoneExchange.Ratio
            + " gain=" + tables.StoneExchange.Gain
            + " highest=" + tables.StoneExchange.HighestKind + "/" + tables.StoneExchange.HighestName
            + " ingot=" + tables.StoneExchange.IngotKind
            + " version=" + tables.StoneExchange.Version);
        // Q6/J4：通行证进度的事实值。每阶点数与来源全在 config/season_pass_progress.json，
        // 与 --pass-progress-config 的只读入口同一个键名、同一组字段。
        Console.WriteLine("season_pass_progress=version=" + tables.SeasonPassProgressTable.Version
            + " pending=" + (tables.SeasonPassProgressTable.Pending ? "true" : "false")
            + " points_per_stage=" + tables.SeasonPassProgressTable.PointsPerStage
            + " sources=" + tables.SeasonPassProgressTable.Sources.Count);
        foreach (var source in tables.SeasonPassProgressTable.Sources)
        {
            Console.WriteLine("season_pass_source=" + source.Id
                + " kind=" + source.Kind
                + " dungeon=" + (source.DungeonId.Length == 0 ? "-" : source.DungeonId)
                + " points=" + source.Points
                + " period=" + source.Period
                + " max=" + source.MaxPerPeriod);
        }

        // Q12/K3：名字规则的事实值。长度、唯一、改名开关、改名卡编号与敏感词条数
        // 都能在这里一次看全；与 --name-rules 的只读入口同一组字段。
        Console.WriteLine("name_rules=version=" + tables.NameRulesTable.Version
            + " global_unique=" + (tables.NameRulesTable.GlobalUnique ? "true" : "false")
            + " allow_rename=" + (tables.NameRulesTable.AllowRename ? "true" : "false")
            + " min_length=" + tables.NameRulesTable.MinLength
            + " max_length=" + tables.NameRulesTable.MaxLength
            + " rename_item_id=" + tables.NameRulesTable.RenameItemId
            + " banned_words=" + tables.NameRulesTable.BannedCount
            + " banned_words_file=" + tables.NameRulesTable.BannedWordsFile);

        // P3-4a2：通行证两轨的事实值。改配置重启后这里能直接核对到：
        // 标价来自 shop.json 那条 season_pass 商品，保护券就是 enhance.json 那一件。
        Console.WriteLine("season_pass=version=" + tables.SeasonPassTable.Version
            + " pending=" + (tables.SeasonPassTable.Pending ? "true" : "false")
            + " seasons=" + tables.SeasonPassTable.Seasons.Count
            + " product=" + tables.SeasonPassTable.ProductId
            + " price=" + tables.SeasonPassTable.PriceYuanbao
            + " protect=" + tables.SeasonPassTable.ProtectItemId
            + " protect_per_stage_max=" + tables.SeasonPassTable.ProtectPerStageMax);
        foreach (var pass in tables.SeasonPassTable.Seasons)
        {
            Console.WriteLine("season_pass_season=" + pass.SeasonId
                + " free=" + pass.Free.Count
                + " paid=" + pass.Paid.Count
                + " free_items=" + string.Join("+", pass.Free.Select(x => x.Rewards[0].ItemId))
                + " paid_items=" + string.Join("+", pass.Paid.Select(x => x.Rewards[0].ItemId)));
        }

        // P3-4b：月叠的事实值。改配置重启后这里能直接核对到：只两种符、每天几张、
        // 共几天、总张数，价格来自 shop.json 那条 monthly_bundle 商品。
        Console.WriteLine("monthly_bundle=version=" + tables.MonthlyBundleTable.Version
            + " pending=" + (tables.MonthlyBundleTable.Pending ? "true" : "false")
            + " product=" + tables.MonthlyBundleTable.ProductId
            + " price=" + tables.MonthlyBundleTable.PriceYuanbao
            + " days=" + tables.MonthlyBundleTable.Days
            + " kinds=" + tables.MonthlyBundleTable.Entries.Count);
        foreach (var entry in tables.MonthlyBundleTable.Entries)
        {
            Console.WriteLine("monthly_bundle_item=" + entry.Kind
                + " item=" + entry.ItemId
                + " per_day=" + entry.PerDay
                + " count=" + entry.Count
                + " bind=always");
        }

        // P3-4b：第二角色栏的事实值。基础位与上限只在这里，名额的事实值在库里那一行。
        Console.WriteLine("character_slot=version=" + tables.CharacterSlotTable.Version
            + " pending=" + (tables.CharacterSlotTable.Pending ? "true" : "false")
            + " product=" + tables.CharacterSlotTable.ProductId
            + " price=" + tables.CharacterSlotTable.PriceYuanbao
            + " base_slots=" + tables.CharacterSlotTable.BaseSlots
            + " max_slots=" + tables.CharacterSlotTable.MaxSlots
            + " table=character_slot");

        // B7+B8：背包扩容的事实值。容量是算出来的：基础格 + 买过的次数 × 步长，到上限封顶。
        Console.WriteLine("bag_capacity=base=" + config.BagSlots
            + " step=" + config.BagExpandStep
            + " max=" + config.BagSlotsMax
            + " product=" + (tables.ShopTable.Items.FirstOrDefault(
                i => i.Mode == DeliveryMode.BagExpand)?.Id ?? ""));

        // B2：品阶与套装的事实值。六档按 order 出，套数与每档套数从这里能一眼对账。
        Console.WriteLine("grade_table_version=" + tables.Grades.Version
            + " grades=" + string.Join(",", tables.Grades.Ordered.Select(g => g.Id))
            + " base_attack=" + string.Join(",", tables.Grades.Ordered.Select(g => g.BaseAttack)));
        Console.WriteLine("set_table_version=" + tables.Sets.Version
            + " sets=" + tables.Sets.All.Count
            + " per_grade=" + string.Join(",", tables.Grades.Ordered.Select(
                g => g.Id + ":" + tables.Sets.All.Count(s => string.Equals(s.Grade, g.Id, StringComparison.Ordinal)))));

        // C1：十二场内容只在这里列一遍事实值，定词正文仍只在 config/dialogue。
        Console.WriteLine("mainline_config=" + (tables.MainlineTable.IsMissing ? "missing" : "ok")
            + " stages=" + tables.MainlineTable.Stages.Count
            + " pending=" + (tables.MainlineTable.Pending ? "true" : "false")
            + " battle_dungeon=" + tables.MainlineTable.BattleDungeon);
        Console.WriteLine("recipe_table_version=" + tables.RecipeTable.Version
            + " recipes=" + tables.RecipeTable.Recipes.Count
            + " professions=" + tables.RecipeTable.Professions.Count
            + " pending=" + (tables.RecipeTable.Pending ? "true" : "false"));
        foreach (var profession in tables.RecipeTable.Professions)
        {
            Console.WriteLine("recipe_profession=" + profession.Id
                + " name=" + profession.Name
                + " recipes=" + tables.RecipeTable.ForProfession(profession.Id).Count);
        }
        // Q5/J2：城池任务表的事实值。数值全在 config/city_tasks.json，这里只列出来，
        // 与 --city-task-config 的只读入口同一个键名、同一组字段。
        Console.WriteLine("city_task_config=" + (tables.CityTaskTable.Tasks.Count == 0 ? "missing" : "ok")
            + " version=" + tables.CityTaskTable.Version
            + " pending=" + (tables.CityTaskTable.Pending ? "true" : "false")
            + " tasks=" + tables.CityTaskTable.Tasks.Count
            + " stone_tiers=" + string.Join("+", tables.CityTaskTable.Tasks
                .SelectMany(t => t.Rewards).Select(r => r.Currency).Distinct()));
        foreach (var task in tables.CityTaskTable.Tasks)
        {
            Console.WriteLine("city_task_row=" + task.Id
                + " city=" + task.CityId
                + " period=" + task.Period
                + " max=" + task.MaxPerPeriod
                + " level=" + task.RequireLevel
                + " realm=" + (task.RequireRealm.Length == 0 ? "-" : task.RequireRealm)
                + " rewards=" + string.Join("+", task.Rewards.Select(r => r.Currency + ":" + r.Amount)));
        }

        Console.WriteLine("hero_codex=version=" + tables.HeroCodexTable.Version
            + " pending=" + (tables.HeroCodexTable.Pending ? "true" : "false")
            + " kind=" + tables.HeroCodexTable.PassiveKind
            + " entries=" + tables.HeroCodexTable.Entries.Count
            + " max_total_passive_pct_bps=" + tables.HeroCodexTable.MaxTotalPassivePctBps);
        // B4：坐骑两档 / 灵兽三档的现值，与物品表、图鉴上限的对账结果在加载期已经过一遍。
        Console.WriteLine("mount_table=version=" + tables.MountTable.Version
            + " pending=" + (tables.MountTable.Pending ? "true" : "false")
            + " tiers=" + tables.MountTable.Tiers.Count
            + " entries=" + tables.MountTable.Entries.Count
            + " speeds=" + string.Join("+", tables.MountTable.Entries.Select(e => e.ItemId + ":" + e.SpeedBps)));
        Console.WriteLine("spirit_beast=version=" + tables.SpiritBeastTable.Version
            + " pending=" + (tables.SpiritBeastTable.Pending ? "true" : "false")
            + " kind=" + tables.SpiritBeastTable.PassiveKind
            + " tiers=" + tables.SpiritBeastTable.Tiers.Count
            + " entries=" + tables.SpiritBeastTable.Entries.Count
            + " life_assist_total=" + tables.SpiritBeastTable.TotalLifeAssistPctBps
            + " codex_total=" + tables.SpiritBeastTable.HeroCodexPassiveBps
            + " cap=" + tables.SpiritBeastTable.MaxTotalLifeAssistPctBps);
        // B6：觉醒门槛与分支覆盖。min_level 已与 exp.json 的 level_cap 对齐过，
        // 这里只把结果打出来；技能编号的合法性由战斗服启动期按 skills.json 再验一次。
        Console.WriteLine("awaken_table=version=" + tables.AwakenTable.Version
            + " pending=" + (tables.AwakenTable.Pending ? "true" : "false")
            + " min_level=" + tables.AwakenTable.MinLevel
            + " gold_cost=" + tables.AwakenTable.GoldCost
            + " branches=" + tables.AwakenTable.BranchSkills.Count
            + " skills=" + string.Join("+", tables.AwakenTable.BranchSkills
                .Select(row => row.BranchId + ":" + row.SkillId)));
        foreach (var stage in tables.MainlineTable.Stages)
        {
            Console.WriteLine("mainline_stage=" + stage.Stage
                + " title=" + stage.Title
                + " level_min=" + stage.LevelMin
                + " requires_breakthrough=" + stage.RequiresBreakthrough
                + " alignment_choice=" + (stage.AlignmentChoice ? "true" : "false")
                + " battle=" + stage.Battle);
        }

        Console.WriteLine("dungeon_drops=" + (tables.DungeonDropTable.IsMissing ? "missing" : "ok")
            + " version=" + tables.DungeonDropTable.Version
            + " dungeons=" + tables.DungeonDropTable.DungeonCount);
        Console.WriteLine("mainline_drops=" + (tables.MainlineDropTable.IsMissing ? "missing" : "ok")
            + " version=" + tables.MainlineDropTable.Version
            + " stages=" + tables.MainlineDropTable.StageCount
            + " pending=" + (tables.MainlineDropTable.Pending ? "true" : "false"));

        // 城池只登记静态定义（编号 / 名字 / 矿脉）；归属与税率是库里的运行时事实（技术架构 6.1）。
        // 城池税收账本与拍卖销毁税是分开的两张表。
        Console.WriteLine("task_grant_city=" + tables.CityTable.TaskGrantCityId);
        foreach (var city in tables.CityTable.Cities)
        {
            Console.WriteLine("city=" + city.CityId
                + " vein=" + city.VeinId
                + " owner_sect=db"
                + " tax_pct=db"
                + " city_tax_table=社交.city_tax_ledger"
                + " auction_tax_table=交易.auction_settlement.tax_gold");
        }
    }
}
