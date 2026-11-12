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
    TransferRepository transfers,
    TransferTable transferTable,
    CrystalRepository crystals,
    CrystalTable crystalTable,
    AuctionRepository auctions,
    AuctionTable auctionTable,
    IngotRepository ingots,
    ShopTable shopTable,
    TalismanTable talismanTable,
    TalismanRepository talismans,
    ReviveScrollTable scrollTable,
    ReviveScrollRepository scrolls,
    ExpBoosterTable boosterTable,
    ExpBoosterRepository boosters,
    UnbindTable unbindTable,
    UnbindRepository unbinds,
    RechargeTable rechargeTable,
    RechargeRepository recharges,
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
            txnId, source, partySize, expTable, boosterTable, context.CancellationToken);
        if (!outcome.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "exp_settle account={Account} id={Id} txn={Txn} source={Source} party={Party} "
            + "duplicate={Duplicate} base={Base} bonus={Bonus} gain={Gain} "
            + "level={LevelBefore}->{Level} exp={Exp} "
            + "booster_pct={BoosterPct} booster_exp={BoosterExp}",
            account.Length == 0 ? "service" : account, request.CharacterId, txnId, source, partySize,
            outcome.Duplicate, outcome.BaseExp, outcome.PartyBonus, outcome.ExpGain,
            outcome.LevelBefore, outcome.Level, outcome.Exp,
            outcome.BoosterPct, outcome.BoosterExp);

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
            // 这一笔有没有吃到道具倍率，是角色服按库里到期时刻判的只读结果。
            BoosterPct = (uint)outcome.BoosterPct,
            BoosterExp = outcome.BoosterExp,
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
                (long)request.CharacterId, (int)request.Slot, stoneId, txnId,
                request.UseCoupon, context.CancellationToken);
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
            // 只作展示：带券与不带券读的是同一档 success_rate，客户端不拿它算结果。
            SuccessRate = outcome.Tier.SuccessRate,
            ProtectedLevel = outcome.ProtectedLevel,
            CouponUsed = outcome.CouponUsed,
            CouponLeft = outcome.CouponLeft,
        };
    }

    /// <summary>
    /// 强化转移（方案 9.3）。请求里只有两个格子：两格等级、绑定、转移石与金币费用
    /// 都在角色服读。来源不高于目标、格子空、不是装备、石头或金币不够，
    /// 都回 200 + ok=false + reason，未生效的数值字段一律留 0。
    /// </summary>
    public override async Task<TransferEnhanceResponse> TransferEnhance(
        TransferEnhanceRequest request, ServerCallContext context)
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

        if (request.FromSlot < 1 || request.FromSlot > config.BagSlots
            || request.ToSlot < 1 || request.ToSlot > config.BagSlots)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slot_out_of_range"));
        }

        if (!await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        TransferOutcome outcome;
        try
        {
            outcome = await transfers.ApplyAsync(
                (long)request.CharacterId, (int)request.FromSlot, (int)request.ToSlot,
                txnId, context.CancellationToken);
        }
        catch (EnhanceException ex)
        {
            logger.LogInformation(
                "transfer_reject account={Account} id={Id} from={From} to={To} txn={Txn} reason={Reason}",
                account, request.CharacterId, request.FromSlot, request.ToSlot, txnId, ex.Code);
            // 未生效的数值字段一律留 0，客户端不能拿拒绝回包当成一次转移结果。
            return new TransferEnhanceResponse
            {
                Ok = false,
                Reason = ex.Code,
                MaxLevel = transferTable.MaxLevel,
            };
        }

        logger.LogInformation(
            "transfer account={Account} id={Id} from={From} to={To} txn={Txn} "
            + "duplicate={Duplicate} level={FromLevelBefore}:{ToLevelBefore}->{FromLevel}:{ToLevel} "
            + "gold={GoldCost} target_bound={TargetBoundBefore}->{TargetBound}",
            account, request.CharacterId, request.FromSlot, request.ToSlot, txnId,
            outcome.Duplicate, outcome.FromLevelBefore, outcome.ToLevelBefore,
            outcome.FromLevel, outcome.ToLevel, outcome.GoldCost,
            outcome.TargetBoundBefore, outcome.TargetBound);

        if (outcome.Duplicate)
        {
            return new TransferEnhanceResponse
            {
                Ok = false,
                Reason = "duplicate",
                Duplicate = true,
                MaxLevel = transferTable.MaxLevel,
            };
        }

        return new TransferEnhanceResponse
        {
            Ok = true,
            Reason = "",
            FromLevelBefore = outcome.FromLevelBefore,
            ToLevelBefore = outcome.ToLevelBefore,
            FromLevel = outcome.FromLevel,
            ToLevel = outcome.ToLevel,
            MaxLevel = transferTable.MaxLevel,
            GoldCost = outcome.GoldCost,
            Gold = outcome.Gold,
            // 只作展示：转移石编号与这一手扣的枚数，客户端不拿它们算结果。
            StoneItem = transferTable.StoneItemId,
            StoneCost = transferTable.StoneCost,
            StoneLeft = outcome.StoneLeft,
            TargetBoundBefore = outcome.TargetBoundBefore,
            TargetBound = outcome.TargetBound,
        };
    }

    /// <summary>
    /// 能量结晶装上（方案第 9 章第 3 层）。请求只有装备格、结晶背包格与镶嵌位号；
    /// 类别、部位、能不能装、固定值与结果都在角色服读。部位不对回 wrong_part，
    /// 装备与背包一格不动；业务拒绝 200 + ok=false，未生效数值字段一律留 0。
    /// </summary>
    public override async Task<CrystalResponse> InstallCrystal(
        InstallCrystalRequest request, ServerCallContext context)
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

        if (request.Slot < 1 || request.Slot > config.BagSlots
            || request.CrystalSlot < 1 || request.CrystalSlot > config.BagSlots
            || request.Socket < 1)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slot_out_of_range"));
        }

        if (!await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        CrystalOutcome outcome;
        try
        {
            outcome = await crystals.InstallAsync(
                (long)request.CharacterId, (int)request.Slot, (int)request.CrystalSlot,
                (int)request.Socket, txnId, context.CancellationToken);
        }
        catch (EnhanceException ex)
        {
            logger.LogInformation(
                "crystal_install_reject account={Account} id={Id} slot={Slot} crystal={CrystalSlot} "
                + "socket={Socket} txn={Txn} reason={Reason}",
                account, request.CharacterId, request.Slot, request.CrystalSlot,
                request.Socket, txnId, ex.Code);
            return CrystalRejected(ex.Code, true, false);
        }

        logger.LogInformation(
            "crystal_install account={Account} id={Id} slot={Slot} socket={Socket} txn={Txn} "
            + "duplicate={Duplicate} item={Item} category={Category} part={Part} filled={Before}->{After}",
            account, request.CharacterId, request.Slot, request.Socket, txnId,
            outcome.Duplicate, outcome.CrystalItem, outcome.Category, outcome.Part,
            outcome.FilledBefore, outcome.Filled);

        if (outcome.Duplicate)
        {
            return CrystalRejected("duplicate", true, true);
        }

        return CrystalReply(outcome);
    }

    /// <summary>
    /// 能量结晶拆下。只带装备格与镶嵌位；拆下来的结晶由角色服放回背包，
    /// 不改强化等级、绑定与金币，也不消耗结晶本身（方案「可拆卸复用」）。
    /// </summary>
    public override async Task<CrystalResponse> RemoveCrystal(
        RemoveCrystalRequest request, ServerCallContext context)
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

        if (request.Slot < 1 || request.Slot > config.BagSlots || request.Socket < 1)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slot_out_of_range"));
        }

        if (!await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        CrystalOutcome outcome;
        try
        {
            outcome = await crystals.RemoveAsync(
                (long)request.CharacterId, (int)request.Slot, (int)request.Socket,
                txnId, context.CancellationToken);
        }
        catch (EnhanceException ex)
        {
            logger.LogInformation(
                "crystal_remove_reject account={Account} id={Id} slot={Slot} socket={Socket} "
                + "txn={Txn} reason={Reason}",
                account, request.CharacterId, request.Slot, request.Socket, txnId, ex.Code);
            return CrystalRejected(ex.Code, false, false);
        }

        logger.LogInformation(
            "crystal_remove account={Account} id={Id} slot={Slot} socket={Socket} txn={Txn} "
            + "duplicate={Duplicate} item={Item} filled={Before}->{After}",
            account, request.CharacterId, request.Slot, request.Socket, txnId,
            outcome.Duplicate, outcome.CrystalItem, outcome.FilledBefore, outcome.Filled);

        if (outcome.Duplicate)
        {
            return CrystalRejected("duplicate", false, true);
        }

        return CrystalReply(outcome);
    }

    /// <summary>
    /// 拍卖成交结算（P1-5a）。全局服务转过来时只带买方角色、挂单号与事务号；
    /// 标价、卖方、物品、税率与到账全由 AuctionRepository 从库和配置读。
    /// 业务前提不满足是 ok=false + reason，不是 gRPC 错误码。
    /// </summary>
    public override async Task<AuctionSettleResponse> SettleAuction(
        AuctionSettleRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0 || request.ListingId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        if (txnId.Length == 0 || txnId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        if (!await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await auctions.SettleAsync(
            (long)request.CharacterId, (long)request.ListingId, txnId, context.CancellationToken);

        logger.LogInformation(
            "auction_settle account={Account} buyer={Buyer} listing={Listing} txn={Txn} "
            + "ok={Ok} reason={Reason} price={Price} tax={Tax} net={Net} duplicate={Duplicate}",
            account, request.CharacterId, request.ListingId, txnId, outcome.Ok,
            outcome.Reason, outcome.PriceGold, outcome.TaxGold, outcome.NetGold,
            outcome.Duplicate);

        if (!outcome.Ok)
        {
            // 拒绝与重放都不带「这一手生效了」的数值；税率只是配置里的展示值。
            return new AuctionSettleResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                ListingId = (ulong)outcome.ListingId,
                TaxRatePct = auctionTable.TaxRatePct,
                Duplicate = outcome.Duplicate,
            };
        }

        return new AuctionSettleResponse
        {
            Ok = true,
            Reason = "",
            ListingId = (ulong)outcome.ListingId,
            PriceGold = outcome.PriceGold,
            TaxGold = outcome.TaxGold,
            NetGold = outcome.NetGold,
            BuyerGold = outcome.BuyerGold,
            SellerGold = outcome.SellerGold,
            TaxRatePct = auctionTable.TaxRatePct,
            ItemId = outcome.ItemId,
            Count = (uint)outcome.Count,
            State = outcome.State,
        };
    }

    /// <summary>
    /// 元宝寄售成交结算（P1-5b）。全局服务转过来时只带买方角色、挂单号与事务号；
    /// 元宝数量、标价、卖方、税率与到账全由 IngotRepository 从库和配置读。
    /// 业务前提不满足是 ok=false + reason，不是 gRPC 错误码。
    /// </summary>
    public override async Task<AuctionIngotSettleResponse> SettleIngot(
        AuctionIngotSettleRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0 || request.ListingId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        if (txnId.Length == 0 || txnId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        if (!await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await ingots.SettleAsync(
            (long)request.CharacterId, (long)request.ListingId, txnId, context.CancellationToken);

        logger.LogInformation(
            "ingot_settle account={Account} buyer={Buyer} listing={Listing} txn={Txn} "
            + "ok={Ok} reason={Reason} ingot={Ingot} price={Price} tax={Tax} net={Net} duplicate={Duplicate}",
            account, request.CharacterId, request.ListingId, txnId, outcome.Ok,
            outcome.Reason, outcome.IngotAmount, outcome.PriceGold, outcome.TaxGold,
            outcome.NetGold, outcome.Duplicate);

        if (!outcome.Ok)
        {
            // 拒绝与重放都不带「这一手生效了」的数值；税率只是配置里的展示值。
            return new AuctionIngotSettleResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                ListingId = (ulong)outcome.ListingId,
                TaxRatePct = auctionTable.TaxRatePct,
                Duplicate = outcome.Duplicate,
            };
        }

        return new AuctionIngotSettleResponse
        {
            Ok = true,
            Reason = "",
            ListingId = (ulong)outcome.ListingId,
            IngotAmount = outcome.IngotAmount,
            PriceGold = outcome.PriceGold,
            TaxGold = outcome.TaxGold,
            NetGold = outcome.NetGold,
            BuyerGold = outcome.BuyerGold,
            SellerGold = outcome.SellerGold,
            BuyerIngot = outcome.BuyerIngot,
            SellerIngot = outcome.SellerIngot,
            TaxRatePct = auctionTable.TaxRatePct,
            State = outcome.State,
        };
    }

    /// <summary>
    /// 买一件商城商品。三种走法：
    ///   · 清单外 → not_in_list，标价与扣款都是 0；
    ///   · 清单内但还没接发货 → delivery_pending，一分不扣（P1-5c1 那十七种仍是这条）；
    ///   · 已接发货 → 同一笔事务里扣元宝 + 入包 + 落绑定（P1-5c2 起，本卡只有保护券）。
    ///
    /// 扣款与发货必须同一笔事务：扣了钱没东西、或东西发了钱没扣，都是不能接受的中间态。
    /// 事务号写进 grant_txn，同一个号重放整笔不做。
    /// </summary>
    public override async Task<BuyShopItemResponse> BuyShopItem(
        BuyShopItemRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var itemId = (request.ItemId ?? "").Trim();
        if (itemId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "item_missing"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        // 余额是库里的事实值，用于展示与「够不够买」；真正的扣款由事务里带条件的 update 把关。
        var balances = await currency.AllAsync(characterId, context.CancellationToken);
        var balance = balances.GetValueOrDefault(shopTable.Currency, 0L);

        var item = shopTable.Find(itemId);
        if (item is null)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} reason=not_in_list balance={Balance}",
                account, characterId, itemId, balance);

            // 清单外：标价、数量这些未生效的数值一律给 0，不拿清单里的任何数顶。
            return new BuyShopItemResponse
            {
                Ok = false,
                Reason = "not_in_list",
                ItemId = itemId,
                Currency = shopTable.Currency,
                Balance = balance,
                Enough = false,
                ChargedYuanbao = 0,
                Delivered = false,
                State = "not_in_list",
            };
        }

        if (!item.Deliverable)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} kind={Kind} reason=delivery_pending"
                + " price={Price} balance={Balance}",
                account, characterId, item.Id, item.Kind, item.PriceYuanbao, balance);

            return ShopRejected("delivery_pending", item, balance);
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > GrantRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        var goods = items.Find(item.DeliveryItemId);
        if (goods is null)
        {
            // 配置校验要求发货物必须真在物品表里，走到这里说明两张表不一致，按没接发货拒绝。
            logger.LogError(
                "shop_buy_reject account={Account} id={Id} item={Item} reason=goods_missing goods={Goods}",
                account, characterId, item.Id, item.DeliveryItemId);
            return ShopRejected("delivery_pending", item, balance);
        }

        var slots = await inventory.ListAsync(characterId, context.CancellationToken);
        var slot = FindDeliverySlot(slots, goods, item.DeliveryCount, config.BagSlots);
        if (slot == 0)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} reason=bag_full balance={Balance}",
                account, characterId, item.Id, balance);
            return ShopRejected("bag_full", item, balance);
        }

        if (balance < item.PriceYuanbao)
        {
            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} reason=not_enough_yuanbao"
                + " price={Price} balance={Balance}",
                account, characterId, item.Id, item.PriceYuanbao, balance);
            return ShopRejected("not_enough_yuanbao", item, balance);
        }

        try
        {
            // 一笔事务：先扣元宝，再把券放进格子里。缺一样整笔退回。
            var outcome = await grants.ApplyAsync(
                characterId,
                txnId,
                new List<GrantEntry>
                {
                    new()
                    {
                        Currency = new CurrencyDelta
                        {
                            Kind = shopTable.Currency,
                            Delta = -item.PriceYuanbao,
                        },
                    },
                    new()
                    {
                        Item = new ItemDelta
                        {
                            Slot = (uint)slot,
                            ItemId = goods.Id,
                            Count = item.DeliveryCount,
                        },
                    },
                },
                context.CancellationToken);

            var after = outcome.Balances.GetValueOrDefault(shopTable.Currency, 0L);

            if (outcome.Duplicate)
            {
                // 同一个事务号又来了：整笔不做，钱和东西都不动，只回当前事实值。
                logger.LogInformation(
                    "shop_buy_replay account={Account} id={Id} item={Item} txn={Txn} balance={Balance}",
                    account, characterId, item.Id, txnId, after);

                return new BuyShopItemResponse
                {
                    Ok = false,
                    Reason = "duplicate",
                    ItemId = item.Id,
                    Kind = item.Kind,
                    Name = item.Name,
                    PriceYuanbao = item.PriceYuanbao,
                    Currency = shopTable.Currency,
                    Balance = after,
                    Enough = true,
                    ChargedYuanbao = 0,
                    Delivered = false,
                    State = "duplicate",
                    DeliveryItemId = goods.Id,
                    BagSlot = 0,
                    Bound = false,
                    Duplicate = true,
                };
            }

            logger.LogInformation(
                "shop_buy account={Account} id={Id} item={Item} kind={Kind} txn={Txn}"
                + " price={Price} charged={Charged} goods={Goods} slot={Slot} bound={Bound} balance={Balance}",
                account, characterId, item.Id, item.Kind, txnId, item.PriceYuanbao,
                item.PriceYuanbao, goods.Id, slot, goods.BindsOnArrival, after);

            return new BuyShopItemResponse
            {
                Ok = true,
                Reason = "",
                ItemId = item.Id,
                Kind = item.Kind,
                Name = item.Name,
                PriceYuanbao = item.PriceYuanbao,
                Currency = shopTable.Currency,
                Balance = after,
                Enough = true,
                ChargedYuanbao = item.PriceYuanbao,
                Delivered = true,
                State = "delivered",
                DeliveryItemId = goods.Id,
                BagSlot = (uint)slot,
                Bound = goods.BindsOnArrival,
                Duplicate = false,
            };
        }
        catch (GrantException ex)
        {
            // 事务已经整笔退回去了：余额与背包都停在原处。
            var reason = ex.Code switch
            {
                "insufficient" => "not_enough_yuanbao",
                "slot_occupied" => "bag_full",
                "stack_overflow" => "bag_full",
                _ => ex.Code,
            };

            logger.LogInformation(
                "shop_buy_reject account={Account} id={Id} item={Item} reason={Reason} balance={Balance}",
                account, characterId, item.Id, reason, balance);

            return ShopRejected(reason, item, balance);
        }
    }

    /// <summary>
    /// 挑一个能放这批货的格子：先叠到已有同物品且没满的格，再找空格。
    /// 找不到回 0（调用方回 bag_full）。绑定态不在这里定——入包时按物品表的绑定规则落。
    /// </summary>
    static int FindDeliverySlot(
        IReadOnlyList<BagSlot> slots, ItemDef goods, int count, int bagSlots)
    {
        if (count < 1 || count > goods.StackMax)
        {
            return 0;
        }

        for (var i = 0; i < slots.Count; i++)
        {
            if (string.Equals(slots[i].ItemId, goods.Id, StringComparison.Ordinal)
                && slots[i].Count + count <= goods.StackMax)
            {
                return slots[i].Slot;
            }
        }

        var used = new HashSet<int>();
        for (var i = 0; i < slots.Count; i++)
        {
            used.Add(slots[i].Slot);
        }

        for (var slot = 1; slot <= bagSlots; slot++)
        {
            if (!used.Contains(slot))
            {
                return slot;
            }
        }

        return 0;
    }

    /// <summary>
    /// 在战斗里用掉一张红符。角色服这一侧只扣背包的一张，不碰生命——
    /// 当前血量是战斗进程里的战斗态，角色库不存。回复值与冷却随配置回包带回去。
    ///
    /// 方案 12.1 的三条边界在这里各落一处：
    ///   · 只回复自己：请求里没有目标编号，也只按 character_id 找本人的背包；
    ///     要治队友只能走牧师的技能，红符这条路结构上就指不过去。
    ///   · 不复活：倒地由战斗服判（Hp ≤ 0 时拒绝），角色服这一侧没有「拉起」这条路径。
    ///   · 回复不超过同级红药：配置校验里已经卡死（TalismanTable），回包只带配置值。
    ///
    /// 不是红符、背包里没有、同一个号重放，三种都不扣，也都不带「这一手生效了」的数。
    /// </summary>
    public override async Task<ConsumeTalismanResponse> ConsumeTalisman(
        ConsumeTalismanRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var itemId = (request.ItemId ?? "").Trim();
        if (itemId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "item_missing"));
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > TalismanRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        var characterId = (long)request.CharacterId;
        // 网关转发时核归属；战斗服服间直连没有账号，只确认角色存在。
        // 与 P1-2 的经验结算同一个口径，不新造一条。
        if (account.Length > 0)
        {
            if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
            {
                throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
            }
        }
        else if (!await inventory.ExistsAsync(characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var spec = talismanTable.Find(itemId);
        if (spec is null)
        {
            // 不是符：复活卷轴、保护券、随便什么编号都走这条，不扣也不回。
            // 张数给的是事实值，不是 0——这个字段是「还剩几张」，不是结算数值。
            var left = await talismans.CountAsync(characterId, itemId, context.CancellationToken);
            logger.LogInformation(
                "talisman_consume_reject account={Account} id={Id} item={Item}"
                + " reason=not_talisman left={Left}",
                account, characterId, itemId, left);
            return TalismanRejected("not_talisman", itemId, left, false);
        }

        var spend = await talismans.SpendAsync(
            characterId, spec.ItemId, txnId, context.CancellationToken);

        if (spend.Duplicate)
        {
            // 同一个号又来了：整笔不做，张数不动，也不带回复值。
            logger.LogInformation(
                "talisman_consume_replay account={Account} id={Id} item={Item} txn={Txn} left={Left}",
                account, characterId, spec.ItemId, txnId, spend.CountLeft);
            return TalismanRejected("duplicate", spec.ItemId, spend.CountLeft, true);
        }

        if (!spend.Ok)
        {
            logger.LogInformation(
                "talisman_consume_reject account={Account} id={Id} item={Item} reason={Reason}",
                account, characterId, spec.ItemId, spend.Reason);
            return TalismanRejected(spend.Reason, spec.ItemId, spend.CountLeft, false);
        }

        logger.LogInformation(
            "talisman_consume account={Account} id={Id} item={Item} txn={Txn} slot={Slot}"
            + " left={Left} pool={Pool} hp_restore={Hp} mana_restore={Mana}"
            + " cooldown_ms={Cooldown} group={Group}",
            account, characterId, spec.ItemId, txnId, spend.Slot, spend.CountLeft,
            spec.Pool, spec.HpRestore, spec.ManaRestore,
            spec.CooldownMs, spec.CooldownGroup);

        return new ConsumeTalismanResponse
        {
            Ok = true,
            Reason = "",
            ItemId = spec.ItemId,
            CountLeft = spend.CountLeft,
            Slot = (uint)Math.Max(0, spend.Slot),
            Duplicate = false,
            HpRestore = spec.HpRestore,
            CooldownMs = (uint)Math.Max(0, spec.CooldownMs),
            CooldownGroup = spec.CooldownGroup,
            Pool = spec.Pool,
            ManaRestore = spec.ManaRestore,
        };
    }

    /// <summary>
    /// 用符之前的只读查询：这个编号是不是符、回复落在哪个池，只有角色服说了算。
    /// 战斗服拿到池之后才能做「池是不是满了」「这一组冷却到没到」的本地预判，
    /// 预判不过就根本不扣符。这里不写库、不扣符、不碰生命与蓝量。
    /// </summary>
    public override async Task<DescribeTalismanResponse> DescribeTalisman(
        DescribeTalismanRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var itemId = (request.ItemId ?? "").Trim();
        if (itemId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "item_missing"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.ExistsAsync(characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var spec = talismanTable.Find(itemId);
        if (spec is null)
        {
            return new DescribeTalismanResponse
            {
                Ok = false,
                Reason = "not_talisman",
                ItemId = itemId,
                Pool = TalismanPool.Unspecified,
                CountLeft = 0,
            };
        }

        var left = await talismans.CountAsync(characterId, spec.ItemId, context.CancellationToken);
        return new DescribeTalismanResponse
        {
            Ok = true,
            Reason = "",
            ItemId = spec.ItemId,
            Pool = spec.Pool,
            CountLeft = left,
        };
    }

    /// <summary>
    /// P1-5c6：用卷轴之前的只读查询。是不是卷轴、冷却到没到、背包有几张，
    /// 只有角色服读配置与角色库说了算；战斗服不解析配置、不自己记冷却。
    /// 这里一个字都不写：冷却到期是上一张卷轴落库的那一行，不是这次查出来的。
    /// </summary>
    public override async Task<DescribeReviveScrollResponse> DescribeReviveScroll(
        DescribeReviveScrollRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var itemId = (request.ItemId ?? "").Trim();
        if (itemId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "item_missing"));
        }

        var spec = scrollTable.Find(itemId);
        if (spec is null)
        {
            return new DescribeReviveScrollResponse
            {
                Ok = false,
                Reason = "not_scroll",
                ItemId = itemId,
            };
        }

        var state = await scrolls.StateAsync(
            (long)request.CharacterId, spec.ItemId, context.CancellationToken);
        if (!state.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "scroll_describe character={Id} item={Item} count={Count} cooling={Cooling}"
            + " ready_at={ReadyAt} now={Now}",
            request.CharacterId, spec.ItemId, state.Count,
            state.Cooling ? "true" : "false", state.ReadyAtMs, state.NowMs);

        return new DescribeReviveScrollResponse
        {
            Ok = true,
            Reason = "",
            ItemId = spec.ItemId,
            CountLeft = state.Count,
            CooldownMs = (uint)Math.Max(0, spec.CooldownMs),
            HpRestorePct = (uint)Math.Max(0, scrollTable.HpRestorePct),
            ExpCostPct = (uint)Math.Max(0, scrollTable.ExpCostPct),
            ReadyAtMs = state.ReadyAtMs,
            NowMs = state.NowMs,
            Level = state.Level,
            Exp = state.Exp,
        };
    }

    /// <summary>
    /// P1-5c6：真用掉一张卷轴。角色服是持久数据的唯一写入方，所以这一笔
    /// 把「扣一张 + 扣当前级经验 + 写跨场冷却」放在同一个事务里；生命与蓝量
    /// 是战斗态，角色服一个字段都不碰，拉起由战斗服按回包的比例做。
    /// </summary>
    public override async Task<ConsumeReviveScrollResponse> ConsumeReviveScroll(
        ConsumeReviveScrollRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var itemId = (request.ItemId ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (request.CharacterId == 0 || itemId.Length == 0 || txnId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        var characterId = (long)request.CharacterId;
        var spec = scrollTable.Find(itemId);
        if (spec is null)
        {
            // 不是卷轴：红符、蓝符、保护券、随便什么编号都走这条，一个字不写。
            var state = await scrolls.StateAsync(
                characterId, itemId, context.CancellationToken);
            logger.LogInformation(
                "scroll_consume_reject id={Id} item={Item} reason=not_scroll",
                characterId, itemId);
            return ScrollRejected("not_scroll", itemId, state.Count, false);
        }

        // 服间直连没有账号，照 P1-2 SettleExp / P1-5c3 ConsumeTalisman 的口径分两支：
        // 有账号核归属，空账号只按角色号确认存在。
        if (account.Length > 0)
        {
            if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
            {
                throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
            }
        }
        else if (!await inventory.ExistsAsync(characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await expSettlements.UseReviveScrollAsync(
            characterId, account, txnId, spec.ItemId,
            scrollTable.ExpCostPct, spec.CooldownMs, context.CancellationToken);

        if (outcome.Reason.Length > 0)
        {
            // 冷却没到、背包空、同一个号重放：一笔事务整笔没做，
            // 卷轴、经验、冷却都不动。
            logger.LogInformation(
                "scroll_consume_reject id={Id} item={Item} txn={Txn} reason={Reason}"
                + " ready_at={ReadyAt}",
                characterId, spec.ItemId, txnId, outcome.Reason, outcome.ReadyAtMs);
            return ScrollRejected(
                outcome.Reason, spec.ItemId, outcome.CountLeft, outcome.Duplicate);
        }

        logger.LogInformation(
            "scroll_consume id={Id} item={Item} txn={Txn} left={Left}"
            + " exp_cost={Cost} level={Level} exp_after={ExpAfter}"
            + " hp_restore_pct={HpPct} exp_cost_pct={ExpPct}"
            + " cooldown_ms={Cooldown} ready_at={ReadyAt}",
            characterId, spec.ItemId, txnId, outcome.CountLeft,
            outcome.ExpCost, outcome.Level, outcome.ExpAfter,
            scrollTable.HpRestorePct, scrollTable.ExpCostPct,
            spec.CooldownMs, outcome.ReadyAtMs);

        return new ConsumeReviveScrollResponse
        {
            Ok = true,
            Reason = "",
            ItemId = spec.ItemId,
            CountLeft = outcome.CountLeft,
            HpRestorePct = (uint)Math.Max(0, scrollTable.HpRestorePct),
            ExpCost = outcome.ExpCost,
            Level = outcome.Level,
            ExpAfter = outcome.ExpAfter,
            ReadyAtMs = outcome.ReadyAtMs,
            CooldownMs = (uint)Math.Max(0, spec.CooldownMs),
            Duplicate = false,
        };
    }

    /// <summary>
    /// P1-5c7：用掉一张限时经验道具。请求里只有标识与物品编号，时长、倍率、
    /// 到期时刻都在角色服：编号认配置、到期时刻写角色库，网关与客户端一个都带不进来。
    /// 业务拒绝（编号不对、背包没有、同号重放）回正常包里的 ok=false + reason。
    /// </summary>
    public override async Task<UseExpBoosterResponse> UseExpBooster(
        UseExpBoosterRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var itemId = (request.ItemId ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (request.CharacterId == 0 || itemId.Length == 0
            || txnId.Length == 0 || txnId.Length > ExpSettlementRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        var characterId = (long)request.CharacterId;
        var spec = boosterTable.Find(itemId);
        if (spec is null)
        {
            // 不是道具：符、券、卷轴、随便什么编号都走这条，一个字不写。
            var state = await boosters.StateAsync(characterId, itemId, context.CancellationToken);
            logger.LogInformation(
                "exp_booster_use_reject id={Id} item={Item} reason=not_booster",
                characterId, itemId);
            return BoosterRejected("not_booster", itemId, state.Count, false);
        }

        // 服间直连没有账号，照 SettleExp / ConsumeTalisman 的口径分两支：
        // 有账号核归属，空账号只按角色号确认存在。
        if (account.Length > 0)
        {
            if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
            {
                throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
            }
        }
        else if (!await inventory.ExistsAsync(characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var outcome = await boosters.UseAsync(
            characterId, account, txnId, spec.ItemId, spec.DurationMs,
            context.CancellationToken);

        if (!outcome.Found || outcome.Reason.Length > 0)
        {
            var reason = outcome.Found ? outcome.Reason : "character_not_found";
            logger.LogInformation(
                "exp_booster_use_reject id={Id} item={Item} txn={Txn} reason={Reason}",
                characterId, spec.ItemId, txnId, reason);
            return BoosterRejected(reason, spec.ItemId, outcome.CountLeft, outcome.Duplicate);
        }

        logger.LogInformation(
            "exp_booster_use id={Id} item={Item} txn={Txn} left={Left}"
            + " duration_ms={Duration} gain_pct={GainPct} expires_at={ExpiresAt}",
            characterId, spec.ItemId, txnId, outcome.CountLeft,
            spec.DurationMs, spec.GainPct, outcome.ExpiresAtMs);

        return new UseExpBoosterResponse
        {
            Ok = true,
            Reason = "",
            ItemId = spec.ItemId,
            CountLeft = outcome.CountLeft,
            DurationMs = (uint)Math.Max(0, spec.DurationMs),
            GainPct = (uint)Math.Max(0, spec.GainPct),
            ExpiresAtMs = outcome.ExpiresAtMs,
            NowMs = outcome.NowMs,
            Duplicate = false,
        };
    }

    /// <summary>没用成的回包：时长、倍率与到期时刻一律不给生效值。</summary>
    static UseExpBoosterResponse BoosterRejected(
        string reason, string itemId, long countLeft, bool duplicate) =>
        new()
        {
            Ok = false,
            Reason = reason,
            ItemId = itemId,
            CountLeft = countLeft,
            DurationMs = 0,
            GainPct = 0,
            ExpiresAtMs = 0,
            NowMs = 0,
            Duplicate = duplicate,
        };

    /// <summary>
    /// P1-5c8：用掉几张解绑道具，把一件已绑定的装备改成可交易。只认配置里那一个道具编号；
    /// 目标必须是 items.json 里的装备、必须已绑定；张数按 now() - bound_at 的年龄从配置算，
    /// 事务内再算一次（不拿事务外的报价）。拒绝路径一条都不写库。
    /// </summary>
    public override async Task<UseUnbindItemResponse> UseUnbindItem(
        UseUnbindItemRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var itemId = (request.ItemId ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (request.CharacterId == 0 || itemId.Length == 0 || txnId.Length == 0
            || txnId.Length > ExpSettlementRepository.MaxTxnLength || request.Slot <= 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        var characterId = (long)request.CharacterId;
        var slot = (int)request.Slot;
        var spec = unbindTable.Find(itemId);
        if (spec is null)
        {
            // 不是解绑道具：符、券、卷轴、经验道具、随便什么编号都走这条，一个字不写。
            var state = await unbinds.QuoteAsync(
                characterId, slot, unbindTable.Unbind.ItemId, context.CancellationToken);
            logger.LogInformation(
                "unbind_use_reject id={Id} item={Item} reason=not_unbind_item",
                characterId, itemId);
            return UnbindRejected("not_unbind_item", itemId, slot, state, state.UnbindCount, false);
        }

        // 服间直连没有账号，照 SettleExp / ConsumeTalisman 的口径分两支：
        // 有账号核归属，空账号只按角色号确认存在。
        if (account.Length > 0)
        {
            if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
            {
                throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
            }
        }
        else if (!await inventory.ExistsAsync(characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        // 事务外先做一次只读预检，把「这一格根本不是装备 / 没绑 / 没道具」挡在写库之前。
        // 真正的判定在 UseAsync 里按同一份事实重做一遍。
        var quote = await unbinds.QuoteAsync(
            characterId, slot, spec.ItemId, context.CancellationToken);
        if (!quote.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        if (quote.Reason.Length > 0)
        {
            return UnbindRejected(quote.Reason, spec.ItemId, slot, quote, quote.UnbindCount, false);
        }

        var target = items.Find(quote.TargetItemId);
        if (target is null
            || !string.Equals(target.Kind, UnbindRepository.EquipmentKind, StringComparison.Ordinal))
        {
            // 符、券、卷轴、经验道具、材料都走这条：解绑道具只解装备。
            logger.LogInformation(
                "unbind_use_reject id={Id} slot={Slot} target={Target} reason=not_equipment",
                characterId, slot, quote.TargetItemId);
            return UnbindRejected("not_equipment", spec.ItemId, slot, quote, quote.UnbindCount, false);
        }

        if (!quote.Bound)
        {
            return UnbindRejected("not_bound", spec.ItemId, slot, quote, quote.UnbindCount, false);
        }

        if (quote.UnbindCount < quote.Cost)
        {
            var reason = quote.UnbindCount == 0 ? "no_unbind_item" : "not_enough_unbind";
            return UnbindRejected(reason, spec.ItemId, slot, quote, quote.UnbindCount, false);
        }

        var outcome = await unbinds.UseAsync(
            characterId, account, txnId, slot, spec.ItemId, context.CancellationToken);

        if (!outcome.Found || outcome.Reason.Length > 0)
        {
            var reason = outcome.Found ? outcome.Reason : "character_not_found";
            logger.LogInformation(
                "unbind_use_reject id={Id} slot={Slot} target={Target} txn={Txn} reason={Reason}",
                characterId, slot, outcome.TargetItemId, txnId, reason);
            return UnbindRejected(
                reason, spec.ItemId, slot, quote, outcome.CountLeft, outcome.Duplicate);
        }

        logger.LogInformation(
            "unbind_use id={Id} slot={Slot} target={Target} txn={Txn} cost={Cost}"
            + " left={Left} age_hours={Age} floor_hit={Floor}",
            characterId, outcome.Slot, outcome.TargetItemId, txnId, outcome.Cost,
            outcome.CountLeft, outcome.AgeHours, quote.FloorHit);

        return new UseUnbindItemResponse
        {
            Ok = true,
            Reason = "",
            ItemId = spec.ItemId,
            Slot = outcome.Slot,
            TargetItemId = outcome.TargetItemId,
            Cost = (uint)Math.Max(0, outcome.Cost),
            CountLeft = outcome.CountLeft,
            AgeHours = outcome.AgeHours,
            FloorHit = quote.FloorHit,
        };
    }

    /// <summary>没解开的回包：张数、剩余、年龄只给事实值，cost 恒 0。</summary>
    static UseUnbindItemResponse UnbindRejected(
        string reason, string itemId, int slot, UnbindQuote quote, long countLeft, bool duplicate) =>
        new()
        {
            Ok = false,
            Reason = reason,
            ItemId = itemId,
            Slot = slot,
            TargetItemId = quote.TargetItemId,
            Cost = 0,
            CountLeft = countLeft,
            AgeHours = quote.AgeHours,
            FloorHit = quote.FloorHit,
        };

    /// <summary>
    /// P1-5d：建一张内部充值订单。P0 不接真实支付渠道，订单与结算都在本服务。
    /// 客户端只带商品编号，元宝数由配置决定；订单号由服务端生成，客户端带不进来。
    /// </summary>
    public override async Task<CreateRechargeOrderResponse> CreateRechargeOrder(
        CreateRechargeOrderRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var productId = (request.ProductId ?? "").Trim();
        if (request.CharacterId == 0 || productId.Length == 0 || account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        var characterId = (long)request.CharacterId;
        if (!await inventory.OwnsAsync(account, characterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var product = rechargeTable.Find(productId);
        if (product is null)
        {
            logger.LogInformation(
                "recharge_create_reject account={Account} id={Id} product={Product} reason=product_not_found",
                account, characterId, productId);
            return new CreateRechargeOrderResponse
            {
                Ok = false,
                Reason = "product_not_found",
                ProductId = productId,
                Status = "",
            };
        }

        // 订单号在服务端生成：回调按这个号认，客户端改不了它。
        var orderId = "rc_" + Guid.NewGuid().ToString("N");
        if (!await recharges.CreateAsync(
                orderId, characterId, product.Id, product.Yuanbao, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "recharge_create account={Account} id={Id} order={Order} product={Product} yuanbao={Yuanbao}",
            account, characterId, orderId, product.Id, product.Yuanbao);

        return new CreateRechargeOrderResponse
        {
            Ok = true,
            Reason = "",
            OrderId = orderId,
            ProductId = product.Id,
            Yuanbao = product.Yuanbao,
            Status = "pending",
        };
    }

    /// <summary>
    /// P1-5d：结算一笔内部充值订单（P0 用它模拟支付渠道回调）。同一笔重复调用只发一次；
    /// 第二次回 duplicate，元宝余额不变。
    /// </summary>
    public override async Task<SettleRechargeOrderResponse> SettleRechargeOrder(
        SettleRechargeOrderRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var orderId = (request.OrderId ?? "").Trim();
        if (orderId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        var outcome = await recharges.SettleAsync(
            orderId, account.Length > 0 ? account : null, context.CancellationToken);

        if (!outcome.Found)
        {
            // 不存在与「不是你的订单」都回同一个 order_not_found，不泄露别人的订单。
            logger.LogInformation(
                "recharge_settle_reject account={Account} order={Order} reason=order_not_found",
                account, orderId);
            return new SettleRechargeOrderResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                OrderId = orderId,
                Status = "",
            };
        }

        if (outcome.Reason.Length > 0)
        {
            logger.LogInformation(
                "recharge_settle_reject account={Account} order={Order} reason={Reason}",
                account, orderId, outcome.Reason);
            return new SettleRechargeOrderResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                OrderId = orderId,
                Status = outcome.Status,
                Yuanbao = outcome.Yuanbao,
                Balance = outcome.Balance,
                Duplicate = outcome.Duplicate,
            };
        }

        logger.LogInformation(
            "recharge_settle account={Account} order={Order} yuanbao={Yuanbao} balance={Balance}",
            account, orderId, outcome.Yuanbao, outcome.Balance);

        return new SettleRechargeOrderResponse
        {
            Ok = true,
            Reason = "",
            OrderId = orderId,
            Status = "paid",
            Yuanbao = outcome.Yuanbao,
            Balance = outcome.Balance,
            PaidAtMs = outcome.PaidAtMs,
            Duplicate = false,
        };
    }

    /// <summary>没扣成的回包：比例、经验变化与到期时刻一律不给生效值。</summary>
    static ConsumeReviveScrollResponse ScrollRejected(
        string reason, string itemId, long countLeft, bool duplicate) =>
        new()
        {
            Ok = false,
            Reason = reason,
            ItemId = itemId,
            CountLeft = countLeft,
            HpRestorePct = 0,
            ExpCost = 0,
            Level = 0,
            ExpAfter = 0,
            ReadyAtMs = 0,
            CooldownMs = 0,
            Duplicate = duplicate,
        };

    /// <summary>没扣成的回包：不带回复值与冷却——下一次真用上时才由配置给。</summary>
    ConsumeTalismanResponse TalismanRejected(
        string reason, string itemId, long countLeft, bool duplicate) =>
        new()
        {
            Ok = false,
            Reason = reason,
            ItemId = itemId,
            CountLeft = countLeft,
            Slot = 0,
            Duplicate = duplicate,
            HpRestore = 0,
            CooldownMs = 0,
            CooldownGroup = "",
        };

    /// <summary>没成交的回包：扣款与发货类数值一律 0，余额给库里的事实值。</summary>
    BuyShopItemResponse ShopRejected(string reason, ShopTable.Item item, long balance) =>
        new()
        {
            Ok = false,
            Reason = reason,
            ItemId = item.Id,
            Kind = item.Kind,
            Name = item.Name,
            PriceYuanbao = item.PriceYuanbao,
            Currency = shopTable.Currency,
            Balance = balance,
            Enough = balance >= item.PriceYuanbao,
            ChargedYuanbao = 0,
            Delivered = false,
            State = reason,
            DeliveryItemId = item.DeliveryItemId,
            BagSlot = 0,
            Bound = false,
            Duplicate = false,
        };

    // 拒绝与重放都不带「这一手生效了」的数值：只有配置里的镶嵌位数作展示。
    CrystalResponse CrystalRejected(string reason, bool installed, bool duplicate) =>
        new()
        {
            Ok = false,
            Reason = reason,
            Installed = installed,
            SocketCount = crystalTable.SocketCount,
            Duplicate = duplicate,
        };

    CrystalResponse CrystalReply(CrystalOutcome outcome) =>
        new()
        {
            Ok = true,
            Reason = "",
            Installed = outcome.Installed,
            Slot = (uint)outcome.Slot,
            Socket = (uint)outcome.Socket,
            CrystalItem = outcome.CrystalItem,
            // 只作展示：类别与部位只在服务端从配置读，客户端不拿它们算结果。
            Category = outcome.Category,
            Part = outcome.Part,
            SocketCount = crystalTable.SocketCount,
            FilledBefore = outcome.FilledBefore,
            Filled = outcome.Filled,
            CrystalLeft = outcome.CrystalLeft,
            Gold = outcome.Gold,
            EnhanceLevel = outcome.EnhanceLevel,
            Bound = outcome.Bound,
        };

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
