using Npgsql;

using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>拍卖 / 红符 / 复活卷轴 / 限时经验 / 解绑 / 高难次数核对。</summary>
internal static class MetaCli
{
    public static int? TryReadOnly()
    {
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

        return null;
    }

    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        var dataSource = ctx.DataSource;
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

        return null;
    }

}