using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;

namespace Social;

/// <summary>
/// 社交服装配：宗门与名册只在社交 schema，创建金币的扣减经角色服 ApplyGrant。
/// 启动日志把五级上限逐档打出来，与 --sect-config 只读入口同形，改配置重启后
/// 直接核对得到；配置不对就起不来，不带着半截配置跑。
/// </summary>
public sealed class SocialProcess(SocialConfig config, SectTable sect)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        Console.WriteLine("config=" + config.SectTable);
        Console.WriteLine("listen=" + config.Listen);
        Console.WriteLine("db_pool_max=" + config.DbPoolMax);
        Console.WriteLine("character_upstream=" + config.CharacterUpstream);
        Console.WriteLine("sect_table_version=" + sect.Version
            + " levels=" + string.Join(",", sect.Levels.Select(l => l.Number + ":" + l.MemberCap))
            + " create_gate=" + sect.Create.FounderLevelGate
            + " create_gold_kind=" + sect.Create.FounderGoldKind
            + " create_gold_cost=" + sect.Create.FounderGoldCost
            + " create_gold_pending=" + sect.Create.FounderGoldCostPending);
        // P2-1a2：四个席位逐项打印，与 --sect-position-config 同形。
        Console.WriteLine("positions=" + string.Join(",",
            sect.Positions.Select(p => p.Id + ":" + p.Cap)));

        var connectionString = DbEnv.Resolve();
        Console.WriteLine("db_env=" + DbEnv.Source());

        if (connectionString == null)
        {
            Console.WriteLine("db_missing=1 需要 " + DbEnv.EnvName + " 或 " + DbEnv.DefaultEnvFile);
            return 3;
        }

        await using var dataSource = NpgsqlDataSource.Create(
            DbEnv.WithPoolLimit(connectionString, config.DbPoolMax));
        var sects = new SectRepository(dataSource, sect);
        using var founderGrant = new SectFounderGrant(config.CharacterUpstream);

        var builder = WebApplication.CreateSlimBuilder();
        // 服与服是明文 gRPC（h2c）：端点必须显式只走 HTTP/2。
        if (!Uri.TryCreate(config.Listen, UriKind.Absolute, out var listenUri)
            || !IPAddress.TryParse(listenUri.Host, out var listenAddress))
        {
            Console.WriteLine("config_error=listen 必须是带 IP 的地址：" + config.Listen);
            return 2;
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(listenAddress, listenUri.Port, options => options.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(sect);
        builder.Services.AddSingleton(sects);
        builder.Services.AddSingleton(founderGrant);

        var app = builder.Build();
        app.MapGrpcService<SocialGrpcService>();

        // E3 认这一行：进程起来了，gRPC 也在听。
        Console.WriteLine("ready name=Social pid=" + Environment.ProcessId);

        try
        {
            await app.RunAsync(stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Social pid=" + Environment.ProcessId);
        return 0;
    }
}
