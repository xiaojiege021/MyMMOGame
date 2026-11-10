using System.Threading.Channels;
using Mmo.Proto.World;

namespace World;

/// <summary>一条到网关的位置流订阅。帧先落到有界队列，写回包只有一个任务。</summary>
public sealed class PositionSubscription(string account, ulong characterId, string lineId)
{
    public long Id { get; set; }
    public string Account { get; } = account;
    public ulong CharacterId { get; } = characterId;

    // 换分线时由广播节拍更新，客户端带不进这个值。
    public string LineId { get; set; } = lineId;

    public Channel<WorldServerFrame> Frames { get; } = Channel.CreateBounded<WorldServerFrame>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
}
