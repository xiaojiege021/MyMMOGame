using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mmo.Proto.Battle;

namespace Mmo.Net
{
    /// <summary>
    /// 一条到战斗进程的 WebSocket，按 P0-1c 先走本机直连。
    /// 只做收发和拆信封：判定、伤害、冷却都在战斗进程里，这边不重复算一遍。
    /// 收到的消息先落在队列里，由调用方在主线程取，socket 线程不碰 Unity 对象。
    /// </summary>
    public sealed class BattleLink : IDisposable
    {
        /// <summary>和服务端同一个上限。超过就断开，不把内存交给对面决定。</summary>
        public const int MaxMessageBytes = 64 * 1024;

        readonly ClientWebSocket socket = new ClientWebSocket();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        readonly ConcurrentQueue<ServerMessage> inbox = new ConcurrentQueue<ServerMessage>();

        CancellationTokenSource loop;
        Task receive;
        string failure = string.Empty;

        BattleLink(string url)
        {
            Url = url;
        }

        public string Url { get; }

        public bool IsOpen => socket.State == WebSocketState.Open;

        /// <summary>收包循环里出过的错。空表示没出错。</summary>
        public string Failure => failure;

        /// <summary>
        /// 连上就等开场包。超时不挂住，直接抛出来，由调用方报给玩家。
        /// </summary>
        public static async Task<BattleLink> ConnectAsync(string url, TimeSpan timeout)
        {
            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException("战斗服地址是空的");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != "ws" && uri.Scheme != "wss"))
            {
                throw new ArgumentException("地址不是 ws:// 或 wss://：" + url);
            }

            var link = new BattleLink(url);
            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    await link.socket.ConnectAsync(uri, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    link.Dispose();
                    throw new TimeoutException("连不上 " + url + "，等了 " + (int)timeout.TotalMilliseconds + " 毫秒");
                }
            }

            link.loop = new CancellationTokenSource();
            link.receive = Task.Run(() => link.ReceiveLoopAsync(link.loop.Token));
            return link;
        }

        /// <summary>把一个技能请求发出去。施放时刻用客户端本地时钟，服务端只用来对号。</summary>
        public Task SendSkillAsync(uint skillId, ulong targetId, long castTimeMs)
        {
            return SendAsync(new ClientMessage
            {
                Skill = new SkillRequest
                {
                    SkillId = skillId,
                    TargetId = targetId,
                    CastTimeMs = castTimeMs,
                },
            });
        }

        /// <summary>前摇里撤掉这次施法。已经结算完的话服务端会回 accepted=false。</summary>
        public Task SendCancelAsync(uint skillId, long castTimeMs)
        {
            return SendAsync(new ClientMessage
            {
                Cancel = new SkillCancelRequest
                {
                    SkillId = skillId,
                    CastTimeMs = castTimeMs,
                },
            });
        }

        /// <summary>主线程取一条已经收到的消息。没有就返回 false，不阻塞。</summary>
        public bool TryReceive(out ServerMessage message)
        {
            return inbox.TryDequeue(out message);
        }

        /// <summary>等一条消息。核对和过场用得上，正式界面还是走 TryReceive。</summary>
        public async Task<ServerMessage> ReceiveAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (inbox.TryDequeue(out var message))
                {
                    return message;
                }

                if (failure.Length > 0)
                {
                    throw new InvalidOperationException("收包断了：" + failure);
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            throw new TimeoutException("等战斗服回消息超时，等了 " + (int)timeout.TotalMilliseconds + " 毫秒");
        }

        /// <summary>正常收尾。先挥手再停收包，服务端好把这场标成关闭。</summary>
        public async Task CloseAsync()
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // 对面先断了也算关掉，不往上抛。
                }
            }

            if (loop != null)
            {
                loop.Cancel();
            }

            if (receive != null)
            {
                try
                {
                    await receive.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        public void Dispose()
        {
            if (loop != null)
            {
                loop.Cancel();
            }

            socket.Dispose();
            sendLock.Dispose();

            if (loop != null)
            {
                loop.Dispose();
            }
        }

        async Task SendAsync(ClientMessage message)
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException("连接已经关了，发不出去");
            }

            var bytes = message.ToByteArray();
            await sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await socket.SendAsync(
                        new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[8 * 1024];
            using (var assembled = new MemoryStream())
            {
                try
                {
                    while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                    {
                        assembled.SetLength(0);
                        WebSocketReceiveResult frame;
                        do
                        {
                            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token)
                                .ConfigureAwait(false);
                            if (frame.MessageType == WebSocketMessageType.Close)
                            {
                                return;
                            }

                            assembled.Write(buffer, 0, frame.Count);
                            if (assembled.Length > MaxMessageBytes)
                            {
                                failure = "包太大=" + assembled.Length;
                                return;
                            }
                        }
                        while (!frame.EndOfMessage);

                        if (frame.MessageType != WebSocketMessageType.Binary)
                        {
                            continue;
                        }

                        inbox.Enqueue(ServerMessage.Parser.ParseFrom(assembled.ToArray()));
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception e)
                {
                    failure = e.GetType().Name + " " + e.Message;
                }
            }
        }
    }
}
