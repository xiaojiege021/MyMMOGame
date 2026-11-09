using System.Net.WebSockets;

namespace Gateway;

/// <summary>
/// 战斗长连接的入口。客户端连网关，不直连战斗服：
///
///   1. 握手先认令牌。没带、没发过、已过期的，握手就 401，连接根本建不起来。
///   2. 握手过了才连战斗服，之后是字节流转发，网关不解析技能消息、不算伤害。
///   3. 连接活着的时候每转发一帧都再看一次有效期：令牌到期，连接立刻关掉，
///      不会出现「握手时有效就一直能发技能」。
///
/// 本行只做鉴权与转发；频控、协议加解密留给后面的网关行。
/// </summary>
public static class BattleProxy
{
    const int BufferBytes = 8 * 1024;

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        app.UseWebSockets();
        app.Map("/battle", context => HandleAsync(context, tokens, config));
    }

    static async Task HandleAsync(HttpContext context, TokenStore tokens, GatewayConfig config)
    {
        var token = TokenFrom(context.Request);
        if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out var account, out var expiresAt))
        {
            Console.WriteLine("proxy_reject reason=token"
                + (string.IsNullOrEmpty(token) ? " (missing)" : " (invalid_or_expired)"));
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_token" });
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            Console.WriteLine("proxy_reject reason=not_websocket account=" + account);
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "websocket_required" });
            return;
        }

        using var client = await context.WebSockets.AcceptWebSocketAsync();
        using var upstream = new ClientWebSocket();
        var abort = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

        // 客户端带的 class / party / dungeon 这些参数要原样交给战斗服，
        // 只把 token 摘掉——上游不需要也不该看到它。
        var upstreamUrl = BuildUpstreamUrl(config.BattleUpstream, context.Request.Query);

        try
        {
            await upstream.ConnectAsync(new Uri(upstreamUrl), abort.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            Console.WriteLine("proxy_reject reason=upstream account=" + account + " " + ex.GetType().Name);
            await CloseQuietlyAsync(client, WebSocketCloseStatus.EndpointUnavailable, "upstream_down");
            return;
        }

        Console.WriteLine("proxy_open account=" + account
            + " expires_in_s=" + (int)(expiresAt - DateTimeOffset.UtcNow).TotalSeconds
            + " upstream=" + config.BattleUpstream);

        var toUpstream = PumpAsync(client, upstream, tokens, token!, expiresAt, abort.Token);
        var toClient = PumpAsync(upstream, client, null, null, default, abort.Token);

        await Task.WhenAny(toUpstream, toClient);
        await abort.CancelAsync();
        await CloseQuietlyAsync(client, WebSocketCloseStatus.NormalClosure, "bye");
        await CloseQuietlyAsync(upstream, WebSocketCloseStatus.NormalClosure, "bye");

        try
        {
            await Task.WhenAll(toUpstream, toClient);
        }
        catch (Exception)
        {
            // 关连接时的取消不往外抛，已经收尾了。
        }

        Console.WriteLine("proxy_close account=" + account);
    }

    /// <summary>
    /// 单向转发。带令牌的那一方向（客户端 → 战斗服）负责判过期：
    /// 令牌一到点，这一帧不再转发，连接按策略违规关掉，客户端没法再发技能。
    /// </summary>
    static async Task PumpAsync(
        WebSocket from,
        WebSocket to,
        TokenStore? tokens,
        string? token,
        DateTimeOffset expiresAt,
        CancellationToken stopping)
    {
        var buffer = new byte[BufferBytes];
        while (!stopping.IsCancellationRequested)
        {
            ValueWebSocketReceiveResult received;
            try
            {
                received = await from.ReceiveAsync(buffer.AsMemory(), stopping);
            }
            catch (Exception)
            {
                return;
            }

            if (received.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            // 握手时有效不算数，发每一帧之前都要再确认一次。
            if (tokens != null && DateTimeOffset.UtcNow >= expiresAt)
            {
                Console.WriteLine("proxy_expired token_rejected=1");
                await CloseQuietlyAsync(from, WebSocketCloseStatus.PolicyViolation, "token_expired");
                return;
            }

            try
            {
                await to.SendAsync(
                    buffer.AsMemory(0, received.Count),
                    received.MessageType,
                    received.EndOfMessage,
                    stopping);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 把客户端的 query 拼到上游地址上，token 摘掉。
    /// 战斗服靠 class / party / dungeon 决定这一场怎么开，缺了它就只能一人一场。
    /// </summary>
    static string BuildUpstreamUrl(string baseUrl, IQueryCollection query)
    {
        var parts = new List<string>();
        foreach (var item in query)
        {
            if (string.Equals(item.Key, "token", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in item.Value)
            {
                parts.Add(Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(value ?? ""));
            }
        }

        if (parts.Count == 0)
        {
            return baseUrl;
        }

        return baseUrl + (baseUrl.Contains('?') ? "&" : "?") + string.Join("&", parts);
    }

    /// <summary>
    /// 令牌从 query 的 token 取，也认 Authorization: Bearer。
    /// 客户端走 query 是因为 Android 的 WebSocket 不好在握手时加头。
    /// </summary>
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

    static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(status, reason, timeout.Token);
        }
        catch (Exception)
        {
            // 对面已经断了就算了。
        }
    }
}
