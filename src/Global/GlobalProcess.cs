using System.Net;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Mmo.Proto.Character;
using Mmo.Shared.Domain;
using Npgsql;

namespace Global;

public sealed class GlobalProcess(string configPath)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        GlobalConfig config;
        CalendarTable calendar;
        try
        {
            config = GlobalConfig.Load(configPath);
            // Q3/G1：赛季边界只认这一份日历；坏表起不来。
            calendar = CalendarTable.Load(config.CalendarTable);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }
        catch (Mmo.Shared.Domain.ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        Console.WriteLine("config=" + configPath);
        Console.WriteLine("listen=" + config.Listen);
        Console.WriteLine("character_upstream=" + config.CharacterUpstream);
        var bootNow = DateTimeOffset.UtcNow;
        var bootWindow = calendar.SeasonOf(bootNow);
        Console.WriteLine("calendar=ok timezone=" + calendar.TimeZoneId
            + " daily_reset_hour=" + calendar.DailyResetHour
            + " weekly_reset=" + calendar.WeeklyResetDay.ToString().ToLowerInvariant()
            + "@" + calendar.WeeklyResetHour
            + " day=" + calendar.DayKey(bootNow).ToString("yyyy-MM-dd")
            + " week_start=" + calendar.WeekStartDate(bootNow).ToString("yyyy-MM-dd")
            + " season_start=" + calendar.SeasonStartDate.ToString("yyyy-MM-dd")
            + " season_length_days=" + calendar.SeasonLengthDays
            + " season_index=" + bootWindow.Index
            + " season_window_start=" + calendar.FormatLocal(bootWindow.StartsAt)
            + " season_window_end=" + calendar.FormatLocal(bootWindow.EndsAt));

        // P3-2a：挂单时校验灵石档位，加载失败就起不来，不带着半截配置跑。
        StoneTierTable stoneTiers;
        try
        {
            stoneTiers = StoneTierTable.Load(config.StoneExchangeTable);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        // 档位数与版本都从配置读出来，不在代码里写死。
        Console.WriteLine("stone_tiers=" + stoneTiers.Count + " version=" + stoneTiers.Version);

        // P3-4a1：赛季表随配置目录热载（E4 / 技术架构 7 节），换季不重启进程、不写库。
        // 按 --config 所在目录找，老卡的临时配置目录没有 season.json 时降级成 missing，
        // 全局服务照常起，不受这张可选表影响。
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? "config";
        using var seasons = new SeasonCatalog(configDirectory, config.SeasonTableFile);
        var bootSeason = seasons.Current();
        Console.WriteLine(bootSeason.Found
            ? "season=" + bootSeason.CurrentSeason
              + " target=" + bootSeason.CurrentTarget
              + " previous=" + bootSeason.PreviousSeason
              + " previous_target=" + bootSeason.PreviousTarget
              + " count=" + bootSeason.Count
            : "season=missing");

        var connectionString = DbEnv.Resolve();
        Console.WriteLine("db_env=" + DbEnv.Source());
        if (connectionString == null)
        {
            Console.WriteLine("db_missing=1 需要 " + DbEnv.EnvName + " 或 " + DbEnv.DefaultEnvFile);
            return 3;
        }

        await using var dataSource = NpgsqlDataSource.Create(
            DbEnv.WithPoolLimit(connectionString, config.DbPoolMax));
        var listings = new AuctionListingRepository(dataSource);
        var ingots = new IngotListingRepository(dataSource);
        var stones = new StoneListingRepository(dataSource);

        // 成交要转给角色服：货币、物品与成交状态都在那一笔角色服事务里。
        var channel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(channel);

        var builder = WebApplication.CreateSlimBuilder();
        if (!Uri.TryCreate(config.Listen, UriKind.Absolute, out var listenUri)
            || !IPAddress.TryParse(listenUri.Host, out var listenAddress))
        {
            Console.WriteLine("config_error=listen 必须是带 IP 的地址：" + config.Listen);
            return 2;
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(listenAddress, listenUri.Port, options => options.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(listings);
        builder.Services.AddSingleton(ingots);
        builder.Services.AddSingleton(stones);
        builder.Services.AddSingleton(stoneTiers);
        builder.Services.AddSingleton(character);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(seasons);

        var app = builder.Build();
        app.MapGrpcService<GlobalGrpcService>();

        Console.WriteLine("ready name=Global pid=" + Environment.ProcessId);
        try
        {
            await app.RunAsync(stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Global pid=" + Environment.ProcessId);
        return 0;
    }
}
