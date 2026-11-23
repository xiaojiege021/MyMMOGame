namespace Social;

// 14.8 D3h：C3 洞府的三条核对入口（--homestead-state / --homestead-place /
// --homestead-remove）原样搬出 Program.cs；homes / homestead 由连库口装配后传入，
// 判定、打印与「表缺失降级」一行未改。
public sealed partial class SocialCli
{
    async Task RunHomesteadAsync(HomesteadRuntime homes, HomesteadTable? homestead)
    {

        // ---- C3 洞府摆放：独立读回与固定事务号重放（只给核对用） ----
        if (homesteadStateOf != 0)
        {
            if (homes.Repository is null)
            {
                Console.WriteLine("homestead_state=missing table");
            }
            else
            {
                var rows = await homes.Repository.DescribeAsync(
                    homesteadStateOf, CancellationToken.None);
                Console.WriteLine("homestead_state=ok character=" + homesteadStateOf
                    + " rows=" + rows.Count
                    + " slots=" + (homestead?.Slots.Count ?? 0)
                    + " placements=" + string.Join(",",
                        rows.Select(r => r.SlotId + ":" + r.ItemId)));
            }
        }

        if (homesteadPlace is not null)
        {
            if (homes.Repository is null)
            {
                Console.WriteLine("homestead_place=unavailable");
            }
            else
            {
                ulong.TryParse(homesteadPlace[1], out var cid);
                var placed = await homes.Repository.PlaceAsync(
                    homesteadPlace[0], cid, homesteadPlace[4],
                    homesteadPlace[2], homesteadPlace[3], CancellationToken.None);
                Console.WriteLine("homestead_place=state character=" + placed.CharacterId
                    + " slot=" + placed.SlotId + " item=" + placed.ItemId
                    + " ok=" + (placed.Ok ? "true" : "false")
                    + " reason=" + placed.Reason
                    + " duplicate=" + (placed.Duplicate ? "true" : "false")
                    + " placements=" + string.Join(",",
                        placed.Placements.Select(p => p.SlotId + ":" + p.ItemId)));
            }
        }

        if (homesteadRemove is not null)
        {
            if (homes.Repository is null)
            {
                Console.WriteLine("homestead_remove=unavailable");
            }
            else
            {
                ulong.TryParse(homesteadRemove[0], out var cid);
                var removed = await homes.Repository.RemoveAsync(
                    cid, homesteadRemove[2], homesteadRemove[1], CancellationToken.None);
                Console.WriteLine("homestead_remove=state character=" + removed.CharacterId
                    + " slot=" + removed.SlotId
                    + " ok=" + (removed.Ok ? "true" : "false")
                    + " reason=" + removed.Reason
                    + " duplicate=" + (removed.Duplicate ? "true" : "false")
                    + " placements=" + string.Join(",",
                        removed.Placements.Select(p => p.SlotId + ":" + p.ItemId)));
            }
        }
    }
}

