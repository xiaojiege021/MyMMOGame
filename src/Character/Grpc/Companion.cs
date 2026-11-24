using Grpc.Core;
using Mmo.Proto.Character;

namespace Character;

// B4：坐骑与灵兽。两条写（骑上 / 下坐骑）与两条只读（当前坐骑 / 灵兽收集）。
// 方案 9.4：坐骑只差移动速度、不进战斗；灵兽只做收集 / 外观 / 生活协助、
// 不进入伤害公式。所以这里没有任何战斗数值，也不改等级 / 境界 / 背包数量。
public sealed partial class CharacterGrpcService
{
    /// <summary>
    /// 骑上某一格背包里的坐骑。请求只带格号：档位与 speed_bps 在 config/mount.json，
    /// 客户端带不进任何数值。业务拒绝一律 ok=true + reason，参数错走 InvalidArgument。
    /// </summary>
    public override async Task<MountResponse> MountUp(
        MountUpRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0 || request.BagSlot == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var outcome = await mounts.MountAsync(
            (long)request.CharacterId, account, (int)request.BagSlot,
            items, mountTable, context.CancellationToken);
        Console.WriteLine("mount_up account=" + account
            + " character=" + request.CharacterId
            + " bag_slot=" + request.BagSlot
            + " item=" + (outcome.ItemId.Length == 0 ? "none" : outcome.ItemId)
            + " tier=" + (outcome.Tier.Length == 0 ? "none" : outcome.Tier)
            + " speed_bps=" + outcome.SpeedBps
            + " ok=" + (outcome.Ok ? "true" : "false")
            + " reason=" + (outcome.Reason.Length == 0 ? "none" : outcome.Reason));

        return new MountResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            ItemId = outcome.ItemId,
            Tier = outcome.Tier,
            SpeedBps = outcome.SpeedBps,
            BagSlot = (uint)outcome.BagSlot,
        };
    }

    /// <summary>下坐骑。背包行一直没动过，所以这里只删一条记录、回原来那一格。</summary>
    public override async Task<MountResponse> Dismount(
        DismountRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "bad_request"));
        }

        var outcome = await mounts.DismountAsync(
            (long)request.CharacterId, account, mountTable, context.CancellationToken);
        Console.WriteLine("mount_down account=" + account
            + " character=" + request.CharacterId
            + " item=" + (outcome.ItemId.Length == 0 ? "none" : outcome.ItemId)
            + " bag_slot=" + outcome.BagSlot
            + " ok=" + (outcome.Ok ? "true" : "false")
            + " reason=" + (outcome.Reason.Length == 0 ? "none" : outcome.Reason));

        return new MountResponse
        {
            Ok = outcome.Ok,
            Reason = outcome.Reason,
            ItemId = outcome.ItemId,
            Tier = outcome.Tier,
            SpeedBps = outcome.SpeedBps,
            BagSlot = (uint)outcome.BagSlot,
        };
    }

    /// <summary>当前骑的是哪一只、背包里一共有哪几只。只读，不改背包。</summary>
    public override async Task<DescribeMountResponse> DescribeMount(
        DescribeMountRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var snap = await mounts.DescribeAsync(
            (long)request.CharacterId, account, items, mountTable, context.CancellationToken);
        if (!snap.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var response = new DescribeMountResponse
        {
            Found = true,
            EquippedItemId = snap.Equipped?.ItemId ?? "",
            EquippedSpeedBps = snap.Equipped?.SpeedBps ?? 0,
        };
        foreach (var row in snap.Owned)
        {
            response.Owned.Add(new MountFact
            {
                ItemId = row.ItemId,
                Name = row.Name,
                Tier = row.Tier,
                SpeedBps = row.SpeedBps,
                BagSlot = (uint)row.BagSlot,
                Mounted = snap.Equipped is not null
                    && string.Equals(snap.Equipped.ItemId, row.ItemId, StringComparison.Ordinal),
            });
        }

        return response;
    }

    /// <summary>灵兽收集度与生活协助合计。收集 = 背包现在持有，不记永久解锁。</summary>
    public override async Task<DescribeSpiritBeastResponse> DescribeSpiritBeast(
        DescribeSpiritBeastRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var snap = await spiritBeasts.DescribeAsync(
            (long)request.CharacterId, account, context.CancellationToken);
        if (!snap.Found)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var response = new DescribeSpiritBeastResponse
        {
            Found = true,
            CollectedCount = snap.CollectedCount,
            EntryCount = snap.EntryCount,
            CollectionPctBps = snap.CollectionPctBps,
            TotalLifeAssistPctBps = snap.TotalLifeAssistPctBps,
            PassiveKind = snap.PassiveKind,
        };
        foreach (var row in snap.Entries)
        {
            response.Entries.Add(new SpiritBeastFact
            {
                ItemId = row.ItemId,
                Name = row.Name,
                Tier = row.Tier,
                Collected = row.Collected,
                LifeAssistPctBps = row.LifeAssistPctBps,
            });
        }

        return response;
    }
}
