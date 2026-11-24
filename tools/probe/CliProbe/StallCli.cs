using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// B5 摆摊的只读与连库核对。
///   · 只读不连库：--stall-config（配置现值）、--stall-table-path（坏配置走这一条）。
///   · 连库：--stall-open / --stall-list / --stall-view / --stall-close / --stall-buy。
/// 走的是起服同一套 Load 与同一个仓储；成交也是同一个 SettleAsync，探针不另算税。
/// </summary>
internal static class StallCli
{
    public static bool Handles(string flag) => flag is
        "--stall-config" or "--stall-table-path" or "--stall-open" or "--stall-list"
        or "--stall-view" or "--stall-close" or "--stall-buy";

    public static int Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--stall-config":
                stallConfig = true;
                return 0;
            case "--stall-table-path" when i + 1 < args.Length:
                stallTablePath = args[++i];
                return 0;
            case "--stall-open" when i + 3 < args.Length:
                long.TryParse(args[++i], out stallOpenOf);
                stallOpenRegion = args[++i];
                stallOpenTitle = args[++i];
                return 0;
            case "--stall-list" when i + 4 < args.Length:
                long.TryParse(args[++i], out stallListOf);
                stallListStall = args[++i];
                int.TryParse(args[++i], out stallListSlot);
                long.TryParse(args[++i], out stallListPrice);
                return 0;
            case "--stall-view" when i + 1 < args.Length:
                stallViewStall = args[++i];
                return 0;
            case "--stall-close" when i + 2 < args.Length:
                long.TryParse(args[++i], out stallCloseOf);
                stallCloseStall = args[++i];
                return 0;
            case "--stall-buy" when i + 2 < args.Length:
                long.TryParse(args[++i], out stallBuyOf);
                long.TryParse(args[++i], out stallBuyListing);
                return 0;
            default:
                return 64;
        }
    }

    public static int? TryReadOnly()
    {
        if (!stallConfig && stallTablePath is null)
        {
            return null;
        }

        try
        {
            var cfg = CharacterConfig.Load(configPath);
            var table = StallTable.Load(stallTablePath ?? cfg.StallTable);
            Console.WriteLine("stall_config=ok version=" + table.Version
                + " pending=" + (table.Pending ? "true" : "false")
                + " tax_rate_pct=" + table.TaxRatePct
                + " tax_min=" + StallTable.WrittenMinRatePct
                + " tax_max=" + StallTable.WrittenMaxRatePct
                + " slots=" + table.Slots
                + " min_level=" + table.MinLevel
                + " cross_server=" + (table.CrossServer ? "true" : "false")
                + " title_max_length=" + table.TitleMaxLength);
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
        if (stallOpenOf == 0 && stallListOf == 0 && stallViewStall is null
            && stallCloseOf == 0 && stallBuyOf == 0)
        {
            return null;
        }

        var cfg = CharacterConfig.Load(configPath);
        var items = ItemTable.Load("config/items.json");
        var stallTable = StallTable.Load(stallTablePath ?? cfg.StallTable);
        var stalls = new StallRepository(ctx.DataSource, stallTable, items, cfg);

        if (stallOpenOf != 0)
        {
            var stallId = "stall_cli_" + Guid.NewGuid().ToString("N")[..16];
            var outcome = await stalls.OpenAsync(
                stallOpenOf, stallOpenRegion ?? "", 0, 0, stallOpenTitle ?? "",
                stallId, CancellationToken.None);
            Console.WriteLine("stall_open=" + (outcome.Ok ? "ok" : "reject")
                + " reason=" + Stamp(outcome.Reason)
                + " stall=" + Stamp(outcome.StallId)
                + " owner=" + Stamp(outcome.OwnerName)
                + " region=" + Stamp(outcome.RegionId)
                + " live=" + outcome.LiveListings
                + " slots=" + outcome.Slots
                + " min_level=" + outcome.MinLevel
                + " tax_rate_pct=" + outcome.TaxRatePct);
        }

        if (stallListOf != 0 && stallListStall is not null)
        {
            var stall = (await stalls.ViewAsync(stallListStall, CancellationToken.None));
            var txnId = "stall_list:" + Guid.NewGuid().ToString("N");
            var outcome = await stalls.ListAsync(
                stallListOf, stallListStall, stallListSlot, 1, stallListPrice,
                txnId, CancellationToken.None);
            Console.WriteLine("stall_list=" + (outcome.Ok ? "ok" : "reject")
                + " reason=" + Stamp(outcome.Reason)
                + " listing=" + outcome.ListingId
                + " stall=" + Stamp(outcome.StallId)
                + " slot=" + outcome.Slot
                + " item=" + Stamp(outcome.ItemId)
                + " count=" + outcome.Count
                + " price=" + outcome.PriceGold
                + " live=" + outcome.LiveListings
                + " slots=" + outcome.Slots
                + " owner=" + Stamp(stall.OwnerName)
                + " state=" + Stamp(stall.State));
        }

        if (stallViewStall is not null)
        {
            var snapshot = await stalls.ViewAsync(stallViewStall, CancellationToken.None);
            Console.WriteLine("stall_view=" + (snapshot.Found ? "ok" : "missing")
                + " stall=" + Stamp(snapshot.StallId)
                + " owner=" + Stamp(snapshot.OwnerName)
                + " region=" + Stamp(snapshot.RegionId)
                + " state=" + Stamp(snapshot.State)
                + " live=" + snapshot.LiveListings
                + " slots=" + snapshot.Slots);
            foreach (var row in snapshot.Listings)
            {
                Console.WriteLine("stall_item=slot" + row.Slot
                    + " listing=" + row.ListingId
                    + " item=" + row.ItemId
                    + " name=" + row.ItemName
                    + " count=" + row.Count
                    + " price=" + row.PriceGold
                    + " bound=" + (row.Bound ? "true" : "false"));
            }
        }

        if (stallCloseOf != 0 && stallCloseStall is not null)
        {
            var outcome = await stalls.CloseAsync(
                stallCloseOf, stallCloseStall, CancellationToken.None);
            Console.WriteLine("stall_close=" + (outcome.Ok ? "ok" : "reject")
                + " reason=" + Stamp(outcome.Reason)
                + " stall=" + Stamp(outcome.StallId)
                + " cancelled=" + outcome.CancelledListings);
        }

        if (stallBuyOf != 0 && stallBuyListing != 0)
        {
            // 与网关同一条链：成交走 SettleAsync 那一笔事务，税按挂单 channel 取配置。
            var currencies = CurrencyTable.Load("config/currency.json");
            var auctionTable = AuctionTable.Load(cfg.AuctionTable, currencies);
            var auctions = new AuctionRepository(
                ctx.DataSource, auctionTable, stallTable, items, cfg);
            var txnId = "stall_buy:" + Guid.NewGuid().ToString("N");
            var outcome = await auctions.SettleAsync(
                stallBuyOf, stallBuyListing, txnId, CancellationToken.None);
            Console.WriteLine("stall_buy=" + (outcome.Ok ? "ok" : "reject")
                + " reason=" + Stamp(outcome.Reason)
                + " listing=" + outcome.ListingId
                + " stall=" + Stamp(outcome.StallId)
                + " price=" + outcome.PriceGold
                + " tax=" + outcome.TaxGold
                + " net=" + outcome.NetGold
                + " tax_rate_pct=" + outcome.TaxRatePct
                + " buyer_gold=" + outcome.BuyerGold
                + " seller_gold=" + outcome.SellerGold
                + " item=" + Stamp(outcome.ItemId)
                + " count=" + outcome.Count
                + " live=" + outcome.LiveListings
                + " duplicate=" + (outcome.Duplicate ? "true" : "false"));
        }

        return 0;
    }

    static string Stamp(string value) => value.Length == 0 ? "none" : value;
}
