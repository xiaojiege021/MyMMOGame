using System.Text;
using Grpc.Core;
using Mmo.Proto.World;

namespace World;

/// <summary>
/// 表世界分线的 gRPC 入口。区域与人物编号全从配置读，请求里带不进分线号、
/// 人数或容量：客户端改包改不了自己被分到哪条线。
/// 这里不结算技能、不发奖励，也不写角色库（P1-3b 与战斗服各自负责）。
/// </summary>
public sealed class WorldGrpcService(LineRegistry registry, WorldCatalog catalog, PositionHub hub)
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

        var result = registry.Enter(account, request.CharacterId, regionId);
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
