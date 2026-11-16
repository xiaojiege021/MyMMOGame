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
long sectRosterOf = 0;
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
// P3-3a3：宗门科技等级只读入口 + 仅核对用的写入口（方案没写定科技产出规则）。
long sectTechStateOf = 0;
long sectTechSetOf = 0;
int sectTechSetTo = -1;
// C3 洞府摆放：只读配置口、连库只读口、两个只给核对用的固定事务号写入口。
// 可选表默认跟 --config 所在目录走（老卡的临时配置目录没有它 → 缺表降级）；
// --homestead-table 指向坏表时，与真实启动走同一份硬校验。
string? homesteadTablePath = null;
var homesteadConfig = false;
long homesteadStateOf = 0;
string[]? homesteadPlace = null;
string[]? homesteadRemove = null;
string? homesteadPlantTablePath = null;

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
        // N2：名册逐条只读，不写库。
        case "--sect-roster" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectRosterOf);
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
        // P3-3a3：只读查角色所属宗门的科技等级。
        case "--sect-tech-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out sectTechStateOf);
            break;
        // 只给核对用：写某宗门的科技等级，不做升级逻辑（方案没写定产出）。
        case "--raw-sect-tech-write" when i + 2 < args.Length:
            long.TryParse(args[++i], out sectTechSetOf);
            int.TryParse(args[++i], out sectTechSetTo);
            break;
        // C3：可选内容表显式路径（坏配置核对用），不显式登记进 config/social.json。
        case "--homestead-table" when i + 1 < args.Length:
            homesteadTablePath = args[++i];
            break;
        case "--homestead-config":
            homesteadConfig = true;
            break;
        case "--homestead-state" when i + 1 < args.Length:
            long.TryParse(args[++i], out homesteadStateOf);
            break;
        // 只给核对用：固定事务号连发两次验幂等。参数 = 账号 角色号 格位 物品 事务号。
        case "--homestead-place" when i + 5 < args.Length:
            homesteadPlace = args.Skip(i + 1).Take(5).ToArray();
            i += 5;
            break;
        // 只给核对用：固定事务号撤下。参数 = 角色号 格位 事务号。
        case "--homestead-remove" when i + 3 < args.Length:
            homesteadRemove = args.Skip(i + 1).Take(3).ToArray();
            i += 3;
            break;
        case "--homestead-plant-table" when i + 1 < args.Length:
            homesteadPlantTablePath = args[++i];
            break;
        default:
            Console.WriteLine("unknown_arg=" + args[i]);
            return 64;
    }
}

// C3：可选内容表的默认路径跟 --config 所在目录走。老卡的临时配置目录里
// 没有 homestead.json，社交服照常起来（homestead=missing）；产品正本在
// config/ 目录里，与 config/social.json 同目录。显式 --homestead-table 优先。
var homesteadPath = homesteadTablePath
    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "homestead.json");
var homesteadPlantPath = homesteadPlantTablePath
    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "homestead_plant.json");

// --homestead-config：不连库，逐项打印格位与可摆物品（后者只来自 items.json）。
// 坏表在这一步就起不来（config_error= + 非 0 退出），与真实启动同一份硬校验。
if (homesteadConfig)
{
    try
    {
        var table = HomesteadTable.LoadIfPresent(homesteadPath);
        Console.WriteLine(table is null
            ? "homestead_config_state=missing"
            : "homestead_config_state=ok version=" + HomesteadTable.Version
                + " pending=" + (table.Pending ? "true" : "false")
                + " slots=" + table.Slots.Count + " slot_ids=" + table.SlotList
                + " items=" + table.ItemIds.Count + " item_ids=" + table.ItemList
                + " items_config=" + table.ItemsConfig);
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
    || sectUpgradeStateOf != 0 || sectUpgradeMembersOf != 0 || sectConstructionSetOf != 0
    || sectTechStateOf != 0 || sectTechSetOf != 0 || sectRosterOf != 0
    // C3：新增的连库只读 / 核对写入口也必须登记进这条「一次性命令」长条件，
    // 否则社交服会走正常启动去 bind 65000，核对时表现为 address already in use。
    || homesteadStateOf != 0 || homesteadPlace is not null || homesteadRemove is not null)
{
    SectTable table;
    SocialConfig socialConfig;
    HomesteadTable? homestead;
    try
    {
        var loaded = SocialLoader.LoadAll(configPath, sectTablePath);
        table = loaded.Sect;
        socialConfig = loaded.Config;
        homestead = HomesteadTable.LoadIfPresent(homesteadPath);
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
    // C3：连库核对入口与真实启动共用同一份可选表装配。
    using var homes = new HomesteadRuntime(homestead, dataSource, socialConfig.CharacterUpstream);

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

        if (sectRosterOf != 0)
        {
            var roster = await repo.RosterAsync(sectRosterOf, CancellationToken.None);
            Console.WriteLine(roster.Found
                ? "sect_roster=ok sect=" + roster.SectId
                    + " rows=" + roster.Members.Count
                    + " members=" + string.Join(",", roster.Members.Select(
                        m => m.CharacterId + ":" + m.Position))
                : "sect_roster=missing sect=" + sectRosterOf);
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

        if (sectTechStateOf != 0)
        {
            var st = await repo.SectTechAsync(sectTechStateOf, CancellationToken.None);
            Console.WriteLine("sect_tech_state=ok character=" + sectTechStateOf
                + " found=" + (st.Found ? "true" : "false")
                + " sect=" + st.SectId
                + " level=" + st.TechLevel);
        }

        if (sectTechSetOf != 0 && sectTechSetTo >= 0)
        {
            var changed = await repo.SetSectTechAsync(
                sectTechSetOf, sectTechSetTo, CancellationToken.None);
            Console.WriteLine("raw_sect_tech=" + (changed ? "ok" : "missing")
                + " sect=" + sectTechSetOf + " level=" + sectTechSetTo);
        }

        if (sectConstructionSetOf != 0 && sectConstructionSetTo >= 0)
        {
            var changed = await repo.SetConstructionAsync(
                sectConstructionSetOf, sectConstructionSetTo, CancellationToken.None);
            Console.WriteLine("sect_construction_set=" + (changed ? "ok" : "missing")
                + " sect=" + sectConstructionSetOf + " construction=" + sectConstructionSetTo);
        }

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
    catch (Exception ex) when (ex is NpgsqlException or IOException)
    {
        Console.WriteLine("db_error=" + ex.GetType().Name + " " + ex.Message);
        return 5;
    }

    return 0;
}

// 真实启动：与上面所有只读入口共用同一份 Load + 同一组参数。
(SocialConfig Config, SectTable Sect) startup;
HomesteadTable? startupHomestead;
HomesteadPlantTable? startupHomesteadPlant;
try
{
    startup = SocialLoader.LoadAll(configPath, sectTablePath);
    startupHomestead = HomesteadTable.LoadIfPresent(homesteadPath);
    startupHomesteadPlant = HomesteadPlantTable.LoadIfPresent(homesteadPlantPath);
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

return await new SocialProcess(
    startup.Config, startup.Sect, startupHomestead, startupHomesteadPlant).RunAsync(stop.Token);
