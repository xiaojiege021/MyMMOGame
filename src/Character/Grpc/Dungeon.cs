using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

    /// <summary>
    /// 进本。人数上限与「扣不扣疲劳、占不占份额」都来自 config/dungeons.json。
    /// 主线 1 人、不扣不占（P1-2a 的路径）；扣疲劳的本（深渊）按人数倍增，
    /// 到日上限就整笔拒绝；占周份额的本（巢穴）按人数扣份额、按人数记产出份数，
    /// 到周上限整笔拒绝。两种扣减与进本记录各在一个事务里。
    /// 战斗服那边还有一道同样的人数上限（它按 party 开本），两道都挡。
    /// 旧副本只看等级；写了 realm 的新副本（元婴起）还要这一段已经开放、
    /// 个人境界也不低于门槛，判定与「能不能突破」共用 RealmTable。
    /// P2-2a 的高难本（龙巢）另加一条：人数不足回 party_too_small，
    /// 本周次数用完由 HardEntryRepository 回 hard_entries_exhausted，两条都不留进本记录。
    /// </summary>
    public override async Task<EnterDungeonResponse> EnterDungeon(
        EnterDungeonRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var dungeon = dungeons.Find((request.DungeonId ?? "").Trim());
        if (dungeon is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown_dungeon"));
        }

        var partySize = (int)request.PartySize;
        if (partySize < 1)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "party_size_invalid"));
        }

        var characterId = (long)request.CharacterId;
        var reserves = await characters.ReservesAsync(characterId, context.CancellationToken);
        if (reserves is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        // Q9/H4：逃跑惩罚。挡在所有扣减之前：不扣疲劳、不扣周份额、不写高难本次数，
        // 回包里的 cost 全是 0，只剩 remaining_s 给客户端提示「还要等几秒」。
        var deserterFact = await deserters.DescribeAsync(characterId, context.CancellationToken);
        if (deserterFact.RemainingMs > 0)
        {
            logger.LogInformation(
                "dungeon_reject account={Account} id={Id} dungeon={Dungeon} reason=deserter_penalty "
                + "remaining_ms={Remaining}",
                account, characterId, dungeon.Id, deserterFact.RemainingMs);

            var blocked = Rejected(
                "deserter_penalty", dungeon, reserves.Value.Fatigue, reserves.Value.WeeklyShare);
            blocked.DeserterRemainingS = (uint)Math.Ceiling(deserterFact.RemainingMs / 1000.0);
            return blocked;
        }

        // P3-1a1：境界从库里读，客户端带进来的字段一概不认。
        // 旧副本没写 realm，这一步直接放行；写了 realm 的新副本先看这一段开没开，
        // 再看个人境界够不够，关着时谁都进不去（包括已经到元婴的人）。
        var realm = await inventory.RealmAsync(account, characterId, context.CancellationToken);
        var gate = realms.CheckDungeon(dungeon.Realm, realm);
        if (!gate.Ok)
        {
            logger.LogInformation(
                "dungeon_reject account={Account} id={Id} dungeon={Dungeon} reason={Reason} "
                + "realm={Realm} required={Required}",
                account, characterId, dungeon.Id, gate.Reason, gate.Realm, gate.Required);

            return Rejected(
                gate.Reason, dungeon, reserves.Value.Fatigue, reserves.Value.WeeklyShare);
        }

        // Q7/H1：人数以社交服的**队伍事实**为准，客户端自报的 party_size 只作提示。
        // 没有队伍就是 1 人（不多给组队加成）；有队伍就是队里实际人数，并且必须
        // 全员已准备——「进本时队伍一致」挡在这里，不靠客户端自觉。
        // 社交服不可达按 1 人（只少给不多给），不阻塞进场。
        var partyFact = await PartyDirectory.DescribeAsync(
            social, request.CharacterId, context.CancellationToken);
        partySize = partyFact.Size;
        if (partyFact.InParty && !partyFact.AllReady)
        {
            logger.LogInformation(
                "dungeon_reject account={Account} id={Id} dungeon={Dungeon} party={Party} "
                + "size={Size} reason=party_not_ready",
                account, characterId, dungeon.Id, partyFact.PartyId, partyFact.Size);

            return Rejected(
                "party_not_ready", dungeon, reserves.Value.Fatigue, reserves.Value.WeeklyShare);
        }

        // 人数超上限：不是错误码能表达的「业务不允许」，回 allowed=false 让客户端提示。
        if (partySize > dungeon.MaxPlayers)
        {
            logger.LogInformation(
                "dungeon_reject account={Account} id={Id} dungeon={Dungeon} party={Party} limit={Limit}",
                account, characterId, dungeon.Id, partySize, dungeon.MaxPlayers);

            return Rejected(
                "party_too_large", dungeon, reserves.Value.Fatigue, reserves.Value.WeeklyShare);
        }

        // 人数不足下限：高难本（龙巢）至少 2 人，普通本下限是 1、走不到这里。
        // 不扣次数、不留进本记录，回包带上配置里的下限供客户端提示。
        if (partySize < dungeon.MinPlayers)
        {
            logger.LogInformation(
                "dungeon_reject account={Account} id={Id} dungeon={Dungeon} party={Party} min={Min}",
                account, characterId, dungeon.Id, partySize, dungeon.MinPlayers);

            return Rejected(
                "party_too_small", dungeon, reserves.Value.Fatigue, reserves.Value.WeeklyShare);
        }

        // 高难本（龙巢）：每人每周固定次数，进一次扣 1 次，产出就是配置里的那 1 份，
        // 不乘进本人数。次数以角色库里已经写下的进本记录为准，只写 dungeon_entry 一行。
        if (dungeon.IsHard)
        {
            var hard = await hardEntries.EnterHardAsync(
                characterId, account, dungeon, partySize, context.CancellationToken);
            if (hard.Reason == "character_not_found")
            {
                throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
            }

            logger.LogInformation(
                "dungeon_enter account={Account} id={Id} dungeon={Dungeon} party={Party} "
                + "allowed={Allowed} reason={Reason} hard_used={Used} hard_cap={Cap} "
                + "hard_remaining={Remaining} reward_shares={Shares} entry={Entry}",
                account, characterId, dungeon.Id, partySize,
                hard.Allowed, hard.Reason, hard.Used, hard.Cap,
                hard.Remaining, hard.RewardShares, hard.EntryId);

            return new EnterDungeonResponse
            {
                Allowed = hard.Allowed,
                Reason = hard.Reason,
                MaxPlayers = (uint)dungeon.MaxPlayers,
                MinPlayers = (uint)dungeon.MinPlayers,
                Fatigue = reserves.Value.Fatigue,
                WeeklyShare = reserves.Value.WeeklyShare,
                EntryId = (ulong)hard.EntryId,
                FatigueCost = 0,
                FatigueDailyCap = dungeons.FatigueDailyCap,
                WeeklyShareCost = 0,
                WeeklyShareRemaining = dungeons.WeeklyShareCap - reserves.Value.WeeklyShare,
                WeeklyShareCap = dungeons.WeeklyShareCap,
                RewardShares = hard.RewardShares,
                HardEntriesUsed = hard.Used,
                HardEntriesCap = hard.Cap,
                HardEntriesRemaining = hard.Remaining,
            };
        }

        // 占周份额的本（巢穴）：与疲劳解绑，不扣疲劳，但按人数扣份额、按人数记份数。
        // 单人 1 份，4 人一次 4 份；一周 12 份，扣完这周就不能再进。
        if (dungeon.TakesWeeklyShare)
        {
            var share = await weeklyShares.EnterWithWeeklyShareAsync(
                characterId, account, dungeon, partySize, dungeons.WeeklyShareCap,
                context.CancellationToken);
            if (share.Reason == "character_not_found")
            {
                throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
            }

            logger.LogInformation(
                "dungeon_enter account={Account} id={Id} dungeon={Dungeon} party={Party} "
                + "allowed={Allowed} reason={Reason} share_cost={ShareCost} used={Used} "
                + "remaining={Remaining} reward_shares={Shares} fatigue={Fatigue} entry={Entry}",
                account, characterId, dungeon.Id, partySize,
                share.Allowed, share.Reason, share.Cost, share.Used,
                share.Remaining, share.RewardShares, share.Fatigue, share.EntryId);

            return new EnterDungeonResponse
            {
                Allowed = share.Allowed,
                Reason = share.Reason,
                MaxPlayers = (uint)dungeon.MaxPlayers,
                MinPlayers = (uint)dungeon.MinPlayers,
                Fatigue = share.Fatigue,
                WeeklyShare = share.Used,
                EntryId = (ulong)share.EntryId,
                FatigueCost = 0,
                FatigueDailyCap = dungeons.FatigueDailyCap,
                WeeklyShareCost = share.Cost,
                WeeklyShareRemaining = share.Remaining,
                WeeklyShareCap = share.WeeklyCap,
                RewardShares = share.RewardShares,
                // 巢穴不是高难本：次数三项恒为 0。
                HardEntriesUsed = 0,
                HardEntriesCap = 0,
                HardEntriesRemaining = 0,
            };
        }

        // 不扣疲劳的本（主线）：P1-2a 的路径，一行进本记录，账完全不动。
        if (!dungeon.ChargesFatigue)
        {
            var entryId = await characters.RecordEntryAsync(
                characterId, dungeon.Id, partySize, context.CancellationToken);

            logger.LogInformation(
                "dungeon_enter account={Account} id={Id} dungeon={Dungeon} party={Party} "
                + "charges_fatigue={Charges} takes_share={Share} entry={Entry}",
                account, characterId, dungeon.Id, partySize,
                dungeon.ChargesFatigue, dungeon.TakesWeeklyShare, entryId);

            return new EnterDungeonResponse
            {
                Allowed = true,
                Reason = "",
                MaxPlayers = (uint)dungeon.MaxPlayers,
                MinPlayers = (uint)dungeon.MinPlayers,
                Fatigue = reserves.Value.Fatigue,
                WeeklyShare = reserves.Value.WeeklyShare,
                EntryId = (ulong)entryId,
                FatigueCost = 0,
                FatigueDailyCap = dungeons.FatigueDailyCap,
                WeeklyShareCost = 0,
                WeeklyShareRemaining = dungeons.WeeklyShareCap - reserves.Value.WeeklyShare,
                WeeklyShareCap = dungeons.WeeklyShareCap,
                RewardShares = 0,
                // 主线不是高难本：次数三项恒为 0。
                HardEntriesUsed = 0,
                HardEntriesCap = 0,
                HardEntriesRemaining = 0,
            };
        }

        // 扣疲劳的本（深渊）：cost = 单人基础 × 进本人数。
        // 到上限整笔拒绝；跨天归零、扣账、记进本在一个事务里。
        var outcome = await dungeonEntries.EnterWithFatigueAsync(
            characterId, account, dungeon, partySize, dungeons.FatigueDailyCap,
            context.CancellationToken);
        if (outcome.Reason == "character_not_found")
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "dungeon_enter account={Account} id={Id} dungeon={Dungeon} party={Party} "
            + "allowed={Allowed} reason={Reason} cost={Cost} fatigue={Fatigue} cap={Cap} "
            + "charges_fatigue={Charges} takes_share={Share} entry={Entry}",
            account, characterId, dungeon.Id, partySize,
            outcome.Allowed, outcome.Reason, outcome.FatigueCost, outcome.Fatigue,
            outcome.DailyCap, dungeon.ChargesFatigue, dungeon.TakesWeeklyShare, outcome.EntryId);

        return new EnterDungeonResponse
        {
            Allowed = outcome.Allowed,
            Reason = outcome.Reason,
            MaxPlayers = (uint)dungeon.MaxPlayers,
            MinPlayers = (uint)dungeon.MinPlayers,
            Fatigue = outcome.Fatigue,
            WeeklyShare = outcome.WeeklyShare,
            // 允许进本时是库里的自增编号（> 0），被拒时是 0。
            EntryId = (ulong)outcome.EntryId,
            FatigueCost = outcome.FatigueCost,
            FatigueDailyCap = outcome.DailyCap,
            WeeklyShareCost = 0,
            WeeklyShareRemaining = dungeons.WeeklyShareCap - outcome.WeeklyShare,
            WeeklyShareCap = dungeons.WeeklyShareCap,
            RewardShares = 0,
            // 深渊不是高难本：次数三项恒为 0。
            HardEntriesUsed = 0,
            HardEntriesCap = 0,
            HardEntriesRemaining = 0,
        };
    }


    EnterDungeonResponse Rejected(string reason, DungeonDef dungeon, int fatigue, int weeklyShare) =>
        new()
        {
            Allowed = false,
            Reason = reason,
            MaxPlayers = (uint)dungeon.MaxPlayers,
            MinPlayers = (uint)dungeon.MinPlayers,
            Fatigue = fatigue,
            WeeklyShare = weeklyShare,
            FatigueCost = 0,
            FatigueDailyCap = dungeons.FatigueDailyCap,
            WeeklyShareCost = 0,
            WeeklyShareRemaining = dungeons.WeeklyShareCap - weeklyShare,
            WeeklyShareCap = dungeons.WeeklyShareCap,
            RewardShares = 0,
        };


    /// <summary>
    /// Q8/H2：战斗服结算前读一次的分配口径。队伍、成员、队长与 loot 规则
    /// 全部转社交服拿事实值——请求里只有一个角色号，客户端与战斗服都带不进
    /// 模式、区间或成员。没队伍 / 社交服不可达时按 personal 与 1 人回
    /// （默认口径，只少给不多给：绝不因为查不到就改成「全队一份」）。
    /// </summary>
    public override async Task<DescribeLootScopeResponse> DescribeLootScope(
        DescribeLootScopeRequest request,
        ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        var fact = await PartyDirectory.DescribeAsync(
            social, request.CharacterId, context.CancellationToken);

        return new DescribeLootScopeResponse
        {
            LootMode = fact.LootMode,
            LootRoll = fact.LootRoll,
            RollMin = fact.RollMin,
            RollMax = fact.RollMax,
            LeaderCharacterId = fact.LeaderCharacterId,
            InParty = fact.InParty,
            MemberIds = { fact.MemberIds },
        };
    }


    /// <summary>
    /// 温泉。恢复量从 config/dungeons.json 的 hot_spring_recovery 读（方案写定 1500），
    /// 客户端请求里没有「恢复多少」这个字段，改不了包也改不了数值。
    /// 角色服是持久数据的唯一写入方：网关只转请求，扣疲劳的进本和补疲劳的温泉
    /// 都在这里落库，且共用同一把行锁。
    /// </summary>
    public override async Task<UseHotSpringResponse> UseHotSpring(
        UseHotSpringRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await hotSpring.UseAsync(
            characterId, account, dungeons.HotSpringRecovery, context.CancellationToken);
        if (!outcome.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "hot_spring account={Account} id={Id} before={Before} recovered={Recovered} "
            + "fatigue={Fatigue} cap={Cap} share={Share}",
            account, characterId, outcome.FatigueBefore, outcome.Recovered,
            outcome.Fatigue, dungeons.FatigueDailyCap, outcome.WeeklyShare);

        return new UseHotSpringResponse
        {
            FatigueBefore = outcome.FatigueBefore,
            Recovered = outcome.Recovered,
            Fatigue = outcome.Fatigue,
            FatigueDailyCap = dungeons.FatigueDailyCap,
            RecoveryLimit = dungeons.HotSpringRecovery,
            WeeklyShare = outcome.WeeklyShare,
        };
    }


    /// <summary>
    /// P2-1c：判整个队伍是否同宗门。逐个问社交服 DescribeCharacterSect；
    /// 任一人没进宗门、或跟第一个人的宗门编号不一致，就判 false。
    /// 人数不足 2 直接 false（同门加成只在整队至少两人时算）。member_ids 没带
    /// 也判 false，不臆造同门关系。社交服不可达抛 Unavailable，交给调用方重试。
    /// </summary>
    async Task<bool> ComputeAllSameSectAsync(
        IReadOnlyList<ulong> memberIds,
        CancellationToken stopping)
    {
        if (memberIds.Count < 2)
        {
            return false;
        }

        long? commonSect = null;
        foreach (var memberId in memberIds)
        {
            DescribeCharacterSectResponse sect;
            try
            {
                sect = await social.DescribeCharacterSectAsync(
                    new DescribeCharacterSectRequest { CharacterId = memberId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: stopping);
            }
            catch (RpcException ex)
            {
                // 社交服不可达：按「不是同门」降级，照常结算基础 + 组队加成。
                // 只可能少给那一小截同门加成，绝不会超发，也不阻塞结算本身。
                logger.LogWarning(
                    "exp_settle_same_sect_unavailable member={Member} detail={Detail}",
                    memberId, ex.Status.Detail);
                return false;
            }

            if (!sect.Found)
            {
                return false;
            }

            if (commonSect is null)
            {
                commonSect = (long)sect.SectId;
            }
            else if (commonSect != (long)sect.SectId)
            {
                return false;
            }
        }

        return commonSect.HasValue;
    }
}
