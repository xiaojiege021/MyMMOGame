using System.Runtime.InteropServices;
using Npgsql;
using Social;

// 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
var configPath = "config/social.json";
string? sectTablePath = null;
var sqlPath = "db/social/social.sql";
var migrate = false;
var sectConfig = false;
var sectList = false;
long sectStateOf = 0;
long sectMembersOf = 0;
long sectSetLevelOf = 0;
var sectSetLevelTo = 0;
var sectPositionConfig = false;
var sectPositionList = false;
long sectPositionStateOf = 0;
long sectPositionMembersOf = 0;
// P2-1a3：四个只读入口，与真实启动共用 SocialLoader.LoadAll 同一组参数。
var sectLeadershipConfig = false;
long sectLeadershipStateOf = 0;
long sectLeadershipMembersOf = 0;
long sectLeaveStateOf = 0;
// P2-1a4：升级只读入口 + 仅核对用的建设赋值，同样与启动共用 LoadAll 同一组参数。
var sectUpgradeConfig = false;
long sectUpgradeStateOf = 0;
long sectUpgradeMembersOf = 0;
long sectConstructionSetOf = 0;
long sectConstructionSetTo = -1;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--sect-table" when i + 1 < args.Length:
            sectTablePath = args[++i];
            break;
        case "--sql" when i + 1 < args.Length:
            sqlPath = args[++i];
            break;
        case "--migrate":
            migrate = true;
            break;
        case "--sect-config":
            sectConfig = true;
            break;
        case "--sect-list":
            sectList = true;
            break;
        case "--sect-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectStateOf);
            break;
        case "--sect-members" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectMembersOf);
            break;
        // 只给核对用：把某个宗门的 level 改到指定档，验上限切换，不做升级逻辑。
        case "--sect-set-level" when i + 2 < args.Length:
            long.TryParse(args[++i], out sectSetLevelOf);
            int.TryParse(args[++i], out sectSetLevelTo);
            break;
        // P2-1a2：四个只读入口，与真实启动共用 SocialLoader.LoadAll 同一组参数。
        case "--sect-position-config":
            sectPositionConfig = true;
            break;
        case "--sect-position-list":
            sectPositionList = true;
            break;
        case "--sect-position-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectPositionStateOf);
            break;
        case "--sect-position-members" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectPositionMembersOf);
            break;
        // P2-1a3：四个只读入口。
        case "--sect-leadership-config":
            sectLeadershipConfig = true;
            break;
        case "--sect-leadership-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectLeadershipStateOf);
            break;
        case "--sect-leadership-members" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectLeadershipMembersOf);
            break;
        case "--sect-leave-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectLeaveStateOf);
            break;
        // P2-1a4：升级只读入口 + 仅核对用的建设赋值（不对外、不作为玩法来源）。
        case "--sect-upgrade-config":
            sectUpgradeConfig = true;
            break;
        case "--sect-upgrade-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectUpgradeStateOf);
            break;
        case "--sect-upgrade-members" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectUpgradeMembersOf);
            break;
        case "--sect-construction-set" when i + 2 < args.Length:
            long.TryParse(args[++i], out sectConstructionSetOf);
            long.TryParse(args[++i], out sectConstructionSetTo);
            break;
        default:
            Console.WriteLine("unknown_arg=" + args[i]);
            return 64;
    }
}

