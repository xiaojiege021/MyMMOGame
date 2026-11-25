using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

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

        var bagSlotsOfCharacter =
            await bagExpands.SlotsAsync((long)request.CharacterId, context.CancellationToken);
        if (request.Slot < 1 || request.Slot > bagSlotsOfCharacter)
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

        var bagSlotsOfCharacter =
            await bagExpands.SlotsAsync((long)request.CharacterId, context.CancellationToken);
        if (request.FromSlot < 1 || request.FromSlot > bagSlotsOfCharacter
            || request.ToSlot < 1 || request.ToSlot > bagSlotsOfCharacter)
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

        var bagSlotsOfCharacter =
            await bagExpands.SlotsAsync((long)request.CharacterId, context.CancellationToken);
        if (request.Slot < 1 || request.Slot > bagSlotsOfCharacter
            || request.CrystalSlot < 1 || request.CrystalSlot > bagSlotsOfCharacter
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

        var bagSlotsOfCharacter =
            await bagExpands.SlotsAsync((long)request.CharacterId, context.CancellationToken);
        if (request.Slot < 1 || request.Slot > bagSlotsOfCharacter || request.Socket < 1)
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


    /// <summary>
    /// P2-4a：这件装备这个人能不能穿。不到 weapon_lock_level 的武器五个职业都能装备；
    /// 到了就要看职业可用兵器（该职业所有分支的本命兵器并集）。等级与职业都从库里读。
    /// </summary>
    public override async Task<CanEquipResponse> CanEquip(
        CanEquipRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var identity = await characters.BattleIdentityAsync(
            (long)request.CharacterId, context.CancellationToken);
        if (identity is null)
        {
            return new CanEquipResponse { Ok = false, Reason = "character_missing" };
        }

        var itemId = (request.ItemId ?? "").Trim();
        var item = items.Find(itemId);
        if (item is null)
        {
            return new CanEquipResponse
            {
                Ok = false,
                Reason = "item_not_found",
                Level = identity.Value.Level,
                ClassId = identity.Value.ClassId,
            };
        }

        var response = new CanEquipResponse
        {
            WeaponKind = item.WeaponKind,
            RequireLevel = item.RequireLevel,
            Level = identity.Value.Level,
            ClassId = identity.Value.ClassId,
            EquipSlot = item.Slot,
        };

        // A9：属性门槛读加点——基础值从职业表现取，加点从角色库现读，合成之后再判。
        var allocated = await attrPoints.AllocationAsync(
            (long)request.CharacterId, context.CancellationToken);
        var effective = (classes.AttributesOf(identity.Value.ClassId) ?? ClassAttributes.None)
            .Plus(allocated.Strength, allocated.Agility, allocated.Spirit, allocated.Vitality);
        var reason = EquipRules.Check(
            item, identity.Value.Level, identity.Value.ClassId, branches, effective);
        response.Ok = reason.Length == 0;
        response.Reason = reason;
        return response;
    }


    /// <summary>
    /// B9：穿上一件。请求只有背包格号；部位、等级门槛与职业锁从物品表与库里读。
    /// 背包行不动（不删行、不改格号），强化与结晶仍挂在这一行上。
    /// </summary>
    public override async Task<EquipItemResponse> EquipItem(
        EquipItemRequest request, ServerCallContext context)
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

        var bagSlot = (int)request.Slot;
        var bagSlotsOfCharacter = await bagExpands.SlotsAsync(characterId, context.CancellationToken);
        if (bagSlot < 1 || bagSlot > bagSlotsOfCharacter)
        {
            // 越界格号是参数错，不是业务拒绝：与 Q10 的移动 / 丢弃同一口径。
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slot_out_of_range"));
        }

        var outcome = await equips.EquipAsync(
            characterId, account, bagSlot, items, branches, context.CancellationToken);

        return new EquipItemResponse
        {
            Ok = outcome.Reason.Length == 0,
            Reason = outcome.Reason,
            ItemId = outcome.ItemId,
            EquipSlot = outcome.Slot,
            EquipAttack = (int)outcome.Attack,
            Level = outcome.Level,
            ClassId = outcome.ClassId,
            BagSlot = (uint)outcome.BagSlot,
        };
    }


    /// <summary>
    /// B9：按部位脱下一件。脱下不换格，回包里的 bag_slot 就是它原来那一格。
    /// </summary>
    public override async Task<UnequipItemResponse> UnequipItem(
        UnequipItemRequest request, ServerCallContext context)
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

        var slot = (request.EquipSlot ?? "").Trim();
        if (Array.IndexOf(ItemTable.EquipSlots, slot) < 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slot_unknown"));
        }

        var outcome = await equips.UnequipAsync(
            characterId, account, slot, items, context.CancellationToken);

        return new UnequipItemResponse
        {
            Ok = outcome.Reason.Length == 0,
            Reason = outcome.Reason,
            ItemId = outcome.ItemId,
            EquipSlot = outcome.Slot,
            BagSlot = (uint)outcome.BagSlot,
            EquipAttack = (int)outcome.Attack,
        };
    }
}
