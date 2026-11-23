using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

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
}