// --sect-config：不连库，逐档打印配置正本。坏配置在这一步就起不来
// （config_error= + 非 0 退出），与真实启动共用同一份 Load + 同一组参数。
if (sectConfig)
{
    try
    {
        var loaded = SocialLoader.LoadAll(configPath, sectTablePath);
        var table = loaded.Sect;
        Console.WriteLine("sect_config_state=ok version=" + table.Version
            + " levels=" + string.Join(",", table.Levels.Select(l => l.Number + ":" + l.MemberCap))
            + " create_gate=" + table.Create.FounderLevelGate
            + " create_gold_kind=" + table.Create.FounderGoldKind
            + " create_gold_cost=" + table.Create.FounderGoldCost
            + " create_gold_pending=" + table.Create.FounderGoldCostPending);
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

// --sect-position-config：不连库，逐项打印四个席位。坏配置在这一步就起不来
// （config_error= + 非 0 退出），与真实启动共用同一份 Load + 同一组参数。
if (sectPositionConfig)
{
    try
    {
        var loaded = SocialLoader.LoadAll(configPath, sectTablePath);
        Console.WriteLine("sect_position_config_state=ok version=" + loaded.Sect.Version
            + " positions=" + string.Join(",",
                loaded.Sect.Positions.Select(p => p.Id + ":" + p.Cap)));
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

// --sect-leadership-config：不连库，逐项打印传位段。坏配置在这一步就起不来
// （config_error= + 非 0 退出），与真实启动共用同一份 Load + 同一组参数。
if (sectLeadershipConfig)
{
    try
    {
        var loaded = SocialLoader.LoadAll(configPath, sectTablePath);
        var t = loaded.Sect.Transfer;
        Console.WriteLine("sect_leadership_config_state=ok version=" + loaded.Sect.Version
            + " idempotency=" + t.Idempotency
            + " old_master_position=" + t.OldMasterPosition
            + " old_master_position_pending=" + t.OldMasterPositionPending);
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

// --sect-upgrade-config：不连库，逐项打印升级档与五档名册上限。坏配置在这一步就起不来
// （config_error= + 非 0 退出），与真实启动共用同一份 Load + 同一组参数。
if (sectUpgradeConfig)
{
    try
    {
        var loaded = SocialLoader.LoadAll(configPath, sectTablePath);
        var u = loaded.Sect.Upgrade;
        Console.WriteLine("sect_upgrade_config_state=ok version=" + loaded.Sect.Version
            + " idempotency=" + u.Idempotency
            + " upgrade_cost_pending=" + u.UpgradeCostPending
            + " upgrade_costs=" + string.Join(",",
                u.Costs.Select(c => c.FromLevel + "->" + c.ToLevel + ":" + c.Cost))
            + " max_level=" + loaded.Sect.MaxLevel
            + " levels=" + string.Join(",",
                loaded.Sect.Levels.Select(l => l.Number + ":" + l.MemberCap)));
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

// 连库入口：--migrate / --sect-list / --sect-state / --sect-members /
// --sect-set-level / --sect-position-* / --sect-leadership-state /
// --sect-leadership-members / --sect-leave-state。都先过与真实启动同一份
// Load + 同一组参数。
// --sect-set-level / --sect-position-list / --sect-position-state /
// --sect-position-members。都先过与真实启动同一份 Load + 同一组参数。
if (migrate || sectList || sectStateOf != 0 || sectMembersOf != 0 || sectSetLevelOf != 0
    || sectPositionList || sectPositionStateOf != 0 || sectPositionMembersOf != 0
    || sectLeadershipStateOf != 0 || sectLeadershipMembersOf != 0 || sectLeaveStateOf != 0
    || sectUpgradeStateOf != 0 || sectUpgradeMembersOf != 0 || sectConstructionSetOf != 0)
{
    SectTable table;
    try
    {
        table = SocialLoader.LoadAll(configPath, sectTablePath).Sect;
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

    var connectionString = DbEnv.Resolve();
    if (connectionString == null)
    {
        Console.WriteLine("db_env=missing 需要 " + DbEnv.EnvName + " 或 " + DbEnv.DefaultEnvFile);
        return 3;
    }

    Console.WriteLine("db_env=" + DbEnv.Source());
    await using var dataSource = NpgsqlDataSource.Create(DbEnv.WithPoolLimit(connectionString, 2));
    var repo = new SectRepository(dataSource, table);

    try
    {
        if (migrate)
        {
            var sql = File.ReadAllText(sqlPath);
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("migrate=ok sql=" + sqlPath);
        }

        if (sectList)
        {
            var all = await repo.ListAsync(CancellationToken.None);
            Console.WriteLine("sect_list_state=ok rows=" + all.Count
                + " sects=" + string.Join(",", all.Select(
                    s => s.SectId + ":" + s.Name + ":" + s.Level + ":" + s.MemberCount)));
        }

        if (sectStateOf != 0)
        {
            var state = await repo.StateAsync(sectStateOf, CancellationToken.None);
            Console.WriteLine(state.Found
                ? "sect_state=ok sect=" + state.SectId
                    + " name=" + state.Name
                    + " level=" + state.Level
                    + " member_cap=" + state.MemberCap
                    + " member_count=" + state.MemberCount
                : "sect_state=missing sect=" + sectStateOf);
        }

        if (sectMembersOf != 0)
        {
            var members = await repo.MembersAsync(sectMembersOf, CancellationToken.None);
            Console.WriteLine("sect_members_state=ok sect=" + sectMembersOf
                + " rows=" + members.Count
                + " ids=" + string.Join(",", members));
        }

        if (sectSetLevelOf != 0 && sectSetLevelTo > 0)
        {
            var changed = await repo.SetLevelAsync(sectSetLevelOf, sectSetLevelTo, CancellationToken.None);
            Console.WriteLine("sect_set_level=" + (changed ? "ok" : "missing")
                + " sect=" + sectSetLevelOf + " level=" + sectSetLevelTo);
        }

        if (sectPositionList)
        {
            var all = await repo.ListAsync(CancellationToken.None);
            foreach (var s in all)
            {
                var ps = await repo.PositionStateAsync(s.SectId, CancellationToken.None);
                Console.WriteLine("sect_position_list_state=ok sect=" + s.SectId
                    + " master=" + ps.Master + " vice_master=" + ps.ViceMaster
                    + " elder=" + ps.Elder + " steward=" + ps.Steward
                    + " disciple=" + ps.Disciple);
            }
            Console.WriteLine("sect_position_list_state=ok rows=" + all.Count);
        }

        if (sectPositionStateOf != 0)
        {
            var ps = await repo.PositionStateAsync(sectPositionStateOf, CancellationToken.None);
            Console.WriteLine(ps.Found
                ? "sect_position_state=ok sect=" + ps.SectId
                    + " master=" + ps.Master + " vice_master=" + ps.ViceMaster
                    + " elder=" + ps.Elder + " steward=" + ps.Steward
                    + " disciple=" + ps.Disciple
                : "sect_position_state=missing sect=" + sectPositionStateOf);
        }

        if (sectPositionMembersOf != 0)
        {
            var rows = await repo.PositionMembersAsync(sectPositionMembersOf, CancellationToken.None);
            Console.WriteLine("sect_position_members_state=ok sect=" + sectPositionMembersOf
                + " rows=" + rows.Count
                + " ids=" + string.Join(",", rows.Select(r => r.CharacterId + ":" + r.Position)));
        }

        if (sectLeadershipStateOf != 0)
        {
            var st = await repo.LeadershipStateAsync(sectLeadershipStateOf, CancellationToken.None);
            Console.WriteLine(st.Found
                ? "sect_leadership_state=ok sect=" + st.SectId
                    + " master_character_id=" + st.MasterCharacterId
                    + " old_master=" + st.LastTransferOldMaster
                    + " new_master=" + st.MasterCharacterId
                    + " master=" + st.MasterCount
                    + " vice_master=" + st.ViceMasterCount
                    + " member_count=" + st.MemberCount
                    + " last_transfer_txn=" + st.LastTransferTxn
                : "sect_leadership_state=missing sect=" + sectLeadershipStateOf);
        }

        if (sectLeadershipMembersOf != 0)
        {
            var rows = await repo.PositionMembersAsync(sectLeadershipMembersOf, CancellationToken.None);
            Console.WriteLine("sect_leadership_members_state=ok sect=" + sectLeadershipMembersOf
                + " rows=" + rows.Count
                + " ids=" + string.Join(",", rows.Select(r => r.CharacterId + ":" + r.Position)));
        }

        if (sectLeaveStateOf != 0)
        {
            var st = await repo.LeadershipStateAsync(sectLeaveStateOf, CancellationToken.None);
            Console.WriteLine(st.Found
                ? "sect_leave_state=ok sect=" + st.SectId
                    + " rows=" + st.MemberCount
                    + " master_character_id=" + st.MasterCharacterId
                : "sect_leave_state=missing sect=" + sectLeaveStateOf);
        }

        if (sectUpgradeStateOf != 0)
        {
            var st = await repo.UpgradeStateAsync(sectUpgradeStateOf, CancellationToken.None);
            Console.WriteLine(st.Found
                ? "sect_upgrade_state=ok sect=" + st.SectId
                    + " level=" + st.Level
                    + " member_cap=" + st.MemberCap
                    + " construction=" + st.Construction
                    + " next_cost=" + st.NextCost
                    + " last_upgrade_txn=" + st.LastUpgradeTxn
                    + " member_count=" + st.MemberCount
                    + " upgrade_txns=" + st.UpgradeTxnCount
                : "sect_upgrade_state=missing sect=" + sectUpgradeStateOf);
        }

        if (sectUpgradeMembersOf != 0)
        {
            var members = await repo.MembersAsync(sectUpgradeMembersOf, CancellationToken.None);
            Console.WriteLine("sect_upgrade_members_state=ok sect=" + sectUpgradeMembersOf
                + " rows=" + members.Count
                + " ids=" + string.Join(",", members));
        }

        if (sectConstructionSetOf != 0 && sectConstructionSetTo >= 0)
        {
            var changed = await repo.SetConstructionAsync(
                sectConstructionSetOf, sectConstructionSetTo, CancellationToken.None);
            Console.WriteLine("sect_construction_set=" + (changed ? "ok" : "missing")
                + " sect=" + sectConstructionSetOf + " construction=" + sectConstructionSetTo);
        }
    }
    catch (Exception ex) when (ex is NpgsqlException or IOException)
    {
        Console.WriteLine("db_error=" + ex.GetType().Name + " " + ex.Message);
        return 5;
    }

    return 0;
}

// 真实启动：与上面所有只读入口共用同一份 Load + 同一组参数。
(SocialConfig Config, SectTable Sect) startup;
try
{
    startup = SocialLoader.LoadAll(configPath, sectTablePath);
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

using var stop = new CancellationTokenSource();
PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.Cancel();
});
PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    stop.Cancel();
});

return await new SocialProcess(startup.Config, startup.Sect).RunAsync(stop.Token);
