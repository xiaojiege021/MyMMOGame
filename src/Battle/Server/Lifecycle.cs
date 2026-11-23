using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mmo.Proto.Battle;


namespace Battle;

// 14.8 D3a/D3b：BattleServer 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class BattleServer
{
    public async Task RunAsync(CancellationToken stopping)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // 让 stdout 只有我们自己的 kv 行，核对脚本才好 grep。
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/battle", HandleAsync);
        app.Urls.Add("http://" + config.Listen);

        await app.StartAsync(stopping);

        var feature = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        Console.WriteLine("listen=" + feature.Addresses.First());

        try
        {
            await Task.Delay(Timeout.Infinite, stopping);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    async Task TickLoopAsync(Party party)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(party.Ticker.Token))
            {
                var cleared = false;
                foreach (var message in party.Encounter.Tick())
                {
                    if (message.Ended is { Cleared: true })
                    {
                        cleared = true;
                    }
                    await BroadcastAsync(party, message, party.Ticker.Token);

                    // Q8：need_greed 的归属在 Ended 之后公布一次；personal 没有这条。
                    if (message.Ended is not null && party.Encounter.LootResult is { } loot)
                    {
                        await BroadcastAsync(
                            party,
                            new ServerMessage { LootAssigned = ToLootAssigned(loot) },
                            party.Ticker.Token);
                    }
                }

                // P3-5a：这一场打掉了，把事实值交回入口（只记名字，不写库）。
                // 先让回包出去，再报；同一场只报一次由 party.WorldBossFinished 挡。
                if (cleared && party.WorldBossWindow.Length > 0)
                {
                    _ = Task.Run(() => FinishWorldBoss(party, true));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // tick 挂了这场就停在半路，必须打出来，不能当没看见。
            Console.WriteLine($"tick_error=session={party.Encounter.SessionId} {ex.GetType().Name} {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 收一条连接上的消息，直到它断。返回值是 Q9 要的那一个位：true 表示客户端
    /// 主动发了 Close 帧（自己退的），false 表示网断或服务端取消——两者的宽限与
    /// 惩罚不一样，主动退立即算逃跑。
    /// </summary>
    async Task<bool> ReceiveLoopAsync(
        WebSocket socket, SemaphoreSlim send, Party party, Member member, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var assembled = new MemoryStream();

        while (!token.IsCancellationRequested)
        {
            assembled.SetLength(0);

            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, token);
                    return true;
                }

                if (received.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }

                assembled.Write(buffer, 0, received.Count);
                if (assembled.Length > MaxMessageBytes)
                {
                    // 不像正常请求，直接掐掉这条连接。
                    socket.Abort();
                    return false;
                }
            }
            while (!received.EndOfMessage);

            if (assembled.Length == 0)
            {
                continue;
            }

            ClientMessage inbound;
            try
            {
                inbound = ClientMessage.Parser.ParseFrom(assembled.ToArray());
            }
            catch (InvalidProtocolBufferException)
            {
                // 解析不了就不理，不当成一次施法。
                continue;
            }

            switch (inbound.BodyCase)
            {
                case ClientMessage.BodyOneofCase.Skill:
                    var rejected = party.Encounter.TrySubmit(member, inbound.Skill);
                    if (rejected is not null)
                    {
                        // 拒绝只回给这个人：别的前摇不用跟着停。
                        await SendAsync(socket, send, new ServerMessage { Rejected = rejected }, token);
                    }
                    break;

                case ClientMessage.BodyOneofCase.Cancel:
                    await SendAsync(
                        socket,
                        send,
                        new ServerMessage { Cancelled = party.Encounter.Cancel(member, inbound.Cancel) },
                        token);
                    break;

                case ClientMessage.BodyOneofCase.Move:
                    // 每次走位都回一条夹过的位置，客户端拿它对一下自己摆得对不对。
                    await SendAsync(
                        socket,
                        send,
                        new ServerMessage { Moved = party.Encounter.Move(member, inbound.Move) },
                        token);
                    break;

                case ClientMessage.BodyOneofCase.Talisman:
                    // 用符只影响自己：结果或拒绝都只回给这个人，不广播。
                    await SendAsync(
                        socket,
                        send,
                        party.Encounter.UseTalisman(member, inbound.Talisman),
                        token);
                    break;

                case ClientMessage.BodyOneofCase.Pill:
                    // B1：吃丹只影响自己：结果或拒绝都只回给这个人，不广播。
                    await SendAsync(
                        socket,
                        send,
                        party.Encounter.UsePill(member, inbound.Pill),
                        token);
                    break;

                case ClientMessage.BodyOneofCase.Scroll:
                    // 卷轴只拉起自己：结果或拒绝都只回给这个人，不广播。
                    await SendAsync(
                        socket,
                        send,
                        party.Encounter.UseReviveScroll(member, inbound.Scroll),
                        token);
                    break;

                case ClientMessage.BodyOneofCase.LootIntent:
                    // Q8：只记表态，不回包、不广播。掷点与归属都在结算时由服务端算，
                    // 客户端这条消息里没有任何数值能影响结果。
                    party.Encounter.SetLootIntent(
                        member.Id, inbound.LootIntent.ItemId, inbound.LootIntent.Decision);
                    break;
            }
        }

        return false;
    }
}
