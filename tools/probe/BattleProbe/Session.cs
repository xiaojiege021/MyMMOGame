using System.Net.WebSockets;
using System.Threading.Channels;
using Google.Protobuf;
using Mmo.Proto.Battle;

namespace Mmo.Tools;

/// <summary>
/// 核对用的一条连接。一条连接就是一场，和手机上那条一样走 protobuf 二进制帧。
/// 收到的东西全部塞进队列，场景代码按超时去取，取不到就是「这段时间没有回包」。
/// </summary>
internal sealed class Session : IAsyncDisposable
{
    readonly ClientWebSocket _socket = new();
    readonly Channel<ServerMessage> _inbox = Channel.CreateUnbounded<ServerMessage>();
    readonly CancellationTokenSource _cts = new();

    public BattleOpened Opened { get; private set; } = null!;

    /// <summary>
    /// skipUntilOpened 只给 P3-3a2 的城战多人用例用：同一场里别人的状态广播、
    /// 档位刷新或 tick 回包可能比自己的开场包先到，连上后一直读到 BattleOpened。
    /// 缺省 false，老用例「第一条必须是开场包」的断言一字不变。
    /// </summary>
    public static async Task<Session> ConnectAsync(string url, bool skipUntilOpened = false)
    {
        var session = new Session();
        await session._socket.ConnectAsync(new Uri(url), session._cts.Token);
        _ = session.PumpAsync();

        var first = skipUntilOpened
            ? await session.ReadUntilAsync(m => m.Opened is not null, 5000)
            : await session.ReadAsync(5000);
        if (first is null)
        {
            throw new InvalidOperationException("连上以后没收到开场消息");
        }
        session.Opened = first.Opened
                         ?? throw new InvalidOperationException("第一条不是 BattleOpened");
        return session;
    }

    public async Task<ServerMessage?> ReadAsync(int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            return await _inbox.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 读一条必须是结算结果的回包，读到拒绝或者超时都算不符合。
    /// P3-3a2：城战开场会先推同屏档位包，之后才轮到这一下的结果；
    /// 这里跳过无关的广播与档位包，不把「第一条不是结果」当成没结算。
    /// 只放宽读包顺序，不改任何用例的断言，也不碰产品代码。
    /// </summary>
    public async Task<SkillResult?> ReadResultAsync(int timeoutMs)
    {
        var message = await ReadUntilAsync(
            m => m.Result is not null || m.Rejected is not null, timeoutMs);
        return message?.Result;
    }

    /// <summary>与 ReadResultAsync 对称：城战档位包先到时不把拒绝判定吃掉。</summary>
    public async Task<SkillRejected?> ReadRejectedAsync(int timeoutMs)
    {
        var message = await ReadUntilAsync(
            m => m.Result is not null || m.Rejected is not null, timeoutMs);
        return message?.Rejected;
    }

    /// <summary>
    /// 一直读到符合的那条为止，中间路过的广播（别人的结算、队友状态）直接丢掉。
    /// 组队场景里各连接都会收到全场广播，靠这个挑自己要的那条。
    /// </summary>
    public async Task<ServerMessage?> ReadUntilAsync(Func<ServerMessage, bool> match, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var left = (int)Math.Max(1, deadline - Environment.TickCount64);
            if (left <= 0)
            {
                return null;
            }

            var message = await ReadAsync(left);
            if (message is null)
            {
                return null;
            }
            if (match(message))
            {
                return message;
            }
        }
    }

    public Task SubmitAsync(uint skillId, ulong targetId, long castTimeMs) =>
        SendAsync(new ClientMessage
        {
            Skill = new SkillRequest
            {
                SkillId = skillId,
                TargetId = targetId,
                CastTimeMs = castTimeMs,
            },
        });

    public Task CancelAsync(uint skillId, long castTimeMs) =>
        SendAsync(new ClientMessage
        {
            Cancel = new SkillCancelRequest { SkillId = skillId, CastTimeMs = castTimeMs },
        });

    /// <summary>
    /// 用一张符。请求里没有目标编号——红符只回复自己，客户端连「想治谁」都表达不了。
    /// </summary>
    public Task UseTalismanAsync(string itemId) =>
        SendAsync(new ClientMessage
        {
            Talisman = new UseTalismanRequest { ItemId = itemId, ClientTimeMs = NowMs() },
        });

    /// <summary>
    /// 用一张复活卷轴。请求里也没有目标编号——方案 12.1 写定只拉起自己，
    /// 客户端连「想拉谁」都表达不了。
    /// </summary>
    public Task UseReviveScrollAsync(string itemId) =>
        SendAsync(new ClientMessage
        {
            Scroll = new UseReviveScrollRequest { ItemId = itemId, ClientTimeMs = NowMs() },
        });

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    async Task SendAsync(ClientMessage message)
    {
        var bytes = message.ToByteArray();
        await _socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            _cts.Token);
    }

    async Task PumpAsync()
    {
        var buffer = new byte[4096];
        using var assembled = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                assembled.SetLength(0);

                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        _inbox.Writer.TryComplete();
                        return;
                    }
                    assembled.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);

                if (assembled.Length == 0)
                {
                    continue;
                }

                await _inbox.Writer.WriteAsync(ServerMessage.Parser.ParseFrom(assembled.ToArray()), _cts.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            _inbox.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(500);
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeTimeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }

        _socket.Dispose();
        _cts.Dispose();
        _inbox.Writer.TryComplete();
    }
}
