using Npgsql;
using Social;

using CalendarTable = Mmo.Shared.Domain.CalendarTable;

namespace Social;

// 14.8 D3h：连库只读 / 仅核对用写入口原样搬出 Program.cs。装配（同一份 Load、
// 同一份可选表、同一个进程内 dataSource）与拆分前一致；洞府三条在 Homestead 分片里，
// 这里只多一行调用，判定、打印与退出码（2 / 3 / 5）一行未改。
public sealed partial class SocialCli
{
    /// <summary>起服前跑完所有连库入口；返回 0 / 2 / 3 / 5，调用方直接退出。</summary>
    public async Task<int> RunReadOnlyAsync()
    {

        SectTable table;
        SocialConfig socialConfig;
        SectTaskTable taskTable;
        CalendarTable calendar;
        HomesteadTable? homestead;
        try
        {
            var loaded = SocialLoader.LoadAll(
                configPath, sectTablePath, calendarTablePath, sectTaskTablePath);
            table = loaded.Sect;
            socialConfig = loaded.Config;
            taskTable = loaded.Tasks;
            calendar = loaded.Calendar;
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
        var taskRepo = new SectTaskRepository(dataSource, taskTable, calendar);
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

        if (sectTaskStateOf != 0 && sectTaskStateCharacter != 0)
        {
            var board = await taskRepo.BoardAsync(
                sectTaskStateOf, sectTaskStateCharacter, DateTimeOffset.UtcNow, CancellationToken.None);
            Console.WriteLine(board.Ok
                ? "sect_task_state=ok sect=" + board.SectId
                    + " character=" + sectTaskStateCharacter
                    + " level=" + board.Level
                    + " construction=" + board.Construction
                    + " contribution=" + board.Contribution
                    + " rows=" + board.Rows.Count
                    + " tasks=" + string.Join(",", board.Rows.Select(
                        r => r.TaskId + ":" + r.Done + "/" + r.MaxPerPeriod + ":" + r.PeriodKey))
                : "sect_task_state=" + board.Reason + " sect=" + sectTaskStateOf
                    + " character=" + sectTaskStateCharacter);
        }

        if (sectTaskComplete is not null)
        {
            long.TryParse(sectTaskComplete[0], out var cSect);
            long.TryParse(sectTaskComplete[1], out var cChar);
            int.TryParse(sectTaskComplete[2], out var cLevel);
            var outcome = await taskRepo.CompleteAsync(
                cSect, cChar, cLevel, sectTaskComplete[3], sectTaskComplete[4],
                sectTaskAt ?? DateTimeOffset.UtcNow, CancellationToken.None);
            Console.WriteLine("sect_task_complete=state sect=" + outcome.SectId
                + " task=" + outcome.TaskId
                + " ok=" + (outcome.Ok ? "true" : "false")
                + " reason=" + outcome.Reason
                + " done=" + outcome.Done + "/" + outcome.MaxPerPeriod
                + " contribution=" + outcome.ContributionBefore + "->" + outcome.ContributionAfter
                + " construction=" + outcome.ConstructionBefore + "->" + outcome.ConstructionAfter
                + " granted=" + outcome.GrantedContribution + "/" + outcome.GrantedConstruction
                + " period=" + outcome.PeriodKey);
        }

        if (sectTaskLedgerOf != 0)
        {
            var rows = await taskRepo.LedgerAsync(sectTaskLedgerOf, 50, CancellationToken.None);
            Console.WriteLine("sect_task_ledger=ok sect=" + sectTaskLedgerOf
                + " rows=" + rows.Count
                + " txns=" + string.Join(",", rows.Select(
                    r => r.TxnId + ":" + r.TaskId + ":" + r.PeriodKey
                        + ":" + r.Contribution + "/" + r.Construction
                        + ":character" + r.CharacterId)));
        }

        // C3 洞府三条（独立读回 / 固定事务号重放 / 撤下）见 SocialCliHomestead.cs。
        await RunHomesteadAsync(homes, homestead);
    }
    catch (Exception ex) when (ex is NpgsqlException or IOException)
    {
        Console.WriteLine("db_error=" + ex.GetType().Name + " " + ex.Message);
        return 5;
    }

    return 0;
    }
}

