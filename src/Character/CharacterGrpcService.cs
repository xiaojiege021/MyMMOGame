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

        var response = new ListInventoryResponse { BagSlots = (uint)config.BagSlots };
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
    /// 主线 1 人、不扣不占（P1-2a 的路径）。扣疲劳的本（深渊）按人数倍增，
    /// 到日上限就整笔拒绝；扣减与进本记录在一个事务里。
    /// 战斗服那边还有一道同样的上限（它按 party 开本），两道都挡。
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
        };
}
