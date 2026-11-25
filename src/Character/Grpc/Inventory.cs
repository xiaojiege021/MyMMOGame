using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

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

        if (request.Slot < 1
            || request.Slot > await bagExpands.SlotsAsync(
                (long)request.CharacterId, context.CancellationToken))
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
        // C2：realm 仍是顺序表规范名（旧数据里的散仙也归一成金仙），realm_display 才按
        // 第十场的立仙 / 立魔决定显示金仙还是散仙；世界服只读 realm，不认展示名。
        var displayName = await inventory.DisplayNameAsync(account, characterId, context.CancellationToken);
        var facts = await inventory.RealmFactsAsync(account, characterId, context.CancellationToken);
        var realm = realms.Normalize(facts?.Realm ?? "");
        var response = new ListInventoryResponse
        {
            // B7+B8：格数按角色现取（基础格 + 买过的扩容），不再是配置里那个单一值。
            BagSlots = (uint)await bagExpands.SlotsAsync(characterId, context.CancellationToken),
            CharacterName = displayName ?? "",
            Realm = realm,
            RealmDisplay = realms.DisplayName(realm, facts?.Alignment ?? ""),
            // B4：骑着的玩法坐骑给多少万分比提速。世界服判地表位移上限时读这一格；
            // 没骑回 0，与「没有坐骑」同义，不改飞行（飞行只由境界打开）。
            MountSpeedBps = await mounts.SpeedBpsAsync(
                characterId, mountTable, context.CancellationToken),
        };
        // B9：这一行正穿在哪个部位。只读一张穿戴表，不改背包；客户端据此把「穿上 / 脱下」
        // 显示成当前事实，而不是自己记一份。
        var worn = await equips.EquippedByBagSlotAsync(characterId, context.CancellationToken);
        // U20 / B3：制作掷点跟着那一行走（主属性 + 最多两条附加属性）。只读，不重掷；
        // 非制作产出的行没有这一项，回包里 craft_attr=0、craft_extras 为空。
        var rolls = await inventory.RollsAsync(characterId, context.CancellationToken);
        foreach (var slot in await inventory.ListAsync(characterId, context.CancellationToken))
        {
            var one = new InventorySlot
            {
                Slot = (uint)slot.Slot,
                ItemId = slot.ItemId,
                Count = slot.Count,
                Bound = slot.Bound,
                EquippedSlot = worn.TryGetValue(slot.Slot, out var where) ? where : "",
            };

            if (rolls.TryGetValue(slot.Slot, out var roll))
            {
                one.CraftAttr = roll.Attr;
                foreach (var extra in roll.Extras)
                {
                    one.CraftExtras.Add(new CraftExtra { Attr = extra.Attr, Value = extra.Value });
                }
            }

            response.Slots.Add(one);
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
    /// Q10：移动一格。空格直接挪、同物品合并、不同物品交换；强化挂在背包行上，
    /// 这里只改 slot，所以不丢也不串。越界格号是参数错，业务拒绝一律回 ok=false。
    /// </summary>
    public override async Task<MoveItemResponse> MoveItem(
        MoveItemRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0 || request.FromSlot == 0 || request.ToSlot == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        var bagSlotsOfCharacter =
            await bagExpands.SlotsAsync((long)request.CharacterId, context.CancellationToken);
        if (request.FromSlot > bagSlotsOfCharacter || request.ToSlot > bagSlotsOfCharacter)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slot_out_of_range"));
        }

        var outcome = await bags.MoveAsync(
            (long)request.CharacterId,
            (request.Account ?? "").Trim(),
            (int)request.FromSlot,
            (int)request.ToSlot,
            bagSlotsOfCharacter,
            items,
            context.CancellationToken);

        if (!outcome.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "bag_move id={Id} from={From} to={To} ok={Ok} reason={Reason}"
            + " merged={Merged} swapped={Swapped} source={Source} target={Target}",
            request.CharacterId, request.FromSlot, request.ToSlot, outcome.Reason.Length == 0,
            outcome.Reason, outcome.Merged, outcome.Swapped,
            outcome.Source.ItemId + ":" + outcome.Source.Count,
            outcome.Target.ItemId + ":" + outcome.Target.Count);

        return new MoveItemResponse
        {
            Ok = outcome.Reason.Length == 0,
            Reason = outcome.Reason,
            Source = ToProtoSlot(outcome.Source),
            Target = ToProtoSlot(outcome.Target),
            Merged = outcome.Merged,
            Swapped = outcome.Swapped,
        };
    }


    /// <summary>
    /// Q10：丢掉若干件。必须带 confirm=true（界面点两次才发），事务号由网关生成，
    /// 同号重放回 duplicate、不二次丢。丢光才删行，强化与结晶跟着走。
    /// </summary>
    public override async Task<DiscardItemResponse> DiscardItem(
        DiscardItemRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        if (request.CharacterId == 0 || request.Slot == 0 || txnId.Length == 0
            || txnId.Length > ExpSettlementRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        if (request.Slot > await bagExpands.SlotsAsync(
                (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slot_out_of_range"));
        }

        if (request.Count < 1)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "count_invalid"));
        }

        var outcome = await bags.DiscardAsync(
            (long)request.CharacterId,
            (request.Account ?? "").Trim(),
            (int)request.Slot,
            request.Count,
            request.Confirm,
            txnId,
            context.CancellationToken);

        if (!outcome.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "bag_discard id={Id} slot={Slot} item={Item} count={Count} confirm={Confirm}"
            + " ok={Ok} reason={Reason} left={Left} duplicate={Duplicate}",
            request.CharacterId, request.Slot, outcome.ItemId, request.Count, request.Confirm,
            outcome.Reason.Length == 0, outcome.Reason, outcome.CountLeft, outcome.Duplicate);

        return new DiscardItemResponse
        {
            Ok = outcome.Reason.Length == 0,
            Reason = outcome.Reason,
            ItemId = outcome.ItemId,
            CountLeft = outcome.CountLeft,
            Duplicate = outcome.Duplicate,
        };
    }


    /// <summary>
    /// Q10：排序。种类 → 品阶 → 物品编号，紧凑到第 1 格；只改 slot，
    /// 绑定、数量、强化一个都不动。回包给的是排完之后的整份背包。
    /// </summary>
    public override async Task<SortInventoryResponse> SortInventory(
        SortInventoryRequest request, ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        var outcome = await bags.SortAsync(
            (long)request.CharacterId,
            (request.Account ?? "").Trim(),
            items,
            context.CancellationToken);

        if (!outcome.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "bag_sort id={Id} ok={Ok} reason={Reason} moved={Moved} rows={Rows}",
            request.CharacterId, outcome.Reason.Length == 0, outcome.Reason,
            outcome.Moved, outcome.Slots.Count);

        var response = new SortInventoryResponse
        {
            Ok = outcome.Reason.Length == 0,
            Reason = outcome.Reason,
            Moved = (uint)Math.Max(0, outcome.Moved),
        };

        // 回包是「排完之后的整份背包」，客户端直接抄进去、不再补发一次只读请求，
        // 所以这一份里也得有这一行正穿在哪个部位：漏了的话排一次序，屏上的穿戴标就没了。
        // 穿戴表按 inventory.slot 现读（排序只改 slot 一列），必须在 SortAsync 提交之后取。
        var worn = await equips.EquippedByBagSlotAsync(
            (long)request.CharacterId, context.CancellationToken);
        foreach (var slot in outcome.Slots)
        {
            var row = ToProtoSlot(slot);
            row.EquippedSlot = worn.TryGetValue(slot.Slot, out var where) ? where : "";
            response.Slots.Add(row);
        }

        return response;
    }


    /// <summary>
    /// Q10：一次用掉叠里的若干个。本卡只做门与判定，效果入口不在这一卡：
    /// 今天挂着的只有「限时经验道具」这一个原入口，而它同时只生效一个、再来一张只刷新，
    /// 所以一次用多张只会白吃——只放行 1 张，其余回 effect_single_instance；
    /// 回春丹、凝神丹、符这类还没接效果的回 no_effect，一个都不扣，等 B1 接。
    /// B1 接丹药时在这里加一条 handler（扣 N 个 + 逐次生效），不改协议、不改客户端。
    /// </summary>
    public override async Task<UseItemBatchResponse> UseItemBatch(
        UseItemBatchRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (request.CharacterId == 0 || request.Slot == 0 || txnId.Length == 0
            || txnId.Length > ExpSettlementRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "request_missing"));
        }

        if (request.Slot > await bagExpands.SlotsAsync(
                (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slot_out_of_range"));
        }

        if (request.Count < 1)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "count_invalid"));
        }

        var characterId = (long)request.CharacterId;

        // 服间直连没有账号，照 UseExpBooster 的口径分两支。
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

        var slot = await inventory.GetSlotAsync(characterId, (int)request.Slot, context.CancellationToken);
        if (slot is null)
        {
            return BatchRejected("slot_empty", "", 0, false);
        }

        var item = items.Find(slot.ItemId);
        if (item is null || !string.Equals(item.Kind, UsableKind, StringComparison.Ordinal))
        {
            return BatchRejected("not_usable", slot.ItemId, slot.Count, false);
        }

        if (request.Count > slot.Count)
        {
            return BatchRejected("count_invalid", slot.ItemId, slot.Count, false);
        }

        var spec = boosterTable.Find(slot.ItemId);
        if (spec is null)
        {
            logger.LogInformation(
                "bag_use_reject id={Id} slot={Slot} item={Item} reason=no_effect",
                characterId, request.Slot, slot.ItemId);
            return BatchRejected("no_effect", slot.ItemId, slot.Count, false);
        }

        if (request.Count > 1)
        {
            logger.LogInformation(
                "bag_use_reject id={Id} slot={Slot} item={Item} count={Count}"
                + " reason=effect_single_instance",
                characterId, request.Slot, slot.ItemId, request.Count);
            return BatchRejected("effect_single_instance", slot.ItemId, slot.Count, false);
        }

        var outcome = await boosters.UseAsync(
            characterId, account, txnId, spec.ItemId, spec.DurationMs, context.CancellationToken);

        if (!outcome.Found || outcome.Reason.Length > 0)
        {
            var reason = outcome.Found ? outcome.Reason : "character_not_found";
            logger.LogInformation(
                "bag_use_reject id={Id} slot={Slot} item={Item} txn={Txn} reason={Reason}",
                characterId, request.Slot, slot.ItemId, txnId, reason);
            return BatchRejected(reason, spec.ItemId, (int)outcome.CountLeft, outcome.Duplicate);
        }

        logger.LogInformation(
            "bag_use id={Id} slot={Slot} item={Item} txn={Txn} used=1 left={Left}",
            characterId, request.Slot, spec.ItemId, txnId, outcome.CountLeft);

        return new UseItemBatchResponse
        {
            Ok = true,
            Reason = "",
            ItemId = spec.ItemId,
            Used = 1,
            CountLeft = (int)outcome.CountLeft,
            Duplicate = false,
        };
    }


    static UseItemBatchResponse BatchRejected(
        string reason, string itemId, int countLeft, bool duplicate) =>
        new()
        {
            Ok = false,
            Reason = reason,
            ItemId = itemId,
            Used = 0,
            CountLeft = countLeft,
            Duplicate = duplicate,
        };


    static InventorySlot ToProtoSlot(BagSlot slot) =>
        new()
        {
            Slot = (uint)Math.Max(0, slot.Slot),
            ItemId = slot.ItemId,
            Count = slot.Count,
            Bound = slot.Bound,
        };


    // ------------------------------------------------------------------ Q10 背包操作

    /// <summary>批量使用认的种类：只认消耗品，装备、材料、凭证都不许「使用」。</summary>
    const string UsableKind = "consumable";
}
