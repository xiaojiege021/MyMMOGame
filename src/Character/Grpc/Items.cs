using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

    /// <summary>
    /// 用符之前的只读查询：这个编号是不是符、回复落在哪个池，只有角色服说了算。
    /// 战斗服拿到池之后才能做「池是不是满了」「这一组冷却到没到」的本地预判，
    /// 预判不过就根本不扣符。这里不写库、不扣符、不碰生命与蓝量。
    /// </summary>
    /// <summary>
    /// B1：吃丹之前的只读查询。和 DescribeTalisman 同一个切法——编号到「哪个池、
    /// 回多少、哪一组冷却」的映射只在角色服的 config/pill.json，战斗服不解析配置。
    /// 不扣丹、不写库；真正扣不扣由 ConsumePill 那笔事务再判一次。
    /// 突破丹也会被认出来（is_breakthrough 那一支），但战斗服拿到它就拒绝：
    /// 方案写定境界丹在突破事务里扣，战斗里吃它没有任何效果。
    /// </summary>
    public override async Task<DescribePillResponse> DescribePill(
        DescribePillRequest request, ServerCallContext context)
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

        var spec = pillTable.Find(itemId);
        if (spec is null || spec.IsBreakthrough)
        {
            // 不是丹，或者是突破丹（不进战斗）：一个都不给池，也不扣。
            return new DescribePillResponse
            {
                Ok = false,
                Reason = "not_pill",
                ItemId = itemId,
                Pool = PillPool.Unspecified,
                CountLeft = 0,
            };
        }

        var left = await pills.CountAsync(characterId, spec.ItemId, context.CancellationToken);
        return new DescribePillResponse
        {
            Ok = true,
            Reason = "",
            ItemId = spec.ItemId,
            Pool = spec.HpRestore > 0 ? PillPool.Hp : PillPool.Mana,
            CountLeft = left,
        };
    }


    /// <summary>
    /// B1：真扣一颗丹。与 ConsumeTalisman 同一套纪律：先核归属，再看编号是不是丹，
    /// 然后在角色服的一笔事务里扣一格背包（抢 grant_txn 号挡重放）。
    /// 生命与蓝量不在这里——当前值是战斗进程里的战斗态，角色库不存。
    /// 回包带配置里的回复值与冷却组，战斗服照着加，它自己不读配置。
    /// </summary>
    public override async Task<ConsumePillResponse> ConsumePill(
        ConsumePillRequest request, ServerCallContext context)
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
        if (txnId.Length == 0 || txnId.Length > PillRepository.MaxTxnLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        var characterId = (long)request.CharacterId;
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

        var spec = pillTable.Find(itemId);
        if (spec is null || spec.IsBreakthrough)
        {
            var left = await pills.CountAsync(characterId, itemId, context.CancellationToken);
            return new ConsumePillResponse
            {
                Ok = false,
                Reason = "not_pill",
                ItemId = itemId,
                CountLeft = left,
                Pool = PillPool.Unspecified,
            };
        }

        var spend = await pills.SpendAsync(characterId, spec.ItemId, txnId, context.CancellationToken);
        if (!spend.Ok)
        {
            return new ConsumePillResponse
            {
                Ok = false,
                Reason = spend.Reason,
                ItemId = spec.ItemId,
                CountLeft = spend.CountLeft,
                Pool = PillPool.Unspecified,
            };
        }

        if (spend.Duplicate)
        {
            // 同一个号重放：这一笔没扣，颗数给事实值，回复值不再给一遍。
            return new ConsumePillResponse
            {
                Ok = true,
                Reason = "",
                ItemId = spec.ItemId,
                CountLeft = spend.CountLeft,
                Duplicate = true,
                Pool = spec.HpRestore > 0 ? PillPool.Hp : PillPool.Mana,
            };
        }

        return new ConsumePillResponse
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
            Pool = spec.HpRestore > 0 ? PillPool.Hp : PillPool.Mana,
            ManaRestore = spec.ManaRestore,
        };
    }


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
}
