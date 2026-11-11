using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// 建角色的入口。账号从令牌里取，不信请求体——客户端改不了自己是谁。
/// 真正的检查与落库在角色服（角色的唯一写入），这里只做鉴权、转发和错误码翻译。
/// </summary>
public static class CharacterApi
{
    public sealed record CreateBody(string? Name, string? Race, string? Appearance, string? ClassId, string? Branch);

    public sealed record LevelBody(ulong? CharacterId, int? Level);

    public sealed record AddItemBody(ulong? CharacterId, uint? Slot, string? ItemId, int? Count);

    public sealed record CanTradeBody(ulong? CharacterId, uint? Slot);

    public sealed record CurrencyBody(ulong? CharacterId, string? Kind, long? Delta);

    public sealed record GrantEntryBody(CurrencyDeltaBody? Currency, ItemDeltaBody? Item);

    public sealed record CurrencyDeltaBody(string? Kind, long? Delta);

    public sealed record ItemDeltaBody(uint? Slot, string? ItemId, int? Count);

    public sealed record GrantBody(ulong? CharacterId, string? TxnId, List<GrantEntryBody>? Entries);

    public sealed record EnterDungeonBody(ulong? CharacterId, string? DungeonId, uint? PartySize);

    public sealed record HotSpringBody(ulong? CharacterId);

    // 经验结算请求里只有角色、事务号、来源与人数，没有经验数值：
    // 客户端多传一个 exp_gain 也进不了角色服的那一笔。
    public sealed record SettleExpBody(
        ulong? CharacterId, string? TxnId, string? Source, uint? PartySize);

    // 强化请求里只有角色、格子、用哪种石头和「这一手用不用保护券」：等级、
    // 成功率、消耗、结果、券编号 / 数量 / 来源与事务号都读不进来
    // （事务号由网关生成），多传这些字段也不会被反序列化到请求里。
    public sealed record EnhanceBody(
        ulong? CharacterId, uint? Slot, string? StoneId, bool? UseCoupon);

    // 转移请求里只有角色和两个格子：两格等级、绑定状态、转移石编号 / 张数、
    // 金币费用、结果与事务号都读不进来（事务号由网关生成）。
    public sealed record TransferBody(ulong? CharacterId, uint? FromSlot, uint? ToSlot);

    // 装上结晶只带装备格、结晶所在背包格与镶嵌位号：类别、部位、固定值、
    // 能不能装、结果与事务号都读不进来（事务号由网关生成）。
    public sealed record InstallCrystalBody(
        ulong? CharacterId, uint? Slot, uint? CrystalSlot, uint? Socket);

    // 拆下只带装备格与镶嵌位号，拆下来的结晶由角色服放回背包。
    public sealed record RemoveCrystalBody(ulong? CharacterId, uint? Slot, uint? Socket);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        // 通道建一次复用；地址在启动配置里定死，运行期不变。
        var channel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var client = new CharacterService.CharacterServiceClient(channel);

        app.MapPost("/character", async context =>
        {
            var token = TokenFrom(context.Request);
            if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out var account, out _))
            {
                Console.WriteLine("character_reject reason=token");
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            CreateBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CreateBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var request = new CreateCharacterRequest
            {
                Account = account,
                Name = (body.Name ?? "").Trim(),
                Race = (body.Race ?? "").Trim(),
                Appearance = body.Appearance ?? "",
                ClassId = (body.ClassId ?? "").Trim(),
                Branch = (body.Branch ?? "").Trim(),
            };

            try
            {
                var created = await client.CreateCharacterAsync(
                    request,
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("character_created account=" + account
                    + " id=" + created.CharacterId
                    + " race=" + created.Race
                    + " class=" + created.ClassId
                    + " branch=" + created.Branch
                    + " main_stage_empty=" + created.MainStageEmpty
                    + " alignment_empty=" + created.AlignmentEmpty);

                await WriteAsync(context, 200, new
                {
                    character_id = created.CharacterId,
                    name = created.Name,
                    race = created.Race,
                    appearance = created.Appearance,
                    @class = created.ClassId,
                    branch = created.Branch,
                    level = created.Level,
                    realm = created.Realm,
                    main_stage_empty = created.MainStageEmpty,
                    alignment_empty = created.AlignmentEmpty,
                });
            }
            catch (RpcException ex)
            {
                var code = ex.Status.Detail;
                var http = ex.StatusCode switch
                {
                    StatusCode.InvalidArgument => 400,
                    StatusCode.NotFound => 404,
                    StatusCode.AlreadyExists => 409,
                    StatusCode.Unavailable => 503,
                    _ => 502,
                };

                Console.WriteLine("character_reject account=" + account
                    + " code=" + code + " http=" + http);
                await WriteAsync(context, http, new { error = code });
            }
        });

