using Npgsql;

using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>连库核对的总入口：先判有没有连库 flag，再按域分发；开库与错误码沿用拆分前。</summary>
internal static class DbCli
{
    public static async Task<int?> TryRunAsync()
    {
        if (!AnyDbFlag())
        {
            return null;
        }

        var connectionString = DbEnv.Resolve();
        if (connectionString == null)
        {
            Console.WriteLine("db_env=missing 需要 " + DbEnv.EnvName + " 或 " + DbEnv.DefaultEnvFile);
            return 3;
        }

        Console.WriteLine("db_env=" + DbEnv.Source());
        await using var dataSource = NpgsqlDataSource.Create(DbEnv.WithPoolLimit(connectionString, 2));
        var characters = new CharacterRepository(dataSource);
        var ctx = new CliContext(dataSource, characters);

        try
        {
            int? code;
            code = await CoreCli.TryRunDbAsync(ctx);
            if (code != null) { return code; }

            code = await ProgressCli.TryRunDbAsync(ctx);
            if (code != null) { return code; }

            code = await RealmCli.TryRunDbAsync(ctx);
            if (code != null) { return code; }

            code = await EnhanceCli.TryRunDbAsync(ctx);
            if (code != null) { return code; }

            code = await ShopCli.TryRunDbAsync(ctx);
            if (code != null) { return code; }

            code = await MetaCli.TryRunDbAsync(ctx);
            if (code != null) { return code; }

            code = await EconomyCli.TryRunDbAsync(ctx);
            if (code != null) { return code; }

        }
        catch (Exception ex) when (ex is NpgsqlException or IOException)
        {
            Console.WriteLine("db_error=" + ex.GetType().Name + " " + ex.Message);
            return 5;
        }

        return 0;
    }

    private static bool AnyDbFlag()
    {
        return reportName != null || deleteName != null || stats || migrate || rawLevelWrite != 0 || levelOf != 0
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
    || rawRealmPhaseId != 0 || realmPassiveStateOf != 0 || promoteRealmPhaseOf != 0
    || heroCodexStateOf != 0
    || exchangeStoneOf != 0 || stoneExchangeStateOf != 0 || cityState || cityTaxRowTxn != null
    || stoneListingState
    || siegeResultState || applySiegeResultTxn != null
    || seasonPassStateOf != 0 || buySeasonPassOf != 0 || claimSeasonPassOf != 0
    || characterSlotsOf != null || wardrobeOf != null || rawSlotAccount != null
    || mainlineStateOf != 0 || mainlineTurnInOf != 0 || mainlineBreakthroughOf != 0
    || mainlineSeedSettlementOf != 0;
    }
}
