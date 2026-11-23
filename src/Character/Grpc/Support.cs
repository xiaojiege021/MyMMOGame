using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{
    // 领地仓储只在 ApplySiegeResult 里用，不另存一份字段（同一个 primary constructor
    // 参数既初始化字段又被方法捕获会触发 CS9124）。

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

        // Q12/K3：长度、可见字符、敏感词只按 config/name_rules.json 的规则判，
        // 代码里没有第二个长度常量（以前这里写死 16）。
        var verdict = nameRules.Check(name);
        if (!verdict.Ok)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, verdict.Reason));
        }

        name = verdict.Display;

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

        if (await characters.NameTakenAsync(verdict.Normalized, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, "name_taken"));
        }

        try
        {
            // P3-4b：账号角色位在同一笔事务里判（一个账号默认一位，买到第二角色栏才两位）。
            // 名额由角色服读配置 + 库里那一行，客户端表达不了「我还有没有位」。
            var outcome = await characters.CreateAsync(
                account,
                name,
                verdict.Normalized,
                race,
                appearance,
                classId,
                branch.Length == 0 ? null : branch,
                characterSlotTable.BaseSlots,
                context.CancellationToken);

            if (!outcome.Ok || outcome.Created is null)
            {
                if (string.Equals(outcome.Reason, "slot_full", StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "character_reject account={Account} name={Name} reason=slot_full base_slots={Base}",
                        account, name, characterSlotTable.BaseSlots);
                    throw new RpcException(new Status(StatusCode.AlreadyExists, "slot_full"));
                }

                // 登记表主键撞了：两个同名并发建号，后到的那一笔整笔回滚。
                if (string.Equals(outcome.Reason, "name_taken", StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "character_reject account={Account} name={Name} reason=name_taken",
                        account, name);
                    throw new RpcException(new Status(StatusCode.AlreadyExists, "name_taken"));
                }

                throw new RpcException(new Status(StatusCode.NotFound, "account_not_found"));
            }

            var created = outcome.Created;

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
    /// Q12/K3：改名。名字判定与建号同一份规则（长度、可见字符、敏感词），
    /// 卡在角色服那笔事务里扣——网关只转请求，不带「用了哪张卡」这种客户端能编的字段。
    /// 业务拒绝（重名、没卡、名字没变、重放）一律回 ok=false + 原因，不改库里的任何值；
    /// 只有请求本身不成立（缺角色号 / 缺事务号 / 角色不属于这个账号）才抛 RpcException。
    /// </summary>
    public override async Task<RenameCharacterResponse> RenameCharacter(
        RenameCharacterRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var txnId = (request.TxnId ?? "").Trim();
        if (txnId.Length == 0 || txnId.Length > 64)
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

        // 规则里关了改名就到此为止，连卡都不看。
        if (!nameRules.AllowRename)
        {
            return new RenameCharacterResponse
            {
                Ok = false,
                Reason = NameRulesTable.ReasonRenameDisabled,
                Name = "",
                ItemId = nameRules.RenameItemId,
                CountLeft = 0,
                Duplicate = false,
            };
        }

        var verdict = nameRules.Check(request.NewName);
        if (!verdict.Ok)
        {
            logger.LogInformation(
                "character_rename_reject id={Id} account={Account} reason={Reason}",
                characterId, account, verdict.Reason);
            return new RenameCharacterResponse
            {
                Ok = false,
                Reason = verdict.Reason,
                Name = "",
                ItemId = nameRules.RenameItemId,
                CountLeft = 0,
                Duplicate = false,
            };
        }

        var outcome = await characters.RenameAsync(
            inventory,
            grants,
            account,
            characterId,
            verdict.Display,
            verdict.Normalized,
            nameRules.RenameItemId,
            txnId,
            context.CancellationToken);

        if (!outcome.Ok)
        {
            logger.LogInformation(
                "character_rename_reject id={Id} account={Account} reason={Reason}",
                characterId, account, outcome.Reason);
            return new RenameCharacterResponse
            {
                Ok = false,
                Reason = outcome.Reason,
                Name = outcome.Name,
                ItemId = nameRules.RenameItemId,
                CountLeft = Math.Max(0, outcome.CountLeft),
                Duplicate = string.Equals(outcome.Reason, "duplicate", StringComparison.Ordinal),
            };
        }

        logger.LogInformation(
            "character_renamed id={Id} account={Account} name={Name} card_left={Left}",
            characterId, account, outcome.Name, outcome.CountLeft);

        return new RenameCharacterResponse
        {
            Ok = true,
            Reason = "",
            Name = outcome.Name,
            ItemId = nameRules.RenameItemId,
            CountLeft = (int)outcome.CountLeft,
            Duplicate = false,
        };
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

        // Q7/H1：**有队伍时**人数与成员一律用队伍事实（自报只作提示）；没有队伍就保留
        // 调用方给的同场人数——方案 11 写定的是「同一场至少两个人多一截少量加成」，
        // 那是同场事实，不是队伍事实：世界 Boss 一场 20 人、彼此没组队也要算。
        // 战斗服的服间直连没有账号，人数是它按本场实际人数算的，同样不受影响。
        var settleMemberIds = new List<ulong>(request.MemberIds.Count);
        settleMemberIds.AddRange(request.MemberIds);
        if (account.Length > 0)
        {
            var partyFact = await PartyDirectory.DescribeAsync(
                social, request.CharacterId, context.CancellationToken);
            if (partyFact.InParty)
            {
                partySize = partyFact.Size;
                settleMemberIds.Clear();
                settleMemberIds.AddRange(partyFact.MemberIds);
                logger.LogInformation(
                    "exp_settle_party_fact id={Id} party={Party} size={Size} all_ready={AllReady}",
                    request.CharacterId, partyFact.PartyId, partyFact.Size, partyFact.AllReady);
            }
        }

        // P3-1a2 补正：历练与等级经验走同一笔。副本编号由战斗服服间直连带上，
        // 空串表示不是副本（表世界、活动等），按方案 8.1 不计历练。
        // 击杀数只是个数，历练数值仍然只从 config/dungeons.json 取，请求带不进来。
        var dungeonId = (request.DungeonId ?? "").Trim();
        if (dungeonId.Length > ExpSettlementRepository.MaxDungeonIdLength)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "dungeon_invalid"));
        }

        DungeonDef? dungeon = null;
        if (dungeonId.Length > 0)
        {
            if (!string.Equals(dungeonId, ExpSettlementRepository.WorldBossDropDungeonId,
                    StringComparison.Ordinal))
            {
                dungeon = dungeons.Find(dungeonId);
                if (dungeon is null)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "dungeon_unknown"));
                }
            }
        }

        var qualifiedKills = (int)request.QualifiedKills;

        // 网关转发时先核归属，与前面各行同一个口径；服间直连没有账号，
        // 由战斗服与角色服之间的内网调用保证，仓库里只按角色编号找。
        if (account.Length > 0
            && !await inventory.OwnsAsync(account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        // P2-1c：同宗门加成要问社交服。判 allSameSect 必须放在 DB 行锁事务之外，
        // 否则跨服调用会拖长行锁、还可能死锁。社交服只是「额外一截」的来源：
        // 不可达时按「不是同门」处理（少给那一小截），绝不让整笔结算失败——
        // 结算本身（基础 + 组队加成）不能硬依赖社交服，只加经验、不该阻塞。
        var allSameSect = await ComputeAllSameSectAsync(settleMemberIds, context.CancellationToken);

        var reportedDrops = new List<DropLineDef>();
        foreach (var line in request.Drops)
        {
            var itemId = (line.ItemId ?? "").Trim();
            if (itemId.Length == 0 || line.Count < 1)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "drop_line_invalid"));
            }

            reportedDrops.Add(new DropLineDef(itemId, line.Count));
        }

        if (dungeonId.Length == 0 && reportedDrops.Count > 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "drop_without_dungeon"));
        }

        if (reportedDrops.Count > 0)
        {
            if (dungeonDropTable.IsMissing)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "drop_table_missing"));
            }

            // Q8/H2：need_greed 下一个赢家只拿其中一行，所以这里认「表内子集」：
            // 物品必须在表内、数量与表内一字不差、同一物品不许拆成两行；
            // 只允许少行（少发不构成超发，多报仍然拒）。
            if (!dungeonDropTable.MatchesReportedSubset(dungeonId, reportedDrops))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "drop_invalid"));
            }
        }

        ExpSettlementOutcome outcome;
        try
        {
            outcome = await expSettlements.SettleAsync(
                (long)request.CharacterId, account.Length == 0 ? null : account,
                txnId, source, partySize, allSameSect, expTable, boosterTable,
                dungeon, qualifiedKills, realms, grants, items, config,
                reportedDrops, context.CancellationToken);
        }
        catch (ExpSettlementRepository.DropSettlementException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Code));
        }

        if (!outcome.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        logger.LogInformation(
            "exp_settle account={Account} id={Id} txn={Txn} source={Source} party={Party} "
            + "duplicate={Duplicate} base={Base} bonus={Bonus} same_sect={SameSect} gain={Gain} "
            + "level={LevelBefore}->{Level} exp={Exp} "
            + "booster_pct={BoosterPct} booster_exp={BoosterExp} "
            + "dungeon={Dungeon} kills={Kills} drill_gain={DrillGain} drill={Drill} "
            + "drill_reason={DrillReason} drop_lines={DropLines} drop_reason={DropReason}",
            account.Length == 0 ? "service" : account, request.CharacterId, txnId, source, partySize,
            outcome.Duplicate, outcome.BaseExp, outcome.PartyBonus, outcome.SameSectBonus, outcome.ExpGain,
            outcome.LevelBefore, outcome.Level, outcome.Exp,
            outcome.BoosterPct, outcome.BoosterExp,
            dungeonId.Length == 0 ? "none" : dungeonId, request.QualifiedKills,
            outcome.DrillGain, outcome.Drill, outcome.DrillReason,
            outcome.DropLines, outcome.DropReason);

        // Q6/J4：副本通关给通行证点数。来源按 config/season_pass_progress.json 的副本编号匹配；
        // 没配来源的副本（表世界、世界 Boss、活动）不加点。加点幂等键从这次结算的事件号派生，
        // 所以结算重放只补一次，不会双加。加点失败不回滚经验，只在日志里记一行。
        if (dungeonId.Length > 0)
        {
            var passSource = seasonPassProgressTable.FindByDungeon(dungeonId);
            if (passSource is not null)
            {
                await AwardPassPointsAsync(
                    (long)request.CharacterId, passSource, dungeonId, txnId,
                    context.CancellationToken);
            }
        }

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
            // P2-1c：同宗门这一截额外经验，只作回包事实值。
            SameSectBonus = outcome.SameSectBonus,
            // P3-1a2 补正：这一笔一起写下的历练，都是角色服的事实值。
            DrillGain = outcome.DrillGain,
            Drill = outcome.Drill,
            DrillReason = outcome.DrillReason,
        };
    }


    /// <summary>
    /// P3-3a4：城战结束后的领地写回。战斗服只送服务端确认过的胜方事实值，
    /// 城池编号、抽成比例与归属变更全在角色服从配置与库里读；同一个事务号只写一次。
    /// 这条 RPC 是服间调用，网关不转发，客户端表达不了胜方 / 宗门 / 税率。
    /// </summary>
    public override async Task<ApplySiegeResultResponse> ApplySiegeResult(
        ApplySiegeResultRequest request, ServerCallContext context)
    {
        var txnId = (request.TxnId ?? "").Trim();
        var cityId = (request.CityId ?? "").Trim();
        var outcome = await siegeTerritories.ApplyAsync(
            txnId, cityId, request.MatchId ?? "", request.WinnerTeam,
            request.WinnerSect, context.CancellationToken);

        logger.LogInformation(
            "siege_result_apply city={City} match={Match} txn={Txn} winner_team={Team} "
            + "winner_sect={Sect} ok={Ok} reason={Reason} duplicate={Duplicate} "
            + "owner={Owner} tax_pct={TaxPct}",
            cityId, request.MatchId, txnId, request.WinnerTeam, request.WinnerSect,
            outcome.Ok, outcome.Reason, outcome.Duplicate, outcome.OwnerSect, outcome.TaxPct);

        return new ApplySiegeResultResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            Duplicate = outcome.Duplicate,
            CityId = outcome.CityId,
            OwnerSect = outcome.OwnerSect,
            TaxPct = outcome.TaxPct,
            TxnId = txnId,
        };
    }


    /// <summary>
    /// Q9/H4：记一次逃跑。只认 voluntary / timeout 两个原因，时长由战斗服从
    /// config/reconnect.json 算成毫秒传进来；这里只加到期时刻、不扣任何东西。
    /// 角色不存在、时长 &lt;= 0 都不写，回 ok=false 让战斗服按本机兜底继续。
    /// </summary>
    public override async Task<RecordDeserterResponse> RecordDeserter(
        RecordDeserterRequest request,
        ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            return new RecordDeserterResponse { Ok = false, Reason = "character_missing" };
        }

        var reason = (request.Reason ?? "").Trim();
        if (reason is not ("voluntary" or "timeout"))
        {
            return new RecordDeserterResponse { Ok = false, Reason = "reason_invalid" };
        }

        if (request.PenaltyMs <= 0)
        {
            return new RecordDeserterResponse { Ok = false, Reason = "penalty_off" };
        }

        var fact = await deserters.RecordAsync(
            (long)request.CharacterId, (long)request.SessionId, reason, request.PenaltyMs,
            context.CancellationToken);

        if (!fact.Found)
        {
            return new RecordDeserterResponse { Ok = false, Reason = "character_not_found" };
        }

        logger.LogInformation(
            "deserter_record character={Character} session={Session} reason={Reason} "
            + "penalty_ms={Penalty} remaining_ms={Remaining}",
            request.CharacterId, request.SessionId, fact.Reason, request.PenaltyMs, fact.RemainingMs);

        return new RecordDeserterResponse
        {
            Ok = true,
            RemainingMs = fact.RemainingMs,
            UntilMs = fact.UntilMs,
            SessionId = (ulong)Math.Max(0, fact.SessionId),
        };
    }


    /// <summary>
    /// Q9/H4：这个角色还要等多久才能再进本。只读，不写库、不改任何事实值；
    /// 没被罚过或已经过期都回 0——「还能不能进」只看 remaining_ms 这一个数。
    /// </summary>
    public override async Task<DescribeDeserterResponse> DescribeDeserter(
        DescribeDeserterRequest request,
        ServerCallContext context)
    {
        if (request.CharacterId == 0)
        {
            return new DescribeDeserterResponse { Ok = false };
        }

        var fact = await deserters.DescribeAsync((long)request.CharacterId, context.CancellationToken);
        return new DescribeDeserterResponse
        {
            Ok = true,
            RemainingMs = fact.RemainingMs,
            UntilMs = fact.UntilMs,
            SessionId = (ulong)Math.Max(0, fact.SessionId),
            Reason = fact.Reason,
        };
    }
}
