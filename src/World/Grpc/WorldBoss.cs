using System.Text;
using Grpc.Core;
using Mmo.Proto.World;

namespace World;

public sealed partial class WorldGrpcService
{
    /// <summary>
    /// P3-5a：这一刻的地表入口。请求是空的——时刻取服务端时钟，场次钥匙、上限、
    /// 归属宗门名都由世界服算完再下发。入口关着就明说关着，不给一个「还能进」的场次。
    /// 这里不结算技能，也不碰任何角色数据。
    /// </summary>
    public override Task<DescribeWorldBossResponse> DescribeWorldBoss(
        DescribeWorldBossRequest request, ServerCallContext context)
    {
        var state = worldBoss.Describe(DateTimeOffset.UtcNow);
        var slots = worldBossSlots.Describe(DateTimeOffset.UtcNow);
        Console.WriteLine("world_boss=entrance=" + (state.EntranceId.Length == 0 ? "none" : state.EntranceId)
            + " visible=" + state.Visible
            + " reason=" + (state.Reason.Length == 0 ? "none" : state.Reason)
            + " window=" + (state.WindowKey.Length == 0 ? "none" : state.WindowKey)
            + " cap=" + state.Cap
            + " current=" + slots.Current
            + " remaining=" + slots.Remaining
            + " killed=" + state.Killed
            + " owner_sect=" + state.Owner.SectId
            + " owner=" + (state.Owner.SectName.Length == 0 ? "none" : state.Owner.SectName));

        return Task.FromResult(new DescribeWorldBossResponse
        {
            Visible = state.Visible,
            Reason = state.Reason,
            EntranceId = state.EntranceId,
            EntranceName = state.EntranceName,
            RegionId = state.RegionId,
            WindowKey = state.WindowKey,
            StartsAtMs = (ulong)Math.Max(0, state.StartsAtMs),
            EndsAtMs = (ulong)Math.Max(0, state.EndsAtMs),
            NextStartsAtMs = (ulong)Math.Max(0, state.NextStartsAtMs),
            Cap = (uint)Math.Max(0, state.Cap),
            Killed = state.Killed,
            OwnerSectId = state.Owner.SectId,
            OwnerSectName = state.Owner.SectName,
            OwnerWindowKey = state.Owner.WindowKey,
            OwnerRecordedAtMs = (ulong)Math.Max(0, state.Owner.RecordedAtMs),
            Current = (uint)Math.Max(0, slots.Current),
            Remaining = (uint)Math.Max(0, slots.Remaining),
            HoldS = (uint)Math.Max(0, slots.HoldSeconds),
            RenewS = (uint)Math.Max(0, slots.RenewSeconds),
            LostS = (uint)Math.Max(0, slots.LostSeconds),
        });
    }

