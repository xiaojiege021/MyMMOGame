using Mmo.Shared.Domain;
using Npgsql;

namespace Gateway;

public sealed class GatewayProcess(string configPath)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        GatewayConfig config;
        CalendarTable calendar;
        try
        {
            config = GatewayConfig.Load(configPath);
            // Q3/G1：展示时刻只认这一份日历；坏表起不来，不带着半截时钟跑。
            calendar = CalendarTable.Load(config.CalendarTable);
        }
        catch (ConfigException ex)
        {
            // 配置不对就起不来，不带着半截配置跑。
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }
        catch (Mmo.Shared.Domain.ConfigException ex)
        {
            // 日历表是共享域的类型；错误口径与网关自己的配置一致。
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        Console.WriteLine("config=" + configPath);
        Console.WriteLine("listen=" + config.Listen);
        Console.WriteLine("token_ttl_s=" + config.TokenTtlSeconds);
        Console.WriteLine("pbkdf2_iterations=" + config.Pbkdf2Iterations);
        Console.WriteLine("battle_upstream=" + config.BattleUpstream);
        Console.WriteLine("character_upstream=" + config.CharacterUpstream);
        Console.WriteLine("world_upstream=" + config.WorldUpstream);
        Console.WriteLine("global_upstream=" + config.GlobalUpstream);
        Console.WriteLine("social_upstream=" + config.SocialUpstream);
        Console.WriteLine("calendar=ok timezone=" + calendar.TimeZoneId
            + " daily_reset_hour=" + calendar.DailyResetHour
            + " weekly_reset=" + calendar.WeeklyResetDay.ToString().ToLowerInvariant()
            + "@" + calendar.WeeklyResetHour
            + " season_start=" + calendar.SeasonStartDate.ToString("yyyy-MM-dd")
            + " season_length_days=" + calendar.SeasonLengthDays);

        // 连接串带密码，日志只打来源。
        var connectionString = DbEnv.Resolve();
        Console.WriteLine("db_env=" + DbEnv.Source());

        // 没有连接串也照常起进程：进程边界与库无关，登录时才回 503。
        // 这样 E3 的换进程核对不依赖库，缺凭据的环境也能验进程生命周期。
        NpgsqlDataSource? dataSource = null;
        AccountRepository? accounts = null;
        if (connectionString != null)
        {
            dataSource = NpgsqlDataSource.Create(DbEnv.WithPoolLimit(connectionString, 8));
            accounts = new AccountRepository(dataSource);
        }

        var tokens = new TokenStore();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(config.Listen);
        var app = builder.Build();

        // D10：令牌只在这道闸里解析一次，解析不出来的就地 401 短路——
        // 后面的路由体不再各自读令牌、各自回 401。豁免表与拒绝码在 GatewayAuth 里只写一份。
        GatewayAuth.UseTokenGate(app, tokens);

        app.MapGet("/health", () => Results.Json(new { status = "ok", tokens = tokens.Count }));

        if (accounts == null)
        {
            app.MapPost("/login", () => Results.Json(
                new { error = "account_store_unavailable" }, statusCode: 503));
        }
        else
        {
            LoginApi.Map(app, accounts, tokens, config);
        }

        // 战斗长连接只从网关进：握手认令牌，认过之后字节流转发给战斗服。
        BattleProxy.Map(app, tokens, config);

        // 建角色同样只从网关进：认令牌，再 gRPC 转给角色服。
        CharacterApi.Map(app, tokens, config);

        // 拍卖行：认令牌、复核归属，再 gRPC 转给全局服务。
        AuctionApi.Map(app, tokens, config);
        // B5：摆摊。开摊 / 关摊 / 上架 / 看摊走角色服，显示走世界服，成交复用拍卖那条链。
        StallApi.Map(app, tokens, config);
        // P3-4a1：赛季当前目标只读口，认令牌后转给全局服务，不读请求参数。
        SeasonApi.Map(app, tokens, config, calendar);
        // P3-4a2：通行证只读与领取。季由角色服向全局服务确认，客户端只带角色号、轨与阶。
        PassApi.Map(app, tokens, config);
        // P3-4b：角色位与外观收藏两个账号级只读口，名额与收藏都归角色服回答。
        SlotApi.Map(app, tokens, config);
        // C1：主线十二场的只读与交任务。事务号由网关生成，进度与门槛全在角色服。
        MainlineApi.Map(app, tokens, config);
        // Q5/J2：主城修炼 / 境界任务的只读面板与完成口。事务号由网关生成，
        // 灵石数量、城池抽成比例与门槛判定全在角色服，网关不本地算。
        CityTaskApi.Map(app, tokens, config);
        // P1-5c1：商城只认清单，清单外拒绝且不扣元宝。
        ShopApi.Map(app, tokens, config);
        // P1-5c7：限时经验道具走网关到角色服，扣一张与写到期时刻在同一笔事务里。
        ExpBoosterApi.Map(app, tokens, config);
        UnbindApi.Map(app, tokens, config);
        RechargeApi.Map(app, tokens, config);
        // P2-1a1：建 1 级宗门 + 收人入名册。认令牌、复核角色归属、服务端生成事务号。
        SectApi.Map(app, tokens, config);
        // Q7：队伍（邀请 / 接受 / 准备 / 离队 / 解散 / 只读）。人数上限与两个超时
        // 只在社交服按 config/party.json 判，网关不算。
        PartyApi.Map(app, tokens, config);
        // C3：洞府摆放（只做展示和生活）。认令牌、复核归属、生成事务号，
        // 摆放事实与拥有判定全在社交服 / 角色服。
        HomesteadApi.Map(app, tokens, config);
        HomesteadPlantApi.Map(app, tokens, config);
        HomeCoinShopApi.Map(app, tokens, config);
        HeroCodexApi.Map(app, tokens, config);
        // N13：境界子阶只读口与升阶口。网关只认令牌、复核归属、生成事务号，
        // 段内阶与被动只在角色服按配置算，网关不本地算、伤害公式不接。
        RealmPhaseApi.Map(app, tokens, config);

        // N5：竞技场与战场的 HTTP 进场口（职业与分支只读角色服，连战斗仍走 /battle）。
        VersusApi.Map(app, tokens, config);
        // B11：竞技场赛季段位。只读分 / 档 / 排行 + 领这一季的专属外观。
        ArenaRankApi.Map(app, tokens, config);

        // 表世界分线：认令牌、复核角色归属，再 gRPC 转给世界服。
        WorldApi.Map(app, tokens, config, calendar);

        // 表世界位置流：同一套令牌与归属复核，WebSocket 帧转 gRPC 双向流。
        WorldStreamProxy.Map(app, tokens, config);

        // 这一行是 E3 认的：进程起来了、后面还没开始接客。
        Console.WriteLine("ready name=Gateway pid=" + Environment.ProcessId);

        try
        {
            await app.RunAsync(stopping);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (dataSource != null)
            {
                await dataSource.DisposeAsync();
            }
        }

        Console.WriteLine("stopped name=Gateway pid=" + Environment.ProcessId);
        return 0;
    }
}
