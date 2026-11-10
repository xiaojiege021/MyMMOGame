using Grpc.Core;
using Mmo.Proto.Character;
using Npgsql;

namespace Character;

/// <summary>
/// 建角色的 gRPC 入口。校验收在服务端：种族要在表里、职业要在表里、
/// 分支必须属于这个职业。种族与外观原样落库，不参与任何数值计算。
/// </summary>
public sealed class CharacterGrpcService(
    CharacterRepository characters,
    InventoryRepository inventory,
    CurrencyRepository currency,
    GrantRepository grants,
    RaceTable races,
    BranchTable branches,
    ClassIds classes,
    ItemTable items,
    CurrencyTable currencies,
    DungeonTable dungeons,
    DungeonEntryRepository dungeonEntries,
    WeeklyShareRepository weeklyShares,
    HotSpringRepository hotSpring,
    ExpSettlementRepository expSettlements,
    ExpTable expTable,
    EnhanceRepository enhances,
    EnhanceTable enhanceTable,
    CharacterConfig config,
    ILogger<CharacterGrpcService> logger) : CharacterService.CharacterServiceBase
{
    public override async Task<CreateCharacterResponse> CreateCharacter(
        CreateCharacterRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var name = (request.Name ?? "").Trim();
        var race = (request.Race ?? "").Trim();
        var classId = (request.ClassId ?? "").Trim();
        var branch = (request.Branch ?? "").Trim();
        var appearance = string.IsNullOrWhiteSpace(request.Appearance) ? "{}" : request.Appearance;

        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (name.Length == 0 || name.Length > 16)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "name_invalid"));
        }

        if (races.Find(race) is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown_race"));
        }

        if (!classes.Contains(classId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown_class"));
        }

        // 分支可以不填（15 级一转才定）；填了就必须属于这个职业。
        if (branch.Length > 0 && !branches.BelongsTo(branch, classId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "branch_mismatch"));
        }

        if (!await characters.AccountExistsAsync(account, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "account_not_found"));
        }

        if (await characters.NameTakenAsync(account, name, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, "name_taken"));
        }

        try
        {
            var created = await characters.CreateAsync(
                account,
                name,
                race,
                appearance,
                classId,
                branch.Length == 0 ? null : branch,
                context.CancellationToken);

            logger.LogInformation(
                "character_created id={Id} account={Account} race={Race} class={Class} branch={Branch}",
                created.Id, account, created.Race, created.ClassId, created.Branch);

            return new CreateCharacterResponse
            {
                CharacterId = (ulong)created.Id,
                Name = created.Name,
                Race = created.Race,
                Appearance = created.Appearance,
                ClassId = created.ClassId,
                Branch = created.Branch,
                Level = created.Level,
                Realm = created.Realm,
                MainStageEmpty = created.MainStageEmpty,
                AlignmentEmpty = created.AlignmentEmpty,
            };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 并发下两个同名一起进来，唯一索引是最后一道闸。
            throw new RpcException(new Status(StatusCode.AlreadyExists, "name_taken"));
        }
    }

    /// <summary>
    /// 写等级。上限 100 是大版本也不抬的（方案「成长与物品」），所以 101 直接拒绝，
    /// 不静默夹到 100——夹住会让调用方以为写成功了。库层另有 CHECK，两边都挡。
    /// </summary>
    public override async Task<SetLevelResponse> SetLevel(SetLevelRequest request, ServerCallContext context)
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

        if (request.Level < MinLevel || request.Level > MaxLevel)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "level_out_of_range"));
        }

        var level = await characters.SetLevelAsync(
            account, (long)request.CharacterId, request.Level, context.CancellationToken);

        if (level is null)
        {
            // 不是这个账号的角色，跟不存在一样处理，不泄漏别人的编号存不存在。
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "character_level account={Account} id={Id} level={Level}",
            account, request.CharacterId, level);

        return new SetLevelResponse
        {
            CharacterId = request.CharacterId,
            Level = level.Value,
        };
    }

    public const int MinLevel = 1;

    public const int MaxLevel = 100;

    /// <summary>
    /// 往一格放东西。绑定规则由物品表定，不看客户端说什么：
    /// 副本装备拾取即绑，商城道具到账即绑，矿石药材这类不绑。
    /// </summary>
    public override async Task<InventorySlot> AddItem(AddItemRequest request, ServerCallContext context)
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

        var item = items.Find((request.ItemId ?? "").Trim());
        if (item is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown_item"));
        }

        if (request.Count < 1)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "count_invalid"));
        }

        if (request.Count > item.StackMax)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "stack_overflow"));
        }

        if (request.Slot < 1 || request.Slot > config.BagSlots)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slot_out_of_range"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        // 一格一样东西：已有的必须是同一件，而且加起来不能超过堆叠上限。
        var existing = await inventory.GetSlotAsync(characterId, (int)request.Slot, context.CancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.ItemId, item.Id, StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "slot_occupied"));
            }

            if (existing.Count + request.Count > item.StackMax)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "stack_overflow"));
            }
        }

        var slot = await inventory.AddAsync(
            characterId, (int)request.Slot, item, request.Count, context.CancellationToken);

        logger.LogInformation(
            "inventory_add account={Account} id={Id} slot={Slot} item={Item} count={Count} bound={Bound}",
            account, characterId, slot.Slot, slot.ItemId, slot.Count, slot.Bound);

        return new InventorySlot
        {
            Slot = (uint)slot.Slot,
            ItemId = slot.ItemId,
            Count = slot.Count,
            Bound = slot.Bound,
        };
    }

    public override async Task<ListInventoryResponse> ListInventory(
        ListInventoryRequest request,
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

        // 名字牌文本也在这里读：同一份归属条件下取角色名，网关拿不到别人的名字。
        // 境界同样是只读的，世界服判飞行与跨区时来问；客户端提交不了这个值。
        var displayName = await inventory.DisplayNameAsync(account, characterId, context.CancellationToken);
        var realm = await inventory.RealmAsync(account, characterId, context.CancellationToken);
        var response = new ListInventoryResponse
        {
            BagSlots = (uint)config.BagSlots,
            CharacterName = displayName ?? "",
            Realm = realm ?? "",
        };
        foreach (var slot in await inventory.ListAsync(characterId, context.CancellationToken))
        {
            response.Slots.Add(new InventorySlot
            {
                Slot = (uint)slot.Slot,
                ItemId = slot.ItemId,
                Count = slot.Count,
                Bound = slot.Bound,
            });
        }

        return response;
    }

    /// <summary>
    /// 这一格能不能直接交易。绑定就是不能——这是「绑定/非绑双轨」的判定点，
    /// 交易本身（拍卖、寄售、摆摊）在 P1-5a 起，
    /// </summary>
    public override async Task<CanTradeResponse> CanTrade(CanTradeRequest request, ServerCallContext context)
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

        var slot = await inventory.GetSlotAsync(characterId, (int)request.Slot, context.CancellationToken);
        if (slot is null)
        {
            return new CanTradeResponse { Tradable = false, Reason = "slot_empty" };
        }

        if (slot.Bound)
        {
            return new CanTradeResponse { Tradable = false, Reason = "bound_item" };
        }

        return new CanTradeResponse { Tradable = true, Reason = "" };
    }

    /// <summary>
    /// 货币加减。金币、灵石五档、元宝都走这里：扣成负数直接拒绝，
    /// 不做「扣到 0 为止」那种夹取——夹住会让调用方以为扣成功了。
    /// </summary>
    public override async Task<CurrencyBalance> AddCurrency(
        AddCurrencyRequest request,
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

        var kind = (request.Kind ?? "").Trim();
        if (currencies.Find(kind) is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown_currency"));
        }

        if (request.Delta == 0)
        {
            // 0 不改变任何东西，但也别写一行「0 元宝」出来。
            throw new RpcException(new Status(StatusCode.InvalidArgument, "delta_zero"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var amount = await currency.AddAsync(characterId, kind, request.Delta, context.CancellationToken);
        if (amount is null)
        {
            logger.LogInformation(
                "currency_reject account={Account} id={Id} kind={Kind} delta={Delta} reason=insufficient",
                account, characterId, kind, request.Delta);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "insufficient"));
        }

        logger.LogInformation(
            "currency account={Account} id={Id} kind={Kind} delta={Delta} amount={Amount}",
            account, characterId, kind, request.Delta, amount.Value);

        return new CurrencyBalance { Kind = kind, Amount = amount.Value };
    }

    public override async Task<GetCurrencyResponse> GetCurrency(
        GetCurrencyRequest request,
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

        var balances = await currency.AllAsync(characterId, context.CancellationToken);

        // 按配置顺序全部返回，没记过账的按 0。
        var response = new GetCurrencyResponse();
        foreach (var def in currencies.All)
        {
            response.Balances.Add(new CurrencyBalance
            {
                Kind = def.Id,
                Amount = balances.GetValueOrDefault(def.Id, 0L),
            });
        }

        return response;
    }

    /// <summary>
    /// 发一笔奖。整笔在一个数据库事务里：事务号重复就整笔不做（duplicate），
    /// 中间任何一条不满足条件就整笔回退，不会出现「钱扣了东西没给」。
    /// </summary>
    public override async Task<ApplyGrantResponse> ApplyGrant(
        ApplyGrantRequest request,
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

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_id_invalid"));
        }

        if (request.Entries.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "entries_empty"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        GrantResult result;
        try
        {
            result = await grants.ApplyAsync(
                characterId, txnId, request.Entries, context.CancellationToken);
        }
        catch (GrantException ex)
        {
            logger.LogInformation(
                "grant_reject account={Account} id={Id} txn={Txn} code={Code}",
                account, characterId, txnId, ex.Code);

            // 业务前提不满足（钱不够、格子占着）走 FailedPrecondition，与参数错分开。
            var status = ex.Code is "insufficient" or "stack_overflow" or "slot_occupied"
                ? StatusCode.FailedPrecondition
                : StatusCode.InvalidArgument;
            throw new RpcException(new Status(status, ex.Code));
        }

        logger.LogInformation(
            "grant account={Account} id={Id} txn={Txn} duplicate={Duplicate} entries={Entries}",
            account, characterId, txnId, result.Duplicate, request.Entries.Count);

        var response = new ApplyGrantResponse { Duplicate = result.Duplicate };
        foreach (var def in currencies.All)
        {
            response.Balances.Add(new CurrencyBalance
            {
                Kind = def.Id,
                Amount = result.Balances.GetValueOrDefault(def.Id, 0L),
            });
        }

        foreach (var slot in result.Slots)
        {
            response.Slots.Add(new InventorySlot
            {
                Slot = (uint)slot.Slot,
                ItemId = slot.ItemId,
                Count = slot.Count,
                Bound = slot.Bound,
            });
        }

        return response;
    }

    /// <summary>
    /// 进本。人数上限与「扣不扣疲劳、占不占份额」都来自 config/dungeons.json。
    /// 主线 1 人、不扣不占（P1-2a 的路径）；扣疲劳的本（深渊）按人数倍增，
    /// 到日上限就整笔拒绝；占周份额的本（巢穴）按人数扣份额、按人数记产出份数，
    /// 到周上限整笔拒绝。两种扣减与进本记录各在一个事务里。
    /// 战斗服那边还有一道同样的人数上限（它按 party 开本），两道都挡。
    /// 准入只看等级，不查境界：元婴副本的境界门槛在后面的卡。
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

        // 人数超上限：不是错误码能表达的「业务不允许」，回 allowed=false 让客户端提示。
        if (partySize > dungeon.MaxPlayers)
        {
            logger.LogInformation(
                "dungeon_reject account={Account} id={Id} dungeon={Dungeon} party={Party} limit={Limit}",
                account, characterId, dungeon.Id, partySize, dungeon.MaxPlayers);

            return Rejected(
                "party_too_large", dungeon, reserves.Value.Fatigue, reserves.Value.WeeklyShare);
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
                Fatigue = share.Fatigue,
                WeeklyShare = share.Used,
                EntryId = (ulong)share.EntryId,
                FatigueCost = 0,
                FatigueDailyCap = dungeons.FatigueDailyCap,
                WeeklyShareCost = share.Cost,
                WeeklyShareRemaining = share.Remaining,
                WeeklyShareCap = share.WeeklyCap,
                RewardShares = share.RewardShares,
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
                Fatigue = reserves.Value.Fatigue,
                WeeklyShare = reserves.Value.WeeklyShare,
                EntryId = (ulong)entryId,
                FatigueCost = 0,
                FatigueDailyCap = dungeons.FatigueDailyCap,
                WeeklyShareCost = 0,
                WeeklyShareRemaining = dungeons.WeeklyShareCap - reserves.Value.WeeklyShare,
                WeeklyShareCap = dungeons.WeeklyShareCap,
                RewardShares = 0,
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
    /// 等级经验结算。活动、副本、任务、竞技场、战场、城战和世界 Boss 只走这一笔：
    /// 基础经验 + 同一场至少两人时的一截少量加成，只加经验。
    /// 请求里没有经验数值，也不接受客户端算好的加成：数值全在 config/exp.json，
    /// 角色服是唯一写入方。同一个事务号再提交不发第二次。
    /// account 为空只用于战斗服通关后的服间直连；网关转发一定带账号并复核归属。
    /// </summary>
    public override async Task<SettleExpResponse> SettleExp(
        SettleExpRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > ExpSettlementRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_id_invalid"));
        }

        var source = (request.Source ?? "").Trim();
        if (source.Length == 0 || source.Length > ExpSettlementRepository.MaxSourceLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "source_invalid"));
        }

        var partySize = (int)request.PartySize;
        if (partySize < 1 || partySize > expTable.PartyMaxSize)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "party_size_invalid"));
        }

        // 网关转发时先核归属，与前面各行同一个口径；服间直连没有账号，
        // 由战斗服与角色服之间的内网调用保证，仓库里只按角色编号找。
        if (account.Length > 0
            && !await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await expSettlements.SettleAsync(
            (long)request.CharacterId, account.Length == 0 ? null : account,
            txnId, source, partySize, expTable, context.CancellationToken);
        if (!outcome.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "exp_settle account={Account} id={Id} txn={Txn} source={Source} party={Party} "
            + "duplicate={Duplicate} base={Base} bonus={Bonus} gain={Gain} "
            + "level={LevelBefore}->{Level} exp={Exp}",
            account.Length == 0 ? "service" : account, request.CharacterId, txnId, source, partySize,
            outcome.Duplicate, outcome.BaseExp, outcome.PartyBonus, outcome.ExpGain,
            outcome.LevelBefore, outcome.Level, outcome.Exp);

        return new SettleExpResponse
        {
            Duplicate = outcome.Duplicate,
            BaseExp = outcome.BaseExp,
            PartyBonus = outcome.PartyBonus,
            ExpGain = outcome.ExpGain,
            LevelBefore = outcome.LevelBefore,
            Level = outcome.Level,
            Exp = outcome.Exp,
            LevelCap = expTable.LevelCap,
            AtCap = outcome.Level >= expTable.LevelCap,
            PartySize = (uint)partySize,
            Source = source,
        };
    }

    /// <summary>
    /// 强化一次装备。目标等级由角色服从库里读出的当前等级 +1 算，档位、材料、
    /// 消耗、成功率全在 config/enhance.json；请求里带不进等级、成功率与结果。
    /// 扣石头 / 扣金币 / 改等级在 EnhanceRepository 的一笔事务里，失败只掉 1 级。
    /// 身份与参数错走 gRPC 错误码，业务前提不满足回正常包里的 ok=false + reason。
    /// </summary>
    public override async Task<EnhanceResponse> Enhance(EnhanceRequest request, ServerCallContext context)
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

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        if (request.Slot < 1 || request.Slot > config.BagSlots)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slot_out_of_range"));
        }

        var stoneId = (request.StoneId ?? "").Trim();
        if (stoneId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "stone_missing"));
        }

        if (!await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        EnhanceOutcome outcome;
        try
        {
            outcome = await enhances.ApplyAsync(
                (long)request.CharacterId, (int)request.Slot, stoneId, txnId, context.CancellationToken);
        }
        catch (EnhanceException ex)
        {
            logger.LogInformation(
                "enhance_reject account={Account} id={Id} slot={Slot} stone={Stone} txn={Txn} reason={Reason}",
                account, request.CharacterId, request.Slot, stoneId, txnId, ex.Code);
            // 未生效的数值字段一律留 0，客户端不能拿拒绝回包当成一次强化结果。
            return new EnhanceResponse
            {
                Ok = false,
                Reason = ex.Code,
                MaxLevel = enhanceTable.MaxLevel,
            };
        }

        logger.LogInformation(
            "enhance account={Account} id={Id} slot={Slot} stone={Stone} txn={Txn} "
            + "duplicate={Duplicate} level={LevelBefore}->{Level} success={Success} "
            + "tier={Tier} gold={Gold} stone_left={StoneLeft}",
            account, request.CharacterId, request.Slot, stoneId, txnId, outcome.Duplicate,
            outcome.LevelBefore, outcome.Level, outcome.Success,
            outcome.Tier?.Id ?? "", outcome.Gold, outcome.StoneLeft);

        if (outcome.Duplicate)
        {
            return new EnhanceResponse
            {
                Ok = false,
                Reason = "duplicate",
                Duplicate = true,
                MaxLevel = enhanceTable.MaxLevel,
            };
        }

        return new EnhanceResponse
        {
            Ok = true,
            Reason = "",
            LevelBefore = outcome.LevelBefore,
            Level = outcome.Level,
            MaxLevel = enhanceTable.MaxLevel,
            Success = outcome.Success,
            Tier = outcome.Tier!.Id,
            RequiredStone = outcome.Tier.StoneId,
            StoneCost = outcome.Tier.StoneCost,
            GoldCost = outcome.Tier.GoldCost,
            Gold = outcome.Gold,
            StoneLeft = outcome.StoneLeft,
        };
    }

    EnterDungeonResponse Rejected(string reason, DungeonDef dungeon, int fatigue, int weeklyShare) =>
        new()
        {
            Allowed = false,
            Reason = reason,
            MaxPlayers = (uint)dungeon.MaxPlayers,
            Fatigue = fatigue,
            WeeklyShare = weeklyShare,
            FatigueCost = 0,
            FatigueDailyCap = dungeons.FatigueDailyCap,
            WeeklyShareCost = 0,
            WeeklyShareRemaining = dungeons.WeeklyShareCap - weeklyShare,
            WeeklyShareCap = dungeons.WeeklyShareCap,
            RewardShares = 0,
        };
}
