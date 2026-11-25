using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Character;

/// <summary>
/// gRPC 宿主装配（D2d：从 CharacterProcess 里原样搬出来）。明文 h2c、端点只走 HTTP/2，
/// 与之前一致；监听地址不合法时抛 ConfigException，由入口打 config_error 并退 2。
/// </summary>
public static class GrpcHost
{
    public static WebApplication Build(
        CharacterConfig config, CharacterTables tables, CharacterServices services)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // 服与服是明文 gRPC（h2c）：端点必须显式只走 HTTP/2，
        // 否则 Kestrel 在明文上只谈 HTTP/1.1，gRPC 握手直接失败。
        if (!Uri.TryCreate(config.Listen, UriKind.Absolute, out var listenUri)
            || !IPAddress.TryParse(listenUri.Host, out var listenAddress))
        {
            throw new ConfigException("listen 必须是带 IP 的地址：" + config.Listen);
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(listenAddress, listenUri.Port, options => options.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(services.Characters);
        builder.Services.AddSingleton(services.Inventory);
        builder.Services.AddSingleton(services.Bags);
        builder.Services.AddSingleton(services.Equips);
        builder.Services.AddSingleton(services.Currency);
        builder.Services.AddSingleton(services.Grants);
        builder.Services.AddSingleton(services.Talismans);
        builder.Services.AddSingleton(services.Scrolls);
        builder.Services.AddSingleton(tables.Races);
        builder.Services.AddSingleton(tables.Branches);
        builder.Services.AddSingleton(tables.Classes);
        builder.Services.AddSingleton(tables.Items);
        builder.Services.AddSingleton(tables.Currencies);
        builder.Services.AddSingleton(tables.Dungeons);
        builder.Services.AddSingleton(tables.Realms);
        builder.Services.AddSingleton(services.DungeonEntries);
        builder.Services.AddSingleton(services.HardEntries);
        builder.Services.AddSingleton(services.WeeklyShares);
        builder.Services.AddSingleton(services.HotSpring);
        builder.Services.AddSingleton(services.ExpSettlements);
        builder.Services.AddSingleton(tables.ExpTable);
        builder.Services.AddSingleton(tables.EnhanceTable);
        builder.Services.AddSingleton(services.Enhances);
        builder.Services.AddSingleton(tables.TransferTable);
        builder.Services.AddSingleton(services.Transfers);
        builder.Services.AddSingleton(tables.CrystalTable);
        builder.Services.AddSingleton(tables.Grades);
        builder.Services.AddSingleton(tables.Sets);
        builder.Services.AddSingleton(services.Crystals);
        builder.Services.AddSingleton(tables.AuctionTable);
        builder.Services.AddSingleton(tables.StallTable);
        builder.Services.AddSingleton(tables.AwakenTable);
        builder.Services.AddSingleton(tables.ShopTable);
        builder.Services.AddSingleton(tables.HomeCoinShopTable);
        builder.Services.AddSingleton(tables.NpcShopTable);
        builder.Services.AddSingleton(services.NpcShops);
        builder.Services.AddSingleton(tables.TalismanTable);
        builder.Services.AddSingleton(tables.PillTable);
        builder.Services.AddSingleton(services.Pills);
        builder.Services.AddSingleton(tables.ScrollTable);
        builder.Services.AddSingleton(services.Boosters);
        builder.Services.AddSingleton(tables.BoosterTable);
        builder.Services.AddSingleton(services.Unbinds);
        builder.Services.AddSingleton(tables.UnbindTable);
        builder.Services.AddSingleton(services.Recharges);
        builder.Services.AddSingleton(services.RealmAdvances);
        builder.Services.AddSingleton(services.RealmPhases);
        builder.Services.AddSingleton(tables.RechargeTable);
        builder.Services.AddSingleton(services.Auctions);
        builder.Services.AddSingleton(services.Stalls);
        builder.Services.AddSingleton(services.Ingots);
        builder.Services.AddSingleton(tables.StoneExchange);
        builder.Services.AddSingleton(tables.CityTable);
        builder.Services.AddSingleton(services.StoneExchanges);
        builder.Services.AddSingleton(services.StoneAuctions);
        builder.Services.AddSingleton(services.SiegeTerritories);
        builder.Services.AddSingleton(tables.SiegeTerritoryPolicy);
        builder.Services.AddSingleton(services.Social);
        builder.Services.AddSingleton(tables.SeasonPassTable);
        builder.Services.AddSingleton(tables.SeasonPassProgressTable);
        builder.Services.AddSingleton(tables.NameRulesTable);
        builder.Services.AddSingleton(services.SeasonPassPoints);
        builder.Services.AddSingleton(services.SeasonPasses);
        builder.Services.AddSingleton(tables.ArenaRankTable);
        builder.Services.AddSingleton(services.ArenaRanks);
        builder.Services.AddSingleton(tables.MonthlyBundleTable);
        builder.Services.AddSingleton(services.MonthlyBundles);
        builder.Services.AddSingleton(tables.CharacterSlotTable);
        builder.Services.AddSingleton(services.CharacterSlots);
        builder.Services.AddSingleton(services.BagExpands);
        builder.Services.AddSingleton(services.Wardrobe);
        builder.Services.AddSingleton(tables.MainlineTable);
        builder.Services.AddSingleton(services.Mainlines);
        builder.Services.AddSingleton(tables.HeroCodexTable);
        builder.Services.AddSingleton(services.HeroCodex);
        builder.Services.AddSingleton(tables.MountTable);
        builder.Services.AddSingleton(tables.SpiritBeastTable);
        builder.Services.AddSingleton(services.Mounts);
        builder.Services.AddSingleton(services.SpiritBeasts);
        builder.Services.AddSingleton(tables.RecipeTable);
        builder.Services.AddSingleton(tables.CityTaskTable);
        builder.Services.AddSingleton(services.CityTasks);
        builder.Services.AddSingleton(services.Deserters);
        builder.Services.AddSingleton(services.Crafts);
        builder.Services.AddSingleton(tables.DungeonDropTable);
        builder.Services.AddSingleton(tables.MainlineDropTable);
        builder.Services.AddSingleton(services.SeasonSource);
        builder.Services.AddSingleton(config);


        var app = builder.Build();
        app.MapGrpcService<CharacterGrpcService>();
        return app;
    }
}
