using Grpc.Core;
using Mmo.Proto.Character;

namespace Character;

/// <summary>
/// B11 竞技场赛季段位四条口。分工：
///   · 记分由战斗服报（一场通关一个人一个幂等号），加多少分、落哪一档全由角色服
///     按 config/arena_rank.json 现取，请求里带不进分数、档位与季；
///   · 季一律向全局服务确认（ISeasonSource），角色服不自己认定当前是哪一季；
///   · 专属外观要单独领：门槛只认库里记下的分数，同一季同一档只领一次；
///   · 排行只读，取前 N 名（N 在配置里），名字与职业取角色表事实值。
/// 这一组口一个字都不碰竞技场模板属性（P2-3a「换装不改变模板属性」一字不变）。
/// </summary>
public sealed partial class CharacterGrpcService
{
    public override async Task<ReportArenaResultResponse> ReportArenaResult(
        ReportArenaResultRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var characterId = (long)request.CharacterId;
        var account = (request.Account ?? "").Trim();

        // 服间直连（战斗服）不带账号，只按角色号找；网关转发时才有账号，要再核一次归属。
        if (account.Length > 0
            && !await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var season = await seasonSource.CurrentSeasonAsync(context.CancellationToken);
        if (season is null)
        {
            return new ReportArenaResultResponse { Ok = false, Reason = "season_unavailable" };
        }

        var award = await arenaRanks.ReportAsync(
            characterId, season, request.TxnId ?? "", request.Mode ?? "", request.Outcome ?? "",
            context.CancellationToken);

        if (award.Ok)
        {
            logger.LogInformation(
                "arena_rank_award id={Id} season={Season} mode={Mode} delta={Delta}"
                + " score={Score} tier={Tier} tier_name={TierName} tier_up={TierUp}"
                + " wins={Wins} txn={Txn}",
                characterId, award.SeasonId, request.Mode ?? "", award.ScoreDelta,
                award.Score, award.TierKey, award.TierName,
                award.TierUp ? "true" : "false", award.Wins, request.TxnId ?? "");
        }
        else
        {
            logger.LogInformation(
                "arena_rank_award_reject id={Id} season={Season} reason={Reason}"
                + " duplicate={Duplicate} txn={Txn}",
                characterId, award.SeasonId, award.Reason,
                award.Duplicate ? "true" : "false", request.TxnId ?? "");
        }

        return new ReportArenaResultResponse
        {
            Ok = award.Ok,
            Reason = award.Reason,
            SeasonId = award.SeasonId,
            Score = award.Score,
            ScoreDelta = award.ScoreDelta,
            TierKey = award.TierKey,
            TierName = award.TierName,
            TierUp = award.TierUp,
            Wins = award.Wins,
            Duplicate = award.Duplicate,
        };
    }

    public override async Task<DescribeArenaRankResponse> DescribeArenaRank(
        DescribeArenaRankRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var characterId = (long)request.CharacterId;
        var season = await seasonSource.CurrentSeasonAsync(context.CancellationToken);
        if (season is null)
        {
            return new DescribeArenaRankResponse { Ok = false, Reason = "season_unavailable" };
        }

        var state = await arenaRanks.StateAsync(characterId, season, context.CancellationToken);

        var reply = new DescribeArenaRankResponse
        {
            Ok = true,
            SeasonId = state.SeasonId,
            Score = state.Score,
            TierKey = state.TierKey,
            TierName = state.TierName,
            Wins = state.Wins,
            NextTierKey = state.NextTierKey,
            NextTierMinScore = state.NextTierMinScore,
        };
        foreach (var tier in state.Tiers)
        {
            reply.Tiers.Add(new ArenaRankTierFact
            {
                Key = tier.Key,
                Name = tier.Name,
                MinScore = tier.MinScore,
                AppearanceItemId = tier.ItemId,
                Reached = tier.Reached,
                Claimed = tier.Claimed,
            });
        }

        logger.LogInformation(
            "arena_rank id={Id} season={Season} score={Score} tier={Tier} wins={Wins}"
            + " next={Next} tiers={Tiers}",
            characterId, state.SeasonId, state.Score, state.TierKey, state.Wins,
            state.NextTierKey, state.Tiers.Count);

        return reply;
    }

    public override async Task<ClaimArenaAppearanceResponse> ClaimArenaAppearance(
        ClaimArenaAppearanceRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var characterId = (long)request.CharacterId;
        var account = (request.Account ?? "").Trim();
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var season = await seasonSource.CurrentSeasonAsync(context.CancellationToken);
        if (season is null)
        {
            return new ClaimArenaAppearanceResponse { Ok = false, Reason = "season_unavailable" };
        }

        var claim = await arenaRanks.ClaimAsync(
            characterId, season, request.TierKey ?? "", request.TxnId ?? "",
            context.CancellationToken);

        if (claim.Ok)
        {
            logger.LogInformation(
                "arena_appearance_claim account={Account} id={Id} season={Season} tier={Tier}"
                + " item={Item} slot={Slot} bound=true txn={Txn}",
                account, characterId, claim.SeasonId, claim.TierKey, claim.ItemId,
                claim.Slot, request.TxnId ?? "");
        }
        else
        {
            logger.LogInformation(
                "arena_appearance_claim_reject account={Account} id={Id} season={Season}"
                + " tier={Tier} reason={Reason} duplicate={Duplicate}",
                account, characterId, claim.SeasonId, claim.TierKey, claim.Reason,
                claim.Duplicate ? "true" : "false");
        }

        return new ClaimArenaAppearanceResponse
        {
            Ok = claim.Ok,
            Reason = claim.Reason,
            SeasonId = claim.SeasonId,
            TierKey = claim.TierKey,
            ItemId = claim.ItemId,
            BagSlot = (uint)claim.Slot,
            Duplicate = claim.Duplicate,
        };
    }

    public override async Task<DescribeArenaLeaderboardResponse> DescribeArenaLeaderboard(
        DescribeArenaLeaderboardRequest request, ServerCallContext context)
    {
        var season = await seasonSource.CurrentSeasonAsync(context.CancellationToken);
        if (season is null)
        {
            return new DescribeArenaLeaderboardResponse { Ok = false, Reason = "season_unavailable" };
        }

        var (seasonId, rows) = await arenaRanks.LeaderboardAsync(
            season, (int)request.Limit, context.CancellationToken);

        var reply = new DescribeArenaLeaderboardResponse
        {
            Ok = true,
            SeasonId = seasonId,
            Limit = (uint)(request.Limit == 0
                ? arenaRankTable.TopN
                : Math.Min((int)request.Limit, arenaRankTable.TopN)),
        };
        foreach (var row in rows)
        {
            // 档位名从配置现取，库里只存分数与编号。
            var tier = arenaRankTable.TierFor(row.Score);
            reply.Entries.Add(new ArenaLeaderboardEntry
            {
                Rank = row.Rank,
                CharacterId = (ulong)row.CharacterId,
                Name = row.Name,
                ClassId = row.ClassId,
                Score = row.Score,
                TierKey = tier?.Key ?? "",
                TierName = tier?.Name ?? "",
            });
        }

        logger.LogInformation(
            "arena_leaderboard season={Season} limit={Limit} rows={Rows}",
            seasonId, reply.Limit, rows.Count);

        return reply;
    }
}
