using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;

using CalendarTable = Mmo.Shared.Domain.CalendarTable;
namespace Social;

/// <summary>
/// 社交服装配：宗门与名册只在社交 schema，创建金币的扣减经角色服 ApplyGrant。
/// 启动日志把五级上限逐档打出来，与 --sect-config 只读入口同形，改配置重启后
/// 直接核对得到；配置不对就起不来，不带着半截配置跑。
/// </summary>
public sealed class SocialProcess(
    SocialConfig config, SectTable sect, CalendarTable calendar, SectTaskTable taskTable,
    PartyTable? partyTable, HomesteadTable? homestead, HomesteadPlantTable? plantTable)
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
        // P2-1a3：传位段逐项打印，与 --sect-leadership-config 同形。
        Console.WriteLine("transfer=idempotency:" + sect.Transfer.Idempotency
            + " old_master_position:" + sect.Transfer.OldMasterPosition
            + " old_master_position_pending:" + sect.Transfer.OldMasterPositionPending);
        // P2-1a4：升级档逐项打印，与 --sect-upgrade-config 同形（同一 upgrade_costs 拼法）。
        Console.WriteLine("upgrade_costs=" + string.Join(",",
            sect.Upgrade.Costs.Select(c => c.FromLevel + "->" + c.ToLevel + ":" + c.Cost))
            + " max_level=" + sect.MaxLevel
            + " levels=" + string.Join(",", sect.Levels.Select(l => l.Number + ":" + l.MemberCap)));
        // Q4：宗门任务的周期键与任务表逐条打印，与 --sect-task-config 同形。
        // 日 / 周界只从 Q3 的日历算，这里不另存小时数。
        var now = DateTimeOffset.UtcNow;
        Console.WriteLine("sect_tasks_calendar=day:" + calendar.DayKey(now).ToString("yyyy-MM-dd")
            + " week:" + calendar.WeekStartDate(now).ToString("yyyy-MM-dd")
            + " timezone:" + calendar.TimeZoneId + " daily_reset_hour:" + calendar.DailyResetHour);
        Console.WriteLine("sect_tasks=" + string.Join(",",
            taskTable.Tasks.Select(t => t.Id + ":" + t.Period + ":" + t.MaxPerPeriod
                + ":level" + t.RequireLevel + ":" + t.Contribution + "/" + t.Construction))
            + " pending=" + (taskTable.Pending ? "true" : "false"));
        // Q7：队伍正本逐项打印，与 --party-config 同形（同一份 PartyTable）；缺表降级。
        Console.WriteLine(partyTable is null ? "party=missing"
            : "party_config=ok max_size=" + partyTable.MaxSize
            + " invite_timeout_s=" + partyTable.InviteTimeoutSeconds
            + " ready_check_timeout_s=" + partyTable.ReadyCheckTimeoutSeconds
            + " leader_succession=" + partyTable.LeaderSuccession
            + " loot=" + partyTable.LootRule.Mode + "/" + partyTable.LootRule.Roll
            + " level_range=" + (partyTable.Levels.Enabled ? "on" : "off")
            + "/" + partyTable.Levels.MaxGap
            + " pending=" + (partyTable.Pending ? "true" : "false"));
        // C3：可选内容表。老卡的临时配置目录没有 homestead.json 时只打 missing，
        // 上面 P2-1 的启动日志一行不少（缺表降级不拖垮宗门老链路）。
        Console.WriteLine(homestead is null
            ? "homestead=missing"
            : "homestead_config=ok slots=" + homestead.Slots.Count
                + " items=" + homestead.ItemIds.Count
                + " slot_ids=" + homestead.SlotList
                + " item_ids=" + homestead.ItemList);
        Console.WriteLine(plantTable is null
            ? "homestead_plant=missing"
            : "homestead_plant_config=ok slots=" + plantTable.Slots.Count
                + " crops=" + plantTable.Crops.Count);

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
        // Q7：队伍状态只在内存，社交服重启就散；它不碰 dataSource，也不写任何 schema。
        // 缺表时整个运行时为 null，七个队伍 RPC 一律回 party_unavailable。
        var parties = partyTable is null ? null : new PartyRuntime(partyTable);
        var tasks = new SectTaskRepository(dataSource, taskTable, calendar);
        using var founderGrant = new SectFounderGrant(config.CharacterUpstream);
        using var levelProbe = new SectTaskLevelProbe(config.CharacterUpstream);
        // C3：洞府摆放的可选装配。缺表时 Table/Repository 都是 null，三个新 RPC
        // 回 homestead_unavailable，宗门那批老 RPC 一个字不受影响。
        using var homes = new HomesteadRuntime(homestead, dataSource, config.CharacterUpstream);
        using var plants = new HomesteadPlantRuntime(plantTable, dataSource, config.CharacterUpstream);

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
        builder.Services.AddSingleton(taskTable);
        builder.Services.AddSingleton(calendar);
        if (partyTable is not null)
        {
            builder.Services.AddSingleton(partyTable);
        }

        if (parties is not null)
        {
            builder.Services.AddSingleton(parties);
        }
        builder.Services.AddSingleton(sects);
        builder.Services.AddSingleton(tasks);
        builder.Services.AddSingleton(founderGrant);
        builder.Services.AddSingleton(levelProbe);
        builder.Services.AddSingleton(homes);
        builder.Services.AddSingleton(plants);

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