        // 写等级。上限 100 由角色服判，网关只转；越界是 400，不是静默改成 100。
        app.MapPost("/character/level", async context =>
        {
            var token = TokenFrom(context.Request);
            if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out var account, out _))
            {
                Console.WriteLine("level_reject reason=token");
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            LevelBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<LevelBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body is null || body.CharacterId is null || body.Level is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var updated = await client.SetLevelAsync(
                    new SetLevelRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Level = body.Level.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("level_updated account=" + account
                    + " id=" + updated.CharacterId
                    + " level=" + updated.Level);

                await WriteAsync(context, 200, new
                {
                    character_id = updated.CharacterId,
                    level = updated.Level,
                });
            }
            catch (RpcException ex)
            {
                var code = ex.Status.Detail;
                var http = ex.StatusCode switch
                {
                    StatusCode.InvalidArgument => 400,
                    StatusCode.NotFound => 404,
                    StatusCode.Unavailable => 503,
                    _ => 502,
                };

                Console.WriteLine("level_reject account=" + account
                    + " code=" + code + " http=" + http);
                await WriteAsync(context, http, new { error = code });
            }
        });

        // 往背包里放东西。绑定不绑定由物品表定，请求里说不了。
        app.MapPost("/inventory/add", async context =>
        {
            if (!TryAuthorize(context, tokens, "inventory_add", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            AddItemBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<AddItemBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.Slot is null || body.Count is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var slot = await client.AddItemAsync(
                    new AddItemRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        ItemId = body.ItemId ?? "",
                        Count = body.Count.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("inventory_add account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + slot.Slot
                    + " item=" + slot.ItemId
                    + " count=" + slot.Count
                    + " bound=" + slot.Bound);

                await WriteAsync(context, 200, new
                {
                    slot = slot.Slot,
                    item_id = slot.ItemId,
                    count = slot.Count,
                    bound = slot.Bound,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_add", account, ex);
            }
        });

        // 读整个背包。
        app.MapGet("/inventory", async context =>
        {
            if (!TryAuthorize(context, tokens, "inventory_list", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var list = await client.ListInventoryAsync(
                    new ListInventoryRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    bag_slots = list.BagSlots,
                    slots = list.Slots.Select(s => new
                    {
                        slot = s.Slot,
                        item_id = s.ItemId,
                        count = s.Count,
                        bound = s.Bound,
                    }),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_list", account, ex);
            }
        });

        // 这一格能不能直接交易。绑定物品这里给 false + bound_item。
        app.MapPost("/inventory/can-trade", async context =>
        {
            if (!TryAuthorize(context, tokens, "inventory_trade", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            CanTradeBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CanTradeBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.Slot is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var can = await client.CanTradeAsync(
                    new CanTradeRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("inventory_trade account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " tradable=" + can.Tradable
                    + " reason=" + can.Reason);

                await WriteAsync(context, 200, new
                {
                    tradable = can.Tradable,
                    reason = can.Reason,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "inventory_trade", account, ex);
            }
        });

        // 货币加减。金币、灵石五档、元宝同一个口子，扣成负数由角色服拒。
        app.MapPost("/currency/add", async context =>
        {
            if (!TryAuthorize(context, tokens, "currency_add", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            CurrencyBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CurrencyBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.Delta is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var balance = await client.AddCurrencyAsync(
                    new AddCurrencyRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Kind = body.Kind ?? "",
                        Delta = body.Delta.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("currency account=" + account
                    + " id=" + body.CharacterId.Value
                    + " kind=" + balance.Kind
                    + " delta=" + body.Delta.Value
                    + " amount=" + balance.Amount);

                await WriteAsync(context, 200, new
                {
                    kind = balance.Kind,
                    amount = balance.Amount,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "currency_add", account, ex);
            }
        });

        // 读回全部币种，没记过账的按 0。
        app.MapGet("/currency", async context =>
        {
            if (!TryAuthorize(context, tokens, "currency_get", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var balances = await client.GetCurrencyAsync(
                    new GetCurrencyRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    balances = balances.Balances.Select(b => new { kind = b.Kind, amount = b.Amount }),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "currency_get", account, ex);
            }
        });

        // 发一笔奖。整笔在角色服的一个事务里，事务号重复就不发第二次。
        app.MapPost("/grant", async context =>
        {
            if (!TryAuthorize(context, tokens, "grant", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            GrantBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<GrantBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.Entries is null || body.Entries.Count == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var request = new ApplyGrantRequest
            {
                Account = account,
                CharacterId = body.CharacterId.Value,
                TxnId = (body.TxnId ?? "").Trim(),
            };

            foreach (var entry in body.Entries)
            {
                if (entry.Currency is not null)
                {
                    request.Entries.Add(new Mmo.Proto.Character.GrantEntry
                    {
                        Currency = new CurrencyDelta
                        {
                            Kind = entry.Currency.Kind ?? "",
                            Delta = entry.Currency.Delta ?? 0,
                        },
                    });
                }
                else if (entry.Item is not null && entry.Item.Slot is not null)
                {
                    request.Entries.Add(new Mmo.Proto.Character.GrantEntry
                    {
                        Item = new ItemDelta
                        {
                            Slot = entry.Item.Slot.Value,
                            ItemId = entry.Item.ItemId ?? "",
                            Count = entry.Item.Count ?? 0,
                        },
                    });
                }
                else
                {
                    await WriteAsync(context, 400, new { error = "entry_invalid" });
                    return;
                }
            }

            try
            {
                var result = await client.ApplyGrantAsync(
                    request,
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("grant account=" + account
                    + " id=" + body.CharacterId.Value
                    + " txn=" + request.TxnId
                    + " duplicate=" + result.Duplicate
                    + " entries=" + request.Entries.Count);

                await WriteAsync(context, 200, new
                {
                    duplicate = result.Duplicate,
                    balances = result.Balances.Select(b => new { kind = b.Kind, amount = b.Amount }),
                    slots = result.Slots.Select(s => new
                    {
                        slot = s.Slot,
                        item_id = s.ItemId,
                        count = s.Count,
                        bound = s.Bound,
                    }),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "grant", account, ex);
            }
        });

        // 进本。主线 1 人、不扣疲劳不占份额；人数与规则都在角色服的副本表里。
        app.MapPost("/dungeon/enter", async context =>
        {
            if (!TryAuthorize(context, tokens, "dungeon_enter", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            EnterDungeonBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<EnterDungeonBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.PartySize is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var result = await client.EnterDungeonAsync(
                    new EnterDungeonRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        DungeonId = body.DungeonId ?? "main",
                        PartySize = body.PartySize.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("dungeon_enter account=" + account
                    + " id=" + body.CharacterId.Value
                    + " dungeon=" + (body.DungeonId ?? "main")
                    + " party=" + body.PartySize.Value
                    + " allowed=" + result.Allowed
                    + " reason=" + result.Reason
                    + " fatigue=" + result.Fatigue
                    + " cost=" + result.FatigueCost
                    + " cap=" + result.FatigueDailyCap
                    + " share=" + result.WeeklyShare
                    + " share_cost=" + result.WeeklyShareCost
                    + " share_left=" + result.WeeklyShareRemaining
                    + " rewards=" + result.RewardShares);

                await WriteAsync(context, 200, new
                {
                    allowed = result.Allowed,
                    reason = result.Reason,
                    max_players = result.MaxPlayers,
                    fatigue = result.Fatigue,
                    weekly_share = result.WeeklyShare,
                    entry_id = result.EntryId,
                    fatigue_cost = result.FatigueCost,
                    fatigue_daily_cap = result.FatigueDailyCap,
                    weekly_share_cost = result.WeeklyShareCost,
                    weekly_share_remaining = result.WeeklyShareRemaining,
                    weekly_share_cap = result.WeeklyShareCap,
                    reward_shares = result.RewardShares,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "dungeon_enter", account, ex);
            }
        });

        // 温泉。恢复多少由角色服按配置定，请求里只有角色编号；网关不碰库。
        app.MapPost("/hot-spring", async context =>
        {
            if (!TryAuthorize(context, tokens, "hot_spring", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            HotSpringBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<HotSpringBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var result = await client.UseHotSpringAsync(
                    new UseHotSpringRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("hot_spring account=" + account
                    + " id=" + body.CharacterId.Value
                    + " before=" + result.FatigueBefore
                    + " recovered=" + result.Recovered
                    + " fatigue=" + result.Fatigue
                    + " cap=" + result.FatigueDailyCap
                    + " share=" + result.WeeklyShare);

                await WriteAsync(context, 200, new
                {
                    fatigue_before = result.FatigueBefore,
                    recovered = result.Recovered,
                    fatigue = result.Fatigue,
                    fatigue_daily_cap = result.FatigueDailyCap,
                    recovery_limit = result.RecoveryLimit,
                    weekly_share = result.WeeklyShare,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "hot_spring", account, ex);
            }
        });

        // 等级经验结算。账号从令牌取，数值全在角色服的 config/exp.json；
        // 网关只鉴权与转发，多传的经验/加成字段不会被读进请求。
        app.MapPost("/exp/settle", async context =>
        {
            if (!TryAuthorize(context, tokens, "exp_settle", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            SettleExpBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SettleExpBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.PartySize is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var result = await client.SettleExpAsync(
                    new SettleExpRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = (body.TxnId ?? "").Trim(),
                        Source = (body.Source ?? "").Trim(),
                        PartySize = body.PartySize.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("exp_settle account=" + account
                    + " id=" + body.CharacterId.Value
                    + " txn=" + (body.TxnId ?? "").Trim()
                    + " source=" + (body.Source ?? "").Trim()
                    + " party=" + body.PartySize.Value
                    + " duplicate=" + result.Duplicate
                    + " base=" + result.BaseExp
                    + " bonus=" + result.PartyBonus
                    + " gain=" + result.ExpGain
                    + " level=" + result.LevelBefore + "->" + result.Level
                    + " exp=" + result.Exp
                    + " cap=" + result.LevelCap);

                await WriteAsync(context, 200, new
                {
                    duplicate = result.Duplicate,
                    base_exp = result.BaseExp,
                    party_bonus = result.PartyBonus,
                    exp_gain = result.ExpGain,
                    level_before = result.LevelBefore,
                    level = result.Level,
                    exp = result.Exp,
                    level_cap = result.LevelCap,
                    at_cap = result.AtCap,
                    party_size = result.PartySize,
                    source = result.Source,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "exp_settle", account, ex);
            }
        });

        // 强化。三档材料、成功率与金币消耗全在角色服的 config/enhance.json；
        // 网关只认令牌、复核归属、生成一个事务号，再把请求转过去。
        // P1-4b 的 use_coupon 只是一个开关：券的编号、数量、来源都不在请求里，
        // 角色服按配置里唯一那一种券从背包扣。业务拒绝（材料不对、到顶、材料或金币
        // 不够、券不够）也是 200 + ok=false。
        app.MapPost("/character/enhance", async context =>
        {
            if (!TryAuthorize(context, tokens, "enhance", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            EnhanceBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<EnhanceBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.Slot is null || body.Slot == 0
                || string.IsNullOrWhiteSpace(body.StoneId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(client, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                // 一次点击一个号：客户端带不进来，也不能靠重放扣两次材料。
                var txnId = "enhance:" + Guid.NewGuid().ToString("N");
                var result = await client.EnhanceAsync(
                    new EnhanceRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        StoneId = body.StoneId!.Trim(),
                        TxnId = txnId,
                        UseCoupon = body.UseCoupon ?? false,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("enhance account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " stone=" + body.StoneId!.Trim()
                    + " txn=" + txnId
                    + " coupon=" + (body.UseCoupon ?? false)
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " level=" + result.LevelBefore + "->" + result.Level
                    + " success=" + result.Success
                    + " tier=" + result.Tier
                    + " protected=" + result.ProtectedLevel
                    + " coupon_used=" + result.CouponUsed
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    level_before = result.LevelBefore,
                    level = result.Level,
                    max_level = result.MaxLevel,
                    success = result.Success,
                    tier = result.Tier,
                    required_stone = result.RequiredStone,
                    stone_cost = result.StoneCost,
                    gold_cost = result.GoldCost,
                    gold = result.Gold,
                    stone_left = result.StoneLeft,
                    duplicate = result.Duplicate,
                    success_rate = result.SuccessRate,
                    protected_level = result.ProtectedLevel,
                    coupon_used = result.CouponUsed,
                    coupon_left = result.CouponLeft,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "enhance", account, ex);
            }
        });

        // 强化转移（方案 9.3）。转移石、费用、两格等级与绑定规则全在角色服；
        // 网关只认令牌、复核归属、生成一个事务号，再把两个格子转过去。
        // 业务拒绝（来源不高于目标、格子空、不是装备、石头或金币不够）也是 200 + ok=false。
        app.MapPost("/character/transfer", async context =>
        {
            if (!TryAuthorize(context, tokens, "transfer", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            TransferBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<TransferBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.FromSlot is null || body.FromSlot == 0
                || body.ToSlot is null || body.ToSlot == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(client, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                // 一次点击一个号：客户端带不进来，也不能靠重放扣两次材料。
                var txnId = "transfer:" + Guid.NewGuid().ToString("N");
                var result = await client.TransferEnhanceAsync(
                    new TransferEnhanceRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        FromSlot = body.FromSlot.Value,
                        ToSlot = body.ToSlot.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("transfer account=" + account
                    + " id=" + body.CharacterId.Value
                    + " from=" + body.FromSlot.Value
                    + " to=" + body.ToSlot.Value
                    + " txn=" + txnId
                    + " ok=" + result.Ok
                    + " reason=" + result.Reason
                    + " level=" + result.FromLevelBefore + ":" + result.ToLevelBefore
                    + "->" + result.FromLevel + ":" + result.ToLevel
                    + " bound=" + result.TargetBoundBefore + "->" + result.TargetBound
                    + " duplicate=" + result.Duplicate);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    from_level_before = result.FromLevelBefore,
                    to_level_before = result.ToLevelBefore,
                    from_level = result.FromLevel,
                    to_level = result.ToLevel,
                    max_level = result.MaxLevel,
                    gold_cost = result.GoldCost,
                    gold = result.Gold,
                    stone_item = result.StoneItem,
                    stone_cost = result.StoneCost,
                    stone_left = result.StoneLeft,
                    target_bound_before = result.TargetBoundBefore,
                    target_bound = result.TargetBound,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "transfer", account, ex);
            }
        });

        // 能量结晶装上（方案第 9 章第 3 层）。类别、部位与判定全在角色服；
        // 网关只认令牌、复核归属、生成事务号，再把三个格位编号转过去。
        // 部位不对、位号越界、位已占用等业务拒绝也是 200 + ok=false。
        app.MapPost("/character/crystal/install", async context =>
        {
            if (!TryAuthorize(context, tokens, "crystal_install", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            InstallCrystalBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<InstallCrystalBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.Slot is null || body.Slot == 0
                || body.CrystalSlot is null || body.CrystalSlot == 0
                || body.Socket is null || body.Socket == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(client, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var txnId = "crystal_install:" + Guid.NewGuid().ToString("N");
                var result = await client.InstallCrystalAsync(
                    new InstallCrystalRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        CrystalSlot = body.CrystalSlot.Value,
                        Socket = body.Socket.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("crystal_install account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " crystal_slot=" + body.CrystalSlot.Value
                    + " socket=" + body.Socket.Value
                    + " txn=" + txnId
                    + " ok=" + (result.Ok ? "true" : "false")
                    + " reason=" + result.Reason
                    + " item=" + result.CrystalItem
                    + " part=" + result.Part
                    + " duplicate=" + (result.Duplicate ? "true" : "false"));

                await WriteCrystalAsync(context, result);
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "crystal_install", account, ex);
            }
        });

        // 拆下：结晶回背包复用，不扣金币、不消耗、不改强化与绑定。
        app.MapPost("/character/crystal/remove", async context =>
        {
            if (!TryAuthorize(context, tokens, "crystal_remove", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            RemoveCrystalBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<RemoveCrystalBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || body.Slot is null || body.Slot == 0
                || body.Socket is null || body.Socket == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(client, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var txnId = "crystal_remove:" + Guid.NewGuid().ToString("N");
                var result = await client.RemoveCrystalAsync(
                    new RemoveCrystalRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Slot = body.Slot.Value,
                        Socket = body.Socket.Value,
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("crystal_remove account=" + account
                    + " id=" + body.CharacterId.Value
                    + " slot=" + body.Slot.Value
                    + " socket=" + body.Socket.Value
                    + " txn=" + txnId
                    + " ok=" + (result.Ok ? "true" : "false")
                    + " reason=" + result.Reason
                    + " item=" + result.CrystalItem
                    + " left=" + result.CrystalLeft
                    + " duplicate=" + (result.Duplicate ? "true" : "false"));

                await WriteCrystalAsync(context, result);
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "crystal_remove", account, ex);
            }
        });
    }

    static Task WriteCrystalAsync(HttpContext context, CrystalResponse result) =>
        WriteAsync(context, 200, new
        {
            ok = result.Ok,
            reason = result.Reason,
            installed = result.Installed,
            slot = result.Slot,
            socket = result.Socket,
            crystal_item = result.CrystalItem,
            category = result.Category,
            part = result.Part,
            socket_count = result.SocketCount,
            filled_before = result.FilledBefore,
            filled = result.Filled,
            crystal_left = result.CrystalLeft,
            gold = result.Gold,
            enhance_level = result.EnhanceLevel,
            bound = result.Bound,
            duplicate = result.Duplicate,
        });

    // 复用角色服已有的只读背包口复核归属，不为强化另起一套身份数据。
    static async Task<bool> OwnsAsync(
        CharacterService.CharacterServiceClient client,
        string account, ulong characterId, HttpContext context)
    {
        try
        {
            await client.ListInventoryAsync(
                new ListInventoryRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: context.RequestAborted);
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }

    static bool TryAuthorize(HttpContext context, TokenStore tokens, string what, out string account)    {
        var token = TokenFrom(context.Request);
        if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out account, out _))
        {
            Console.WriteLine(what + "_reject reason=token");
            return false;
        }

        return true;
    }

    static async Task WriteRpcErrorAsync(HttpContext context, string what, string account, RpcException ex)
    {
        var code = ex.Status.Detail;
        var http = ex.StatusCode switch
        {
            StatusCode.InvalidArgument => 400,
            StatusCode.NotFound => 404,
            StatusCode.AlreadyExists => 409,
            // 余额不足这类「前提不满足」走 412，与参数错分开。
            StatusCode.FailedPrecondition => 412,
            StatusCode.Unavailable => 503,
            _ => 502,
        };

        Console.WriteLine(what + "_reject account=" + account + " code=" + code + " http=" + http);
        await WriteAsync(context, http, new { error = code });
    }

    static string? TokenFrom(HttpRequest request)
    {
        var fromQuery = request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(fromQuery))
        {
            return fromQuery;
        }

        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return header[prefix.Length..].Trim();
        }

        return null;
    }

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
