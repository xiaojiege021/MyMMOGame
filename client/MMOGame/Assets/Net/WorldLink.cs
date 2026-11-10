using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mmo.Proto.World;

namespace Mmo.Net
{
    /// <summary>
    /// 到网关 /world/stream 的一条 WebSocket：表世界位置上报与周围人的快照。
    /// 只做收发和拆信封：坐标校验、广播频率、谁在分线里都由世界服定，这边不重复算。
    /// 收到的帧先落在队列里，调用方在主线程取，socket 线程不碰 Unity 对象。
    /// </summary>
    public sealed class WorldLink : IDisposable
    {
        public const int MaxMessageBytes = 64 * 1024;

        readonly ClientWebSocket socket = new ClientWebSocket();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        readonly ConcurrentQueue<WorldServerFrame> inbox = new ConcurrentQueue<WorldServerFrame>();

        CancellationTokenSource loop;
        Task receive;
        long seq;
        string failure = string.Empty;

        WorldLink(string url)
        {
            Url = url;
        }

        public string Url { get; }

        public bool IsOpen => socket.State == WebSocketState.Open;

        public string Failure => failure;

        public static async Task<WorldLink> ConnectAsync(string url, TimeSpan timeout)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != "ws" && uri.Scheme != "wss"))
            {
                throw new ArgumentException("地址不是 ws:// 或 wss://：" + url);
            }

            var link = new WorldLink(url);
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

        /// <summary>上报自己的坐标与朝向；序号只用来对号，客户端不带身份和分线号。</summary>
        public Task SendPositionAsync(float x, float y, float z, float facing)
        {
            var next = (ulong)Interlocked.Increment(ref seq);
            return SendAsync(new WorldClientFrame
            {
                Position = new PositionReport
                {
                    X = x, Y = y, Z = z, Facing = facing, Seq = next,
                },
            });
        }

        public bool TryReceive(out WorldServerFrame frame)
        {
            return inbox.TryDequeue(out frame);
        }

        public void Dispose()
        {
            if (loop != null)
            {
                loop.Cancel();
                loop.Dispose();
            }

            socket.Dispose();
            sendLock.Dispose();
        }

        async Task SendAsync(WorldClientFrame message)
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

                        inbox.Enqueue(WorldServerFrame.Parser.ParseFrom(assembled.ToArray()));
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
