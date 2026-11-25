using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

    // ------------------------------------------------------------------ P3-4b 账号级只读

    /// <summary>
    /// 账号有几个角色位、已经用掉几个。名额是库里的事实值（没有那一行就是配置的基础位），
    /// 上限与基础位只从 config/character_slot.json 读，客户端表达不了也改不动。
    /// </summary>
    public override async Task<DescribeCharacterSlotsResponse> DescribeCharacterSlots(
        DescribeCharacterSlotsRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        var state = await characterSlots.DescribeAsync(account, context.CancellationToken);
        if (!state.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "account_not_found"));
        }

        return new DescribeCharacterSlotsResponse
        {
            Account = account,
            BaseSlots = state.BaseSlots,
            MaxSlots = state.MaxSlots,
            Slots = state.Slots,
            Used = state.Used,
            ProductId = state.ProductId,
            PriceYuanbao = state.PriceYuanbao,
        };
    }


    /// <summary>
    /// 外观收藏：按账号把名下所有角色持有的外观类物品取并集。
    /// 只读、不写库、不改背包归属；装备 / 强化 / 等级 / 境界一条都不返回。
    /// </summary>
    public override async Task<DescribeWardrobeResponse> DescribeWardrobe(
        DescribeWardrobeRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        var (found, entries) = await wardrobe.DescribeAsync(account, context.CancellationToken);
        if (!found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "account_not_found"));
        }

        var response = new DescribeWardrobeResponse { Account = account, Found = true };
        foreach (var entry in entries)
        {
            var item = new WardrobeEntry
            {
                ItemId = entry.ItemId,
                Kind = entry.Kind,
                Name = entry.Name,
            };
            foreach (var characterId in entry.CharacterIds)
            {
                item.CharacterIds.Add((ulong)characterId);
            }

            response.Entries.Add(item);
        }

        return response;
    }


    /// <summary>
    /// 通行证只读状态：这一季两轨每阶发什么、领没领、付费轨开没开。
    /// 季向全局服务确认；奖励物品与数量全从 config/season_pass.json 读。
    /// </summary>
    public override async Task<DescribeSeasonPassResponse> DescribeSeasonPass(
        DescribeSeasonPassRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var season = await seasonSource.CurrentSeasonAsync(context.CancellationToken);
        if (season is null)
        {
            return new DescribeSeasonPassResponse
            {
                Ok = false,
                Reason = "season_unavailable",
                Found = false,
            };
        }

        var pass = seasonPassTable.Find(season);
        if (pass is null)
        {
            // 配置里没有这一季：照实回 found=false，不拿别的季凑一份。
            return new DescribeSeasonPassResponse
            {
                Ok = false,
                Reason = "unknown_season",
                Found = false,
                SeasonId = season,
            };
        }

        var state = await seasonPasses.StateAsync(characterId, season, context.CancellationToken);
        // Q6/J4：本季点数只从库里读一次，阶门槛按「阶号 × points_per_stage」算给界面；
        // 真正的拒绝仍在领取那一笔事务里再判一遍。
        var passPointsNow = await seasonPassPoints.PointsAsync(
            characterId, season, context.CancellationToken);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in state.Claims)
        {
            claimed.Add(row.Track + "#" + row.Stage);
        }

        var reply = new DescribeSeasonPassResponse
        {
            Ok = true,
            Found = true,
            SeasonId = season,
            Paid = state.Paid,
            PriceYuanbao = seasonPassTable.PriceYuanbao,
            ProtectItemId = seasonPassTable.ProtectItemId,
            Points = passPointsNow,
            PointsPerStage = seasonPassProgressTable.PointsPerStage,
        };

        foreach (var stage in pass.Free)
        {
            reply.Rewards.Add(PassReward(
                SeasonPassTable.FreeTrack, stage, claimed, passPointsNow, seasonPassProgressTable));
        }

        foreach (var stage in pass.Paid)
        {
            reply.Rewards.Add(PassReward(
                SeasonPassTable.PaidTrack, stage, claimed, passPointsNow, seasonPassProgressTable));
        }

        return reply;
    }


    static SeasonPassReward PassReward(
        string track, SeasonPassStage stage, HashSet<string> claimed, long points,
        SeasonPassProgressTable progress)
    {
        var reward = stage.Rewards[0];
        var required = progress.RequiredPoints(stage.Stage);
        return new SeasonPassReward
        {
            Track = track,
            Stage = (uint)stage.Stage,
            ItemId = reward.ItemId,
            Count = (uint)reward.Count,
            Claimed = claimed.Contains(track + "#" + stage.Stage),
            RequiredPoints = (uint)required,
            Unlocked = points >= required,
        };
    }


    /// <summary>
    /// 领一阶。物品、数量、绑定全从配置与物品表定；季向全局服务确认。
    /// 付费轨要先买过这一季的通行证；同一事务号重放只回 duplicate，不发第二次。
    /// </summary>
    public override async Task<ClaimSeasonPassResponse> ClaimSeasonPassReward(
        ClaimSeasonPassRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var txn = (request.TxnId ?? "").Trim();
        if (txn.Length == 0 || txn.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        var season = await seasonSource.CurrentSeasonAsync(context.CancellationToken);
        if (season is null)
        {
            return new ClaimSeasonPassResponse { Ok = false, Reason = "season_unavailable" };
        }

        var result = await seasonPasses.ClaimAsync(
            characterId, season, request.Track ?? "", (int)request.Stage, txn,
            context.CancellationToken);

        if (result.Ok)
        {
            logger.LogInformation(
                "season_pass_claim account={Account} id={Id} season={Season} track={Track} stage={Stage}"
                + " item={Item} count={Count} slot={Slot} bound=true txn={Txn}",
                account, characterId, result.SeasonId, result.Track, result.Stage,
                result.ItemId, result.Count, result.Slot, txn);
        }
        else
        {
            logger.LogInformation(
                "season_pass_claim_reject account={Account} id={Id} season={Season} track={Track}"
                + " stage={Stage} reason={Reason} duplicate={Duplicate}",
                account, characterId, result.SeasonId, result.Track, result.Stage,
                result.Reason, result.Duplicate);
        }

        return new ClaimSeasonPassResponse
        {
            Ok = result.Ok,
            Reason = result.Reason,
            SeasonId = result.SeasonId,
            Track = result.Track,
            Stage = (uint)result.Stage,
            ItemId = result.ItemId,
            Count = (uint)result.Count,
            BagSlot = (uint)result.Slot,
            Bound = result.Claimed,
            Duplicate = result.Duplicate,
            Claimed = result.Claimed,
        };
    }


    /// <summary>
    /// P2-3b：战场进场前的只读战斗属性快照。只给事实值——职业、分支、等级、
    /// 各槽强化等级之和与有强化记录的槽数。属性折算（每级强化折多少攻击与生命）、
    /// 有效战力、同队 1.3 倍平衡全在战斗服按 config/battlefield.json 做：
    /// 角色服不参与数值，也不把背包、货币带出去。这里一个字都不写库。
    /// </summary>
    public override async Task<DescribeBattleProfileResponse> DescribeBattleProfile(
        DescribeBattleProfileRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var identity = await characters.BattleIdentityAsync(
            (long)request.CharacterId, context.CancellationToken);
        if (identity is null)
        {
            // 不抛 NotFound：战场拿到 found=false 自己拒进场，比服间 502 好定位。
            return new DescribeBattleProfileResponse { Found = false };
        }

        var (total, slots) = await enhances.TotalsAsync(
            (long)request.CharacterId, context.CancellationToken);

        // Q2：段内阶被动万分比。战斗服的伤害第一段只从这份快照读，
        // 不另读 config/realms.json，避免两端口径不一致；查不到按 0（低境界）。
        var passive = await realmPhases.DescribePassiveAsync(
            (long)request.CharacterId, context.CancellationToken);

        // B10：已穿装备的固定攻击 / 强化等级之和 / 攻击结晶固定值一次读完。
        // 强化系数在这里折算成万分点（配置只给「每级多少」），战斗服只做除法，不读强化表。
        var worn = await equips.WornTotalsAsync(
            (long)request.CharacterId, items, crystalTable, sets, context.CancellationToken);

        return new DescribeBattleProfileResponse
        {
            Found = true,
            ClassId = identity.Value.ClassId,
            Branch = identity.Value.Branch,
            Level = identity.Value.Level,
            EnhanceTotal = total,
            EnhanceSlots = slots,
            BranchStep = identity.Value.BranchStep,
            PassivePctBps = passive.Found ? passive.PassivePctBps : 0,
            // B9：装备固定攻击之和；B2 把套装加成并进同一格（方案 9 第 1 层：套装只加基础属性）。
            // 副本的伤害第一段读它；竞技场模板、战场压缩与城战全额三条都不吃这一格
            // （它们各自的折算在战斗服里，B2 不动）。
            EquipAttack = (int)worn.TotalAttack,
            // B10：只算「穿在身上的」——没穿的装备即使有强化记录也不加伤害。
            // 方案 7.2 第二段的两个乘区项，竞技场 / 战场 / 城战三条同样不吃。
            EnhanceCoefBps = enhanceTable.CoefBpsFor(worn.EnhanceTotal),
            CrystalFlatAttack = (int)worn.CrystalFlatAttack,
            // B6：觉醒过就把那条技能编号带出去（0 = 没觉醒）。战斗服按它在三条
            // 职业技能之后追加一条，并自己再验一次「确实是这个分支的觉醒技」。
            AwakenSkillId = identity.Value.AwakenSkillId,
        };
    }


    public override async Task<DescribeHeroCodexResponse> DescribeHeroCodex(
        DescribeHeroCodexRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var account = (request.Account ?? "").Trim();
        var snap = await heroCodex.DescribeAsync(
            (long)request.CharacterId,
            account.Length == 0 ? null : account,
            context.CancellationToken);
        if (!snap.Found)
        {
            return new DescribeHeroCodexResponse { Ok = false };
        }

        var response = new DescribeHeroCodexResponse
        {
            Ok = true,
            MainStage = snap.MainStage,
            CollectedCount = snap.CollectedCount,
            EntryCount = snap.EntryCount,
            CollectionPctBps = snap.CollectionPctBps,
            TotalPassivePctBps = snap.TotalPassivePctBps,
            PassiveKind = snap.PassiveKind,
        };
        foreach (var row in snap.Entries)
        {
            response.Entries.Add(new HeroCodexEntryFact
            {
                Id = row.Id,
                Name = row.Name,
                UnlockMainStage = row.UnlockMainStage,
                Collected = row.Collected,
                PassivePctBps = row.PassivePctBps,
            });
        }

        return response;
    }
}
