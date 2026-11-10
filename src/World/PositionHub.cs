using Mmo.Proto.World;

namespace World;

/// <summary>
/// 位置流的订阅表与快照组装。广播节拍只由 PositionBroadcaster 驱动，
/// 客户端上报多快都不改推送频率；同一条分线里的人才互相出现在快照里。
/// 位置只活在这张表和 LineRegistry 的进程内存里，不落库。
/// </summary>
public sealed class PositionHub(LineRegistry registry, WorldConfig config, AppearanceTierTable tiers)
{
    readonly object gate = new();
    readonly Dictionary<long, PositionSubscription> subscriptions = new();
    long nextId;

    /// <summary>没进分线的人不给开流：位置广播从属于 P1-3a1 的分线。</summary>
    public PositionSubscription? Open(string account, ulong characterId)
    {
        var lineId = registry.LineOf(account, characterId);
        if (lineId is null)
        {
            return null;
        }

        var subscription = new PositionSubscription(account, characterId, lineId);
        lock (gate)
        {
            subscription.Id = ++nextId;
            subscriptions.Add(subscription.Id, subscription);
        }

        return subscription;
    }

    public void Close(PositionSubscription subscription)
    {
        lock (gate)
        {
            subscriptions.Remove(subscription.Id);
        }
    }

    /// <summary>上报不通过时只给本人回一次拉回，不改动别人看到的位置。</summary>
    public PositionReportResult Report(
        PositionSubscription subscription, PositionReport report, DateTimeOffset now)
    {
        var result = registry.Report(
            subscription.Account, subscription.CharacterId,
            report.X, report.Y, report.Z, report.Facing, report.Seq, now);

        if (!result.Accepted)
        {
            var frame = new WorldServerFrame
            {
                Rejected = new PositionRejected { Reason = result.Reason },
            };
            if (result.Corrected is { } corrected)
            {
                frame.Rejected.Corrected = ToProto(corrected, AppearanceTier.Unspecified);
            }

            subscription.Frames.Writer.TryWrite(frame);
        }

        return result;
    }

    /// <summary>按配置频率跑一次：给每条流组装它所在分线的其他成员快照。</summary>
    public void Tick(DateTimeOffset now)
    {
        PositionSubscription[] current;
        lock (gate)
        {
            current = subscriptions.Values.ToArray();
        }

        var lines = registry.MemberSnapshot();
        var timeMs = (ulong)now.ToUnixTimeMilliseconds();

        foreach (var subscription in current)
        {
            var lineId = registry.LineOf(subscription.Account, subscription.CharacterId);
            if (lineId is null)
            {
                // 离开分线后旧流不再推人，也不留着占订阅表。
                subscription.Frames.Writer.TryWrite(new WorldServerFrame
                {
                    Rejected = new PositionRejected { Reason = "left_line" },
                });
                subscription.Frames.Writer.TryComplete();
                Close(subscription);
                continue;
            }

            subscription.LineId = lineId;
            var line = lines.FirstOrDefault(l => l.LineId == lineId);
            var snapshot = new PositionSnapshot
            {
                LineId = lineId,
                RegionId = line?.RegionId ?? "",
                ServerTimeMs = timeMs,
                SyncHz = (uint)config.SyncHz,
                SelfCharacterId = subscription.CharacterId,
            };

            if (line is not null)
            {
                // 档位按「看的人」到「被看的人」的距离算，同一帧里每个人可能不一样。
                // 名单仍是同一分线的全量，不按距离删人（AOI 不在这张卡）。
                var self = line.Members.FirstOrDefault(
                    m => m.CharacterId == subscription.CharacterId);
                foreach (var avatar in line.Members)
                {
                    if (avatar.CharacterId == subscription.CharacterId)
                    {
                        continue;
                    }

                    var tier = self is null
                        ? AppearanceTier.Full
                        : tiers.For(self, avatar);
                    snapshot.Avatars.Add(ToProto(avatar, tier));
                }
            }

            subscription.Frames.Writer.TryWrite(new WorldServerFrame { Snapshot = snapshot });
        }
    }

    public static AvatarPosition ToProto(AvatarState state, AppearanceTier tier) => new()
    {
        CharacterId = state.CharacterId,
        X = state.X,
        Y = state.Y,
        Z = state.Z,
        Facing = state.Facing,
        Seq = state.Seq,
        AppearanceTier = tier,
        DisplayName = state.DisplayName,
    };
}
