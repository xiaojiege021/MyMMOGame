using System.Text;
using Grpc.Core;
using Mmo.Proto.World;

namespace World;

/// <summary>
/// 表世界分线、位置、采集的 gRPC 入口。区域与人物编号全从配置读，请求里带不进
/// 分线号、人数、容量或采集产物：客户端改包改不了自己被分到哪条线，也改不了产物。
/// 这里不结算技能，也不写角色库；采集成功的产物由角色服的 ApplyGrant 落库。
/// </summary>
public sealed class WorldGrpcService(
    LineRegistry registry, WorldCatalog catalog, PositionHub hub,
    GatherTable gather, GatherNodeRegistry nodes, IGatherGrant grants,
    FlightGate flight)
    : global::Mmo.Proto.World.WorldService.WorldServiceBase
{
    public override Task<EnterLineResponse> EnterLine(
        EnterLineRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var regionId = (request.RegionId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0 || regionId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        // 名字是网关复核归属后从角色服取的只读文本，客户端请求体里没有这个字段。
        var result = registry.Enter(account, request.CharacterId, regionId, request.DisplayName);
        Console.WriteLine("world_enter account=" + account
            + " character=" + request.CharacterId
            + " region=" + regionId
            + " entered=" + result.Entered
            + " reason=" + result.Reason
            + " line=" + result.LineId
            + " size=" + result.LineSize
            + " cap=" + result.Capacity
            + " duplicate=" + result.Duplicate);

        var response = new EnterLineResponse
        {
            Entered = result.Entered,
            Reason = result.Reason,
            LineId = result.LineId,
            RegionId = result.RegionId,
            LineNo = (uint)result.LineNo,
            LineSize = (uint)result.LineSize,
            LineCapacity = (uint)result.Capacity,
            Duplicate = result.Duplicate,
        };

        if (result.Entered)
        {
            var region = catalog.Find(regionId);
            if (region is not null)
            {
                response.Neighbors.AddRange(region.Neighbors);
                response.DialogueIds.AddRange(catalog.DialogueIdsFor(regionId));
            }
        }

        return Task.FromResult(response);
    }

    public override Task<LeaveLineResponse> LeaveLine(
        LeaveLineRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var result = registry.Leave(account, request.CharacterId);
        Console.WriteLine("world_leave account=" + account
            + " character=" + request.CharacterId
            + " left=" + result.Left
            + " line=" + result.LineId
            + " size=" + result.LineSize);

        return Task.FromResult(new LeaveLineResponse
        {
            Left = result.Left,
            LineId = result.LineId,
            LineSize = (uint)result.LineSize,
        });
    }

    public override Task<ListRegionsResponse> ListRegions(
        ListRegionsRequest request, ServerCallContext context)
    {
        var response = new ListRegionsResponse { Version = (uint)catalog.Version };
        foreach (var region in catalog.Regions)
        {
            var info = new RegionInfo
            {
                Id = region.Id,
                Name = region.Name,
                Open = region.Open,
            };
            info.Chapters.AddRange(region.Chapters.Select(c => (uint)c));
            info.Neighbors.AddRange(region.Neighbors);
            info.DialogueIds.AddRange(catalog.DialogueIdsFor(region.Id));
            response.Regions.Add(info);
        }

        response.Speakers.AddRange(catalog.Speakers);
        response.Factions.AddRange(catalog.Factions);
        return Task.FromResult(response);
    }

    /// <summary>
    /// 采矿与采药。方案 9.5：矿脉给矿石、药草给药材，采集不耗疲劳；
    /// 灵石矿脉归主城辖区，不开放给玩家个人去挖，对它的请求一律拒绝。
    /// 成功与否、节点占用与刷新都在这里判，产物交给角色服的 ApplyGrant 写背包。
    /// 请求里只有节点编号，产物、数量、背包格、事务号与身份都带不进来。
    /// </summary>
    public override async Task<GatherResponse> Gather(
        GatherRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var nodeId = (request.NodeId ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0 || nodeId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var node = gather.Find(nodeId);
        if (node is null)
        {
            return Reject(account, request.CharacterId, nodeId, "unknown_node");
        }

        // 灵石矿脉先判：不管人在哪、节点是否被占，个人采集都不成立，
        // 也不会走到角色服那笔发奖。灵石仍走主城任务，不进采集背包。
        if (string.Equals(node.Kind, GatherTable.SpiritStone, StringComparison.Ordinal))
        {
            return Reject(account, request.CharacterId, nodeId, "spirit_stone_forbidden");
        }

        var region = registry.RegionOf(account, request.CharacterId);
        if (region is null)
        {
            return Reject(account, request.CharacterId, nodeId, "not_in_line");
        }

        if (!string.Equals(region, node.RegionId, StringComparison.Ordinal))
        {
            return Reject(account, request.CharacterId, nodeId, "wrong_region");
        }

        if (!nodes.TryClaim(nodeId, node.RespawnSeconds, DateTimeOffset.UtcNow))
        {
            return Reject(account, request.CharacterId, nodeId, "node_busy");
        }

        // 每笔一个号，角色服的 grant_txn 主键挡住重放；号只由世界服生成。
        var txnId = "gather:" + Guid.NewGuid().ToString("N");
        var result = await grants.GrantAsync(
            account, request.CharacterId, txnId, node, context.CancellationToken);
        if (!result.Ok)
        {
            // 角色服没写成：把占用还回去，不回一个假的成功。
            nodes.Release(nodeId);
            return Reject(account, request.CharacterId, nodeId, result.Reason);
        }

        Console.WriteLine("gather account=" + account
            + " character=" + request.CharacterId
            + " node=" + nodeId
            + " kind=" + node.Kind
            + " item=" + node.ItemId
            + " count=" + node.Count
            + " slot=" + node.BagSlot
            + " txn=" + txnId
            + " duplicate=" + result.Duplicate);

        return new GatherResponse
        {
            Ok = true,
            NodeId = nodeId,
            Kind = node.Kind,
            ItemId = node.ItemId,
            Count = node.Count,
            BagSlot = (uint)node.BagSlot,
            TxnId = txnId,
            Duplicate = result.Duplicate,
            RespawnS = node.RespawnSeconds,
        };
    }

    static GatherResponse Reject(string account, ulong characterId, string nodeId, string reason)
    {
        Console.WriteLine("gather_reject account=" + account
            + " character=" + characterId
            + " node=" + nodeId
            + " reason=" + reason);
        // 被拒时未生效的产物字段一律是 0 / 空，不给客户端留一份「看起来发了」的回包。
        return new GatherResponse { Ok = false, Reason = reason, NodeId = nodeId };
    }

    /// <summary>
    /// 飞行请求。客户端只给想不想飞，账号从网关来、境界由世界服问角色服；
    /// 权限不够就 ok=false + flight_forbidden，并把 y 拉回地面高度。
    /// </summary>
    public override async Task<FlyResponse> Fly(FlyRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var decision = await flight.FlyAsync(
            account, request.CharacterId, request.WantFly, context.CancellationToken);

        Console.WriteLine("world_fly account=" + account
            + " character=" + request.CharacterId
            + " want=" + request.WantFly
            + " ok=" + decision.Ok
            + " reason=" + decision.Reason
            + " realm=" + decision.Realm
            + " required=" + decision.RequiredRealm
            + " flying=" + decision.Flying
            + " can_fly=" + decision.CanFly
            + " y=" + decision.Y);

        return new FlyResponse
        {
            Ok = decision.Ok,
            Reason = decision.Reason,
            Flying = decision.Flying,
            CanFly = decision.CanFly,
            Realm = decision.Realm,
            RequiredRealm = decision.RequiredRealm,
            Y = decision.Y,
        };
    }

    /// <summary>
    /// 跨区请求。当前区域由分线注册表给，相连、开放与境界门槛都在服务端判；
    /// 被拉回时回包里的 region_id / line_id 仍是原来那一块、那一条。
    /// </summary>
    public override async Task<CrossRegionResponse> CrossRegion(
        CrossRegionRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var target = (request.TargetRegion ?? "").Trim();
        if (account.Length == 0 || request.CharacterId == 0 || target.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var decision = await flight.CrossAsync(
            account, request.CharacterId, target, context.CancellationToken);

        Console.WriteLine("world_cross account=" + account
            + " character=" + request.CharacterId
            + " target=" + target
            + " ok=" + decision.Ok
            + " reason=" + decision.Reason
            + " realm=" + decision.Realm
            + " required=" + decision.RequiredRealm
            + " region=" + (decision.Line?.RegionId ?? "")
            + " line=" + (decision.Line?.LineId ?? ""));

        return new CrossRegionResponse
        {
            Ok = decision.Ok,
            Reason = decision.Reason,
            RegionId = decision.Line?.RegionId ?? "",
            LineId = decision.Line?.LineId ?? "",
            LineNo = (uint)(decision.Line?.LineNo ?? 0),
            Realm = decision.Realm,
            RequiredRealm = decision.RequiredRealm,
        };
    }

    /// <summary>
    /// 网关到世界服的一条双向流。身份只从 gRPC 请求头读（网关鉴权后放进去），
    /// 客户端帧里没有账号、角色编号或分线号；没进分线的人不给开流。
    /// 一个流只有写回包那一个任务碰 responseStream，避免并发写。
    /// </summary>
    public override async Task PositionStream(
        IAsyncStreamReader<WorldClientFrame> requestStream,
        IServerStreamWriter<WorldServerFrame> responseStream,
        ServerCallContext context)
    {
        if (!TryIdentity(context, out var account, out var characterId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_identity"));
        }

        var subscription = hub.Open(account, characterId);
        if (subscription is null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "not_in_line"));
        }

        Console.WriteLine("position_stream_open account=" + account
            + " character=" + characterId
            + " line=" + subscription.LineId);

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var writer = WriteLoopAsync(subscription, responseStream, abort.Token);
        var reader = ReadLoopAsync(subscription, requestStream, abort.Token);
        try
        {
            await Task.WhenAny(writer, reader);
        }
        finally
        {
            hub.Close(subscription);
            subscription.Frames.Writer.TryComplete();
            await abort.CancelAsync();
            try
            {
                await Task.WhenAll(writer, reader);
            }
            catch (Exception)
            {
                // 断开或取消在收尾时是常态，错误已经在循环里打过。
            }

            Console.WriteLine("position_stream_close account=" + account
                + " character=" + characterId);
        }
    }

    static async Task WriteLoopAsync(
        PositionSubscription subscription,
        IServerStreamWriter<WorldServerFrame> responseStream,
        CancellationToken stopping)
    {
        try
        {
            await foreach (var frame in subscription.Frames.Reader.ReadAllAsync(stopping))
            {
                await responseStream.WriteAsync(frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine("position_stream_write_error=" + ex.GetType().Name);
        }
    }

    async Task ReadLoopAsync(
        PositionSubscription subscription,
        IAsyncStreamReader<WorldClientFrame> requestStream,
        CancellationToken stopping)
    {
        try
        {
            await foreach (var frame in requestStream.ReadAllAsync(stopping))
            {
                if (frame.Position is not { } report)
                {
                    continue;
                }

                var result = hub.Report(subscription, report, DateTimeOffset.UtcNow);
                if (!result.Accepted)
                {
                    Console.WriteLine("position_reject account=" + subscription.Account
                        + " character=" + subscription.CharacterId
                        + " reason=" + result.Reason);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine("position_stream_read_error=" + ex.GetType().Name);
        }
    }

    // 网关把账号用 base64 放进头，兼容中文账号；客户端到网关这一段的帧没有这两个字段。
    static bool TryIdentity(ServerCallContext context, out string account, out ulong characterId)
    {
        account = "";
        characterId = 0;
        var encoded = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-mmo-account")?.Value;
        var rawId = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-mmo-character-id")?.Value;
        if (string.IsNullOrEmpty(encoded)
            || !ulong.TryParse(rawId, out characterId)
            || characterId == 0)
        {
            return false;
        }

        try
        {
            account = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return false;
        }

        return account.Length > 0;
    }
}
