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
    RaceTable races,
    BranchTable branches,
    ClassIds classes,
    ItemTable items,
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
}
