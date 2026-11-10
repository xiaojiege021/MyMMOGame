using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.World;
using CharacterClient = Mmo.Proto.Character.CharacterService.CharacterServiceClient;
using WorldClient = Mmo.Proto.World.WorldService.WorldServiceClient;

namespace Gateway;

/// <summary>
/// 表世界位置流：手机到网关一条 WebSocket + Protobuf，网关到世界服一条 gRPC 双向流。
/// 账号只从令牌取，角色编号从 query 取后先让角色服复核归属，再作为 gRPC 请求头传给世界服；
/// 客户端帧里没有身份、分线号或别人的坐标，网关不转发这些字段。
/// 和 /battle 一样逐帧检查令牌是否过期，位置与在线状态都不在网关落库。
/// </summary>
public static class WorldStreamProxy
{
    const int BufferBytes = 8 * 1024;
    const int MaxMessageBytes = 64 * 1024;

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        app.UseWebSockets();
        app.Map("/world/stream", context => HandleAsync(context, tokens, config));
    }

    static async Task HandleAsync(HttpContext context, TokenStore tokens, GatewayConfig config)
    {
        var token = TokenFrom(context.Request);
        if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out var account, out var expiresAt))
        {
            Console.WriteLine("world_stream_reject reason=token");
            await WriteAsync(context, 401, new { error = "invalid_token" });
            return;
        }

        if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
            || characterId == 0)
        {
            Console.WriteLine("world_stream_reject account=" + account + " reason=bad_character");
            await WriteAsync(context, 400, new { error = "bad_request" });
            return;
        }

        using var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var characters = new CharacterClient(characterChannel);
        try
        {
            // 与 /world/enter 同口径：复核这个角色确实属于这个账号。
            await characters.ListInventoryAsync(
                new ListInventoryRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: context.RequestAborted);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            Console.WriteLine("world_stream_reject account=" + account + " reason=character_not_found");
            await WriteAsync(context, 404, new { error = "character_not_found" });
            return;
        }
        catch (RpcException ex)
        {
            Console.WriteLine("world_stream_reject account=" + account + " reason=upstream " + ex.StatusCode);
            await WriteAsync(context, 503, new { error = "character_upstream_unavailable" });
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await WriteAsync(context, 400, new { error = "websocket_required" });
            return;
        }

        using var worldChannel = GrpcChannel.ForAddress(config.WorldUpstream);
        var world = new WorldClient(worldChannel);
        var client = await context.WebSockets.AcceptWebSocketAsync();
        var sendLock = new SemaphoreSlim(1, 1);

        // 身份只在服与服这一段传：客户端改包也放不进这两个头。
        var headers = new Metadata
        {
            { "x-mmo-account", Convert.ToBase64String(Encoding.UTF8.GetBytes(account)) },
            { "x-mmo-character-id", characterId.ToString(CultureInfo.InvariantCulture) },
        };

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var call = world.PositionStream(headers: headers, cancellationToken: abort.Token);

        Console.WriteLine("world_stream_open account=" + account
            + " character=" + characterId
            + " expires_in_s=" + (long)(expiresAt - DateTimeOffset.UtcNow).TotalSeconds);

        var toWorld = ClientToWorldAsync(client, call, expiresAt, sendLock, abort.Token);
        var toClient = WorldToClientAsync(client, call, sendLock, abort.Token);
        await Task.WhenAny(toWorld, toClient);
        await abort.CancelAsync();
        try
        {
            await call.RequestStream.CompleteAsync();
        }
        catch (Exception)
        {
            // 上游已经断开时补这一句没有意义，收尾不因此失败。
        }

        await CloseQuietlyAsync(client, WebSocketCloseStatus.NormalClosure, "bye", sendLock);
        try
        {
            await Task.WhenAll(toWorld, toClient);
        }
        catch (Exception)
        {
            // 取消与断连在收尾时是常态。
        }

        Console.WriteLine("world_stream_close account=" + account + " character=" + characterId);
    }

    static async Task ClientToWorldAsync(
        WebSocket client,
        AsyncDuplexStreamingCall<WorldClientFrame, WorldServerFrame> call,
        DateTimeOffset expiresAt,
        SemaphoreSlim sendLock,
        CancellationToken stopping)
    {
        var buffer = new byte[BufferBytes];
        using var assembled = new MemoryStream();
        try
        {
            while (!stopping.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                assembled.SetLength(0);
                WebSocketReceiveResult received;
                do
                {
                    received = await client.ReceiveAsync(new ArraySegment<byte>(buffer), stopping);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    assembled.Write(buffer, 0, received.Count);
                    if (assembled.Length > MaxMessageBytes)
                    {
                        Console.WriteLine("world_stream_drop reason=frame_too_large bytes=" + assembled.Length);
                        return;
                    }
                }
                while (!received.EndOfMessage);

                if (received.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }

                // 与 P1-1a2 的 /battle 同口径：长连接逐帧看令牌是否还有效。
                if (DateTimeOffset.UtcNow >= expiresAt)
                {
                    Console.WriteLine("world_stream_expired token_rejected=1");
                    await CloseQuietlyAsync(
                        client, WebSocketCloseStatus.PolicyViolation, "token_expired", sendLock);
                    return;
                }

                WorldClientFrame frame;
                try
                {
                    frame = WorldClientFrame.Parser.ParseFrom(assembled.ToArray());
                }
                catch (InvalidProtocolBufferException)
                {
                    Console.WriteLine("world_stream_drop reason=bad_frame");
                    return;
                }

                // 只转发位置上报；别的帧不交给世界服。
                if (frame.Position is not null)
                {
                    await call.RequestStream.WriteAsync(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine("world_stream_client_error=" + ex.WebSocketErrorCode);
        }
        catch (Exception ex)
        {
            Console.WriteLine("world_stream_client_error=" + ex.GetType().Name);
        }
    }

    static async Task WorldToClientAsync(
        WebSocket client,
        AsyncDuplexStreamingCall<WorldClientFrame, WorldServerFrame> call,
        SemaphoreSlim sendLock,
        CancellationToken stopping)
    {
        try
        {
            await foreach (var frame in call.ResponseStream.ReadAllAsync(stopping))
            {
                var bytes = frame.ToByteArray();
                await sendLock.WaitAsync(stopping);
                try
                {
                    await client.SendAsync(
                        new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, stopping);
                }
                finally
                {
                    sendLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine("world_stream_upstream_error=" + ex.GetType().Name);
        }
    }

    static async Task CloseQuietlyAsync(
        WebSocket socket, WebSocketCloseStatus status, string reason, SemaphoreSlim sendLock)
    {
        if (socket.State != WebSocketState.Open && socket.State != WebSocketState.CloseReceived)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await sendLock.WaitAsync(timeout.Token);
            try
            {
                await socket.CloseAsync(status, reason, timeout.Token);
            }
            finally
            {
                sendLock.Release();
            }
        }
        catch (Exception)
        {
            // 对面已经断了，收尾不因此失败。
        }
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
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(body);
    }
}