    /// <summary>
    /// Q11：要一个名额。三层判定都在世界服：这一刻是不是这一场（表 + 日历）、
    /// 人在不在入口那条分线上（分线注册表）、这一场还有没有位置（占位表）。
    /// 请求里只有角色编号，场次与位置一个都带不进来。
    /// </summary>
    public override Task<HoldWorldBossSlotResponse> HoldWorldBossSlot(
        HoldWorldBossSlotRequest request, ServerCallContext context)
    {
        var outcome = worldBossSlots.Hold(request.CharacterId, DateTimeOffset.UtcNow);
        Console.WriteLine("world_boss_slot=op=hold"
            + " character=" + request.CharacterId
            + " ok=" + (outcome.Ok ? "true" : "false")
            + " reason=" + (outcome.Reason.Length == 0 ? "none" : outcome.Reason)
            + " duplicate=" + (outcome.Duplicate ? "true" : "false")
            + " window=" + (outcome.WindowKey.Length == 0 ? "none" : outcome.WindowKey)
            + " current=" + outcome.Current
            + " cap=" + outcome.Cap
            + " line=" + (outcome.LineKey.Length == 0 ? "none" : outcome.LineKey)
            + " line_occupancy=" + outcome.LineOccupancy
            + " line_capacity=" + outcome.LineCapacity);

        return Task.FromResult(new HoldWorldBossSlotResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            Duplicate = outcome.Duplicate,
            WindowKey = outcome.WindowKey,
            ExpiresAtMs = (ulong)Math.Max(0, outcome.ExpiresAtMs),
            Current = (uint)Math.Max(0, outcome.Current),
            Cap = (uint)Math.Max(0, outcome.Cap),
            LineKey = outcome.LineKey,
            LineOccupancy = (uint)Math.Max(0, outcome.LineOccupancy),
            LineCapacity = (uint)Math.Max(0, outcome.LineCapacity),
        });
    }

    /// <summary>Q11：续约。进场后的第一次确认也走这条（entered=true）。</summary>
    public override Task<RenewWorldBossSlotResponse> RenewWorldBossSlot(
        RenewWorldBossSlotRequest request, ServerCallContext context)
    {
        var outcome = worldBossSlots.Renew(request.CharacterId, request.Entered, DateTimeOffset.UtcNow);
        Console.WriteLine("world_boss_slot=op=renew"
            + " character=" + request.CharacterId
            + " entered=" + (request.Entered ? "true" : "false")
            + " ok=" + (outcome.Ok ? "true" : "false")
            + " reason=" + (outcome.Reason.Length == 0 ? "none" : outcome.Reason)
            + " window=" + (outcome.WindowKey.Length == 0 ? "none" : outcome.WindowKey)
            + " current=" + outcome.Current
            + " cap=" + outcome.Cap);

        return Task.FromResult(new RenewWorldBossSlotResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            WindowKey = outcome.WindowKey,
            ExpiresAtMs = (ulong)Math.Max(0, outcome.ExpiresAtMs),
            Current = (uint)Math.Max(0, outcome.Current),
            Cap = (uint)Math.Max(0, outcome.Cap),
        });
    }

    /// <summary>Q11：还名额。reason 只作日志（leave / closed / timeout / clear）。</summary>
    public override Task<ReleaseWorldBossSlotResponse> ReleaseWorldBossSlot(
        ReleaseWorldBossSlotRequest request, ServerCallContext context)
    {
        var reason = (request.Reason ?? "").Trim();
        var outcome = worldBossSlots.Release(request.CharacterId, reason, DateTimeOffset.UtcNow);
        Console.WriteLine("world_boss_slot=op=release"
            + " character=" + request.CharacterId
            + " ok=" + (outcome.Ok ? "true" : "false")
            + " reason=" + (outcome.Reason.Length == 0 ? "none" : outcome.Reason)
            + " window=" + (outcome.WindowKey.Length == 0 ? "none" : outcome.WindowKey)
            + " current=" + outcome.Current);

        return Task.FromResult(new ReleaseWorldBossSlotResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            Current = (uint)Math.Max(0, outcome.Current),
        });
    }

    /// <summary>
    /// P3-5a：战斗服交回这一场的事实值（打掉没打掉、伤害最高的宗门）。
    /// 世界服只关入口、记名字：没有奖励、没有属性加成、没有税率、没有矿脉归属，
    /// 也不写任何库表。同一个场次只记一次，重复回报回 duplicate 且不覆盖。
    /// </summary>
    public override Task<RecordWorldBossOwnerResponse> RecordWorldBossOwner(
        RecordWorldBossOwnerRequest request, ServerCallContext context)
    {
        var outcome = worldBoss.Record(new RecordFact(
            (request.TxnId ?? "").Trim(),
            (request.EntranceId ?? "").Trim(),
            (request.WindowKey ?? "").Trim(),
            request.Killed,
            request.WinnerSectId,
            (request.WinnerSectName ?? "").Trim(),
            request.DamageTotal));

        Console.WriteLine("world_boss_record=window=" + outcome.WindowKey
            + " entrance=" + (request.EntranceId ?? "").Trim()
            + " ok=" + outcome.Ok
            + " duplicate=" + outcome.Duplicate
            + " reason=" + (outcome.Reason.Length == 0 ? "none" : outcome.Reason)
            + " killed=" + request.Killed
            + " winner_sect=" + request.WinnerSectId
            + " owner=" + (outcome.Owner.SectName.Length == 0 ? "none" : outcome.Owner.SectName)
            + " entrance_closed=" + outcome.EntranceClosed);

        return Task.FromResult(new RecordWorldBossOwnerResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            Duplicate = outcome.Duplicate,
            WindowKey = outcome.WindowKey,
            OwnerSectId = outcome.Owner.SectId,
            OwnerSectName = outcome.Owner.SectName,
            EntranceClosed = outcome.EntranceClosed,
        });
    }
}
