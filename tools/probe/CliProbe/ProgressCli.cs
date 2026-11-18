using Npgsql;

using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>角色进度连库核对：等级 / 分支 / 装备资格 / 绑定 / 货币 / 背包 / 疲劳 / 周份额 / 经验。</summary>
internal static class ProgressCli
{
    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        var dataSource = ctx.DataSource;
        var characters = ctx.Characters;
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

        if (rawCurrencyAddId != 0 && rawCurrencyAddKind != null)
        {
            var money = new CurrencyRepository(dataSource);
            var amount = await money.AddAsync(
                rawCurrencyAddId, rawCurrencyAddKind, rawCurrencyAddDelta, CancellationToken.None);
            Console.WriteLine(amount is null
                ? "raw_currency_add=rejected id=" + rawCurrencyAddId
                  + " kind=" + rawCurrencyAddKind + " delta=" + rawCurrencyAddDelta
                : "raw_currency_add=ok id=" + rawCurrencyAddId
                  + " kind=" + rawCurrencyAddKind + " delta=" + rawCurrencyAddDelta
                  + " amount=" + amount);
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
            var hardEntries = new HardEntryRepository(
                dataSource, LoadCalendar(CharacterConfig.Load(configPath), null));

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
            var dungeonEntries = new DungeonEntryRepository(
                dataSource, LoadCalendar(CharacterConfig.Load(configPath), null));

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
            var weeklyShares = new WeeklyShareRepository(
                dataSource, LoadCalendar(CharacterConfig.Load(configPath), null));

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

        return null;
    }

}