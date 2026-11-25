using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// B11：竞技场赛季段位的核对入口。
///   · 只读不连库：--arena-rank-config（表现值）、--arena-rank-table-path（指到临时表试坏配置）。
///   · 连库：--arena-rank-award &lt;角色号&gt; &lt;模式&gt; &lt;季&gt; &lt;事务号&gt;（记一场胜）、
///     --arena-rank-state &lt;角色号&gt; &lt;季&gt;（只读分数与档位）、
///     --arena-rank-top &lt;季&gt; [名次]（只读排行）。
/// 走的是起服同一套 ArenaRankTable.Load 与同一个 ArenaRankRepository，探针不另写阈值或加分。
/// 季由调用方显式给：探针是核对工具，不去拉起全局服务。
/// </summary>
internal static class ArenaCli
{
    public static bool Handles(string flag) => flag is
        "--arena-rank-config" or "--arena-rank-table-path" or "--arena-rank-award"
        or "--arena-rank-state" or "--arena-rank-claim" or "--arena-rank-top";

    public static int Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--arena-rank-config":
                arenaRankConfig = true;
                return 0;
            case "--arena-rank-table-path" when i + 1 < args.Length:
                arenaRankTablePath = args[++i];
                return 0;
            case "--arena-rank-award" when i + 4 < args.Length:
                long.TryParse(args[++i], out arenaRankOf);
                arenaRankMode = args[++i];
                arenaRankSeason = args[++i];
                arenaRankTxn = args[++i];
                return 0;
            case "--arena-rank-claim" when i + 4 < args.Length:
                long.TryParse(args[++i], out arenaRankClaimOf);
                arenaRankSeason = args[++i];
                arenaRankTierKey = args[++i];
                arenaRankTxn = args[++i];
                return 0;
            case "--arena-rank-state" when i + 2 < args.Length:
                long.TryParse(args[++i], out arenaRankStateOf);
                arenaRankSeason = args[++i];
                return 0;
            case "--arena-rank-top" when i + 1 < args.Length:
                arenaRankTopSeason = args[++i];
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out _))
                {
                    int.TryParse(args[++i], out arenaRankTopLimit);
                }
                return 0;
            default:
                return 64;
        }
    }

    public static int? TryReadOnly()
    {
        if (!arenaRankConfig && arenaRankTablePath is null)
        {
            return null;
        }

        try
        {
            var cfg = CharacterConfig.Load(configPath);
            var table = LoadTable(cfg);
            Console.WriteLine("arena_rank_config=ok"
                + " version=" + table.Version
                + " pending=" + (table.Pending ? "true" : "false")
                + " win_points=" + table.WinPoints
                + " top_n=" + table.TopN
                + " tiers=" + table.Tiers.Count);
            foreach (var tier in table.Tiers)
            {
                Console.WriteLine("arena_rank_row=key=" + tier.Key
                    + " name=" + tier.Name
                    + " min_score=" + tier.MinScore
                    + " item=" + tier.AppearanceItemId);
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

    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        if (arenaRankOf == 0 && arenaRankStateOf == 0 && arenaRankClaimOf == 0
            && arenaRankTopSeason is null)
        {
            return null;
        }

        var cfg = CharacterConfig.Load(configPath);
        var items = ItemTable.Load("config/items.json");
        var table = LoadTable(cfg);
        var repo = new ArenaRankRepository(ctx.DataSource, table, items, cfg);

        if (arenaRankOf != 0)
        {
            var award = await repo.ReportAsync(
                arenaRankOf, arenaRankSeason ?? "", arenaRankTxn ?? "",
                arenaRankMode ?? "", "win", CancellationToken.None);
            Console.WriteLine("arena_rank_award=" + (award.Ok ? "ok" : "reject")
                + " reason=" + Stamp(award.Reason)
                + " season=" + Stamp(award.SeasonId)
                + " score=" + award.Score
                + " delta=" + award.ScoreDelta
                + " tier=" + Stamp(award.TierKey)
                + " tier_up=" + (award.TierUp ? "true" : "false")
                + " wins=" + award.Wins
                + " duplicate=" + (award.Duplicate ? "true" : "false"));
        }

        if (arenaRankStateOf != 0)
        {
            var state = await repo.StateAsync(
                arenaRankStateOf, arenaRankSeason ?? "", CancellationToken.None);
            Console.WriteLine("arena_rank_state=ok"
                + " season=" + Stamp(state.SeasonId)
                + " score=" + state.Score
                + " tier=" + Stamp(state.TierKey)
                + " tier_name=" + Stamp(state.TierName)
                + " wins=" + state.Wins
                + " next=" + Stamp(state.NextTierKey)
                + " next_min=" + state.NextTierMinScore
                + " tiers=" + state.Tiers.Count);
            foreach (var tier in state.Tiers)
            {
                Console.WriteLine("arena_rank_tier=key=" + tier.Key
                    + " reached=" + (tier.Reached ? "true" : "false")
                    + " claimed=" + (tier.Claimed ? "true" : "false")
                    + " item=" + tier.ItemId);
            }
        }

        if (arenaRankClaimOf != 0)
        {
            var claim = await repo.ClaimAsync(
                arenaRankClaimOf, arenaRankSeason ?? "", arenaRankTierKey ?? "",
                arenaRankTxn ?? "", CancellationToken.None);
            Console.WriteLine("arena_rank_claim=" + (claim.Ok ? "ok" : "reject")
                + " reason=" + Stamp(claim.Reason)
                + " season=" + Stamp(claim.SeasonId)
                + " tier=" + Stamp(claim.TierKey)
                + " item=" + Stamp(claim.ItemId)
                + " slot=" + claim.Slot
                + " duplicate=" + (claim.Duplicate ? "true" : "false"));
        }

        if (arenaRankTopSeason is not null)
        {
            var (season, rows) = await repo.LeaderboardAsync(
                arenaRankTopSeason, arenaRankTopLimit, CancellationToken.None);
            Console.WriteLine("arena_rank_top=ok"
                + " season=" + Stamp(season)
                + " rows=" + rows.Count
                + " limit=" + (arenaRankTopLimit <= 0 ? table.TopN : arenaRankTopLimit));
            foreach (var row in rows)
            {
                Console.WriteLine("arena_rank_top_row=rank=" + row.Rank
                    + " character=" + row.CharacterId
                    + " name=" + row.Name
                    + " score=" + row.Score);
            }
        }

        return 0;
    }

    /// <summary>与起服同一条链：物品表 → 段位表（外观编号与物品表交叉校验）。</summary>
    static ArenaRankTable LoadTable(CharacterConfig cfg)
    {
        var items = ItemTable.Load("config/items.json");
        return ArenaRankTable.Load(arenaRankTablePath ?? cfg.ArenaRankTable, items);
    }

    /// <summary>空串在日志里会吞掉后面的字段，统一写 none。</summary>
    static string Stamp(string value) => value.Length == 0 ? "none" : value;
}
