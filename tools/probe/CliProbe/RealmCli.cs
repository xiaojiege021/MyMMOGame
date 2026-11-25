using Npgsql;

using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>境界与主线连库核对：境界 / 突破 / 历练 / 主线进度。</summary>
internal static class RealmCli
{
    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        var dataSource = ctx.DataSource;
        var characters = ctx.Characters;
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
            || realmBreakStateOf != 0 || advanceRealmOf != 0 || settleDrillOf != 0
            || rawRealmPhaseId != 0 || realmPassiveStateOf != 0 || promoteRealmPhaseOf != 0
            || heroCodexStateOf != 0)
        {
            var realmConfig = CharacterConfig.Load(configPath);
            var realmDungeons = DungeonTable.Load(realmConfig.DungeonTable);
            var realmCurrencies = CurrencyTable.Load("config/currency.json");
            var realmTable = RealmTable.Load(realmConfig.RealmTable, realmDungeons, realmCurrencies);
            var realmAdvances = new RealmAdvanceRepository(dataSource, realmTable);
            var realmPhases = new RealmPhaseRepository(dataSource, realmTable);
            var hcMainline = File.Exists(realmConfig.MainlineTable)
                ? MainlineTable.Load(realmConfig.MainlineTable, realmDungeons, realmTable)
                : MainlineTable.Missing();
            var hcPath = string.IsNullOrWhiteSpace(heroCodexTablePath)
                ? "config/hero_codex.json"
                : heroCodexTablePath!;
            var hcTable = HeroCodexTable.Load(hcPath, hcMainline);
            var heroCodexRepo = new HeroCodexRepository(dataSource, hcTable);

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
                        + " realm_phase=" + state.Value.RealmPhase
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
                    + " success_pct=" + outcome.SuccessPct
                    // B1：境界丹——配置里的编号与枚数，加扣完的余额（事实值）。
                    + " pill=" + (outcome.PillKind.Length == 0 ? "-" : outcome.PillKind)
                    + " pill_count=" + outcome.PillCount
                    + " pill_left=" + outcome.PillLeft);
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

            if (rawRealmPhaseId != 0)
            {
                try
                {
                    await realmPhases.RawWritePhaseAsync(
                        rawRealmPhaseId, rawRealmPhaseValue, CancellationToken.None);
                    Console.WriteLine("raw_realm_phase=written id=" + rawRealmPhaseId
                        + " phase=" + rawRealmPhaseValue);
                }
                catch (PostgresException ex) when (ex.SqlState == "23514")
                {
                    Console.WriteLine("raw_realm_phase=rejected id=" + rawRealmPhaseId
                        + " sqlstate=" + ex.SqlState);
                }
            }

            if (realmPassiveStateOf != 0)
            {
                var snap = await realmPhases.DescribePassiveAsync(
                    realmPassiveStateOf, CancellationToken.None);
                if (!snap.Found)
                {
                    Console.WriteLine("realm_passive_state=missing id=" + realmPassiveStateOf);
                }
                else
                {
                    Console.WriteLine("realm_passive_state=ok id=" + realmPassiveStateOf
                        + " realm=" + snap.Realm
                        + " realm_phase=" + snap.Phase
                        + " phase_name=" + snap.PhaseName
                        + " passive_pct_bps=" + snap.PassivePctBps
                        + " attr_points=" + snap.AttrPoints);
                }
            }

            if (heroCodexStateOf != 0)
            {
                var snap = await heroCodexRepo.DescribeAsync(
                    heroCodexStateOf, null, CancellationToken.None);
                if (!snap.Found)
                {
                    Console.WriteLine("hero_codex_state=missing id=" + heroCodexStateOf);
                }
                else
                {
                    Console.WriteLine("hero_codex_state=ok id=" + heroCodexStateOf
                        + " main_stage=" + snap.MainStage
                        + " collected=" + snap.CollectedCount
                        + "/" + snap.EntryCount
                        + " collection_pct_bps=" + snap.CollectionPctBps
                        + " total_passive_pct_bps=" + snap.TotalPassivePctBps
                        + " passive_kind=" + snap.PassiveKind);
                }
            }

            if (promoteRealmPhaseOf != 0)
            {
                var outcome = await realmPhases.PromoteAsync(
                    promoteRealmPhaseOf, null, promoteRealmPhaseTxn!, CancellationToken.None);
                Console.WriteLine("promote_realm_phase=done id=" + promoteRealmPhaseOf
                    + " executed=" + (outcome.Executed ? "true" : "false")
                    + " duplicate=" + (outcome.Duplicate ? "true" : "false")
                    + " from=" + outcome.FromPhase
                    + " to=" + outcome.ToPhase
                    + " phase_name=" + outcome.PhaseName
                    + " passive_pct_bps=" + outcome.PassivePctBps
                    + " reason=" + outcome.Reason
                    + " level=" + outcome.Level
                    + " level_required=" + outcome.LevelRequired
                    + " points_awarded=" + outcome.PointsAwarded
                    + " attr_points=" + outcome.AttrPoints);
            }
        }

        return null;
    }

}