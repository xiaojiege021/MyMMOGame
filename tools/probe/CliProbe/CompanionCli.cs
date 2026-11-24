using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// B4：坐骑与灵兽的核对入口。
///   · 只读不连库：--companion-config（两张表的现值）、--mount-speed &lt;物品编号&gt;；
///     坏配置（--mount-table-path / --beast-table-path 指到临时表）也走这一条。
///   · 连库：--mount / --dismount / --mount-row / --beast-row。
/// 走的是起服同一套 Load 与同一个仓储，探针不另算一套速度或生活协助。
/// </summary>
internal static class CompanionCli
{
    public static bool Handles(string flag) => flag is
        "--companion-config" or "--mount-speed" or "--mount" or "--dismount"
        or "--mount-row" or "--beast-row" or "--mount-table-path" or "--beast-table-path";

    public static int Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--companion-config":
                companionConfig = true;
                return 0;
            case "--mount-speed" when i + 1 < args.Length:
                mountSpeedItem = args[++i];
                return 0;
            case "--mount" when i + 2 < args.Length:
                long.TryParse(args[++i], out mountOf);
                int.TryParse(args[++i], out mountBagSlot);
                return 0;
            case "--dismount" when i + 1 < args.Length:
                long.TryParse(args[++i], out dismountOf);
                return 0;
            case "--mount-row" when i + 1 < args.Length:
                long.TryParse(args[++i], out mountRowOf);
                return 0;
            case "--beast-row" when i + 1 < args.Length:
                long.TryParse(args[++i], out beastRowOf);
                return 0;
            case "--mount-table-path" when i + 1 < args.Length:
                mountTablePath = args[++i];
                return 0;
            case "--beast-table-path" when i + 1 < args.Length:
                beastTablePath = args[++i];
                return 0;
            default:
                return 64;
        }
    }

    public static int? TryReadOnly()
    {
        if (!companionConfig && mountSpeedItem is null
            && mountTablePath is null && beastTablePath is null)
        {
            return null;
        }

        try
        {
            var cfg = CharacterConfig.Load(configPath);
            var (items, mounts, beasts) = LoadAll(cfg);

            if (mountTablePath is not null || beastTablePath is not null || companionConfig)
            {
                DumpConfig(mounts, beasts);
            }

            if (mountSpeedItem is not null)
            {
                var id = mountSpeedItem.Trim();
                var mount = mounts.Find(id)
                    ?? throw new ConfigException("坐骑表里没有这只：" + id);
                var goods = items.Find(id);
                Console.WriteLine("mount_speed=item=" + id
                    + " kind=" + (goods?.Kind ?? "missing")
                    + " tier=" + mount.Tier
                    + " speed_bps=" + mount.SpeedBps
                    + " name=" + mount.Name);
            }
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }
        catch (IOException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        return 0;
    }

    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        if (mountOf == 0 && dismountOf == 0 && mountRowOf == 0 && beastRowOf == 0)
        {
            return null;
        }

        var cfg = CharacterConfig.Load(configPath);
        var (items, mounts, beasts) = LoadAll(cfg);
        var mountRepo = new MountRepository(ctx.DataSource);

        if (mountOf != 0)
        {
            var outcome = await mountRepo.MountAsync(
                mountOf, null, mountBagSlot, items, mounts, CancellationToken.None);
            Console.WriteLine("mount=" + (outcome.Reason.Length == 0 ? "ok" : "reject")
                + " reason=" + Stamp(outcome.Reason)
                + " item=" + Stamp(outcome.ItemId)
                + " tier=" + Stamp(outcome.Tier)
                + " speed_bps=" + outcome.SpeedBps
                + " bag_slot=" + outcome.BagSlot);
        }

        if (dismountOf != 0)
        {
            var outcome = await mountRepo.DismountAsync(
                dismountOf, null, mounts, CancellationToken.None);
            Console.WriteLine("dismount=" + (outcome.Reason.Length == 0 ? "ok" : "reject")
                + " reason=" + Stamp(outcome.Reason)
                + " item=" + Stamp(outcome.ItemId)
                + " speed_bps=" + outcome.SpeedBps
                + " bag_slot=" + outcome.BagSlot);
        }

        if (mountRowOf != 0)
        {
            var snap = await mountRepo.DescribeAsync(
                mountRowOf, null, items, mounts, CancellationToken.None);
            Console.WriteLine("mount_row=" + (snap.Found ? "ok" : "missing")
                + " equipped=" + (snap.Equipped?.ItemId ?? "none")
                + " equipped_tier=" + Stamp(snap.Equipped?.Tier ?? "")
                + " equipped_speed_bps=" + (snap.Equipped?.SpeedBps ?? 0)
                + " owned=" + (snap.Owned.Count == 0
                    ? "none"
                    : string.Join(",", snap.Owned.Select(row =>
                        row.ItemId + ":" + row.Tier + ":" + row.SpeedBps + ":slot" + row.BagSlot))));
        }

        if (beastRowOf != 0)
        {
            var beastsRepo = new SpiritBeastRepository(ctx.DataSource, items, beasts);
            var snap = await beastsRepo.DescribeAsync(beastRowOf, null, CancellationToken.None);
            Console.WriteLine("beast_row=" + (snap.Found ? "ok" : "missing")
                + " collected=" + snap.CollectedCount
                + " entries=" + snap.EntryCount
                + " pct_bps=" + snap.CollectionPctBps
                + " life_assist_total_bps=" + snap.TotalLifeAssistPctBps
                + " kind=" + Stamp(snap.PassiveKind)
                + " held=" + (snap.Entries.Count(e => e.Collected) == 0
                    ? "none"
                    : string.Join(",", snap.Entries.Where(e => e.Collected).Select(e => e.ItemId))));
        }

        return 0;
    }

    static void DumpConfig(MountTable mounts, SpiritBeastTable beasts)
    {
        Console.WriteLine("companion_config=ok"
            + " mount_version=" + mounts.Version
            + " mount_pending=" + (mounts.Pending ? "true" : "false")
            + " mount_tiers=" + mounts.Tiers.Count
            + " mount_entries=" + mounts.Entries.Count);
        foreach (var tier in mounts.Tiers)
        {
            Console.WriteLine("mount_tier=" + tier.Id
                + " name=" + tier.Name
                + " min_speed_bps=" + tier.MinSpeedBps
                + " max_speed_bps=" + tier.MaxSpeedBps);
        }

        foreach (var entry in mounts.Entries)
        {
            Console.WriteLine("mount=" + entry.ItemId
                + " tier=" + entry.Tier
                + " speed_bps=" + entry.SpeedBps
                + " name=" + entry.Name);
        }

        Console.WriteLine("beast_config=ok"
            + " version=" + beasts.Version
            + " pending=" + (beasts.Pending ? "true" : "false")
            + " kind=" + beasts.PassiveKind
            + " tiers=" + beasts.Tiers.Count
            + " entries=" + beasts.Entries.Count
            + " life_assist_total_bps=" + beasts.TotalLifeAssistPctBps
            + " codex_total_bps=" + beasts.HeroCodexPassiveBps
            + " cap_bps=" + beasts.MaxTotalLifeAssistPctBps);
        foreach (var entry in beasts.Entries)
        {
            Console.WriteLine("beast=" + entry.ItemId
                + " tier=" + entry.Tier
                + " life_assist_pct_bps=" + entry.LifeAssistPctBps
                + " name=" + entry.Name);
        }
    }

    /// <summary>与起服同一条链：物品表 → 坐骑表 → 主线 → 图鉴 → 灵兽表（灵兽要图鉴的合计对上限）。</summary>
    static (ItemTable Items, MountTable Mounts, SpiritBeastTable Beasts) LoadAll(CharacterConfig cfg)
    {
        var items = ItemTable.Load("config/items.json");
        var mounts = MountTable.Load(mountTablePath ?? cfg.MountTable, items);
        var mainline = File.Exists(cfg.MainlineTable)
            ? CliLoaders.LoadMainline(cfg, null)
            : MainlineTable.Missing();
        var codex = HeroCodexTable.Load("config/hero_codex.json", mainline);
        var beasts = SpiritBeastTable.Load(beastTablePath ?? cfg.SpiritBeastTable, items, codex);
        return (items, mounts, beasts);
    }

    /// <summary>空串在日志里会吞掉后面的字段，统一写 none。</summary>
    static string Stamp(string value) => value.Length == 0 ? "none" : value;
}
