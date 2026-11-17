using Npgsql;

namespace Gateway;

public sealed class GatewayProcess(string configPath)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        GatewayConfig config;
        try
        {
            config = GatewayConfig.Load(configPath);
        }
        catch (ConfigException ex)
        {
            // 配置不对就起不来，不带着半截配置跑。
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
        // P3-4a1：赛季当前目标只读口，认令牌后转给全局服务，不读请求参数。
        SeasonApi.Map(app, tokens, config);
        // P3-4a2：通行证只读与领取。季由角色服向全局服务确认，客户端只带角色号、轨与阶。
        PassApi.Map(app, tokens, config);
        // P3-4b：角色位与外观收藏两个账号级只读口，名额与收藏都归角色服回答。
        SlotApi.Map(app, tokens, config);
        // C1：主线十二场的只读与交任务。事务号由网关生成，进度与门槛全在角色服。
        MainlineApi.Map(app, tokens, config);
        // P1-5c1：商城只认清单，清单外拒绝且不扣元宝。
        ShopApi.Map(app, tokens, config);
        // P1-5c7：限时经验道具走网关到角色服，扣一张与写到期时刻在同一笔事务里。
        ExpBoosterApi.Map(app, tokens, config);
        UnbindApi.Map(app, tokens, config);
        RechargeApi.Map(app, tokens, config);
        // P2-1a1：建 1 级宗门 + 收人入名册。认令牌、复核角色归属、服务端生成事务号。
        SectApi.Map(app, tokens, config);
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

        // 表世界分线：认令牌、复核角色归属，再 gRPC 转给世界服。
        WorldApi.Map(app, tokens, config);

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
