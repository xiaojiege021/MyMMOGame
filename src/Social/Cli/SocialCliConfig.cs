namespace Social;

// 14.8 D3h：不连库的配置口原样搬出 Program.cs（洞府 / 队伍 / 队伍状态机 / 宗门 /
/// 席位 / 传位 / 升级 / 任务），判定与打印一行未改，命中任一个就返回 0，
/// 坏表在这一步照旧 config_error= + 退出 2。一个都没命中返回 null，继续往下走。
public sealed partial class SocialCli
{
    /// <summary>命中配置口就返回它的退出码；一个都没命中返回 null。</summary>
    public int? RunConfigOnly()
    {

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

        // --party-config：不连库，逐项打印队伍正本。坏表在这一步就起不来
        // （config_error= + 非 0 退出），与真实启动共用同一份 Load + 同一组参数。
        if (partyConfig)
        {
            try
            {
                var loaded = SocialLoader.LoadAll(
                    configPath, sectTablePath, calendarTablePath, sectTaskTablePath, partyTablePath);
                var table = loaded.Party;
                if (table is null)
                {
                    Console.WriteLine("party=missing（可选表：缺 " + partyTablePath + " / config/party.json）");
                    return 0;
                }

                Console.WriteLine("party_config_state=ok version=" + table.Version
                    + " max_size=" + table.MaxSize
                    + " invite_timeout_s=" + table.InviteTimeoutSeconds
                    + " ready_check_timeout_s=" + table.ReadyCheckTimeoutSeconds
                    + " leader_succession=" + table.LeaderSuccession
                    + " loot=" + table.LootRule.Mode + "/" + table.LootRule.Roll
                    + " roll_range=" + table.LootRule.RollMin + ".." + table.LootRule.RollMax
                    + " level_range=" + (table.Levels.Enabled ? "on" : "off")
                    + "/" + table.Levels.MaxGap
                    + " pending=" + (table.Pending ? "true" : "false"));
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

        // --party-state：不连库，把队伍状态机一步步跑完并逐行打 party_step=。
        // 超时用例用 --at 推时钟，不真的等一分钟；坏表同样在这一步起不来。
        if (partyState)
        {
            try
            {
                var loaded = SocialLoader.LoadAll(
                    configPath, sectTablePath, calendarTablePath, sectTaskTablePath, partyTablePath);
                if (loaded.Party is null)
                {
                    Console.WriteLine("party=missing（可选表：缺 party.json，队伍 RPC 回 party_unavailable）");
                    return 0;
                }

                return PartyProbe.Run(loaded.Party, sectTaskAt ?? DateTimeOffset.UtcNow,
                    line => Console.WriteLine(line));
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
        }

        // --sect-config：不连库，逐档打印配置正本。坏配置在这一步就起不来
        // （config_error= + 非 0 退出），与真实启动共用同一份 Load + 同一组参数。
        if (sectConfig)
        {
            try
            {
                var loaded = SocialLoader.LoadAll(configPath, sectTablePath, calendarTablePath, sectTaskTablePath);
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
                var loaded = SocialLoader.LoadAll(configPath, sectTablePath, calendarTablePath, sectTaskTablePath);
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
                var loaded = SocialLoader.LoadAll(configPath, sectTablePath, calendarTablePath, sectTaskTablePath);
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
                var loaded = SocialLoader.LoadAll(configPath, sectTablePath, calendarTablePath, sectTaskTablePath);
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

        // --sect-task-config：不连库，逐条打印任务表与当前日历给的两个周期键。
        // 坏表在这一步就起不来（config_error= + 非 0 退出），与真实启动共用同一份 Load；
        // --at 可把时间点固定住，验业务日 / 周首随 config/calendar.json 变。
        if (sectTaskConfig)
        {
            try
            {
                var loaded = SocialLoader.LoadAll(
                    configPath, sectTablePath, calendarTablePath, sectTaskTablePath);
                var now = sectTaskAt ?? DateTimeOffset.UtcNow;
                Console.WriteLine("sect_task_config_state=ok version=" + loaded.Tasks.Version
                    + " pending=" + (loaded.Tasks.Pending ? "true" : "false")
                    + " tasks=" + string.Join(",", loaded.Tasks.Tasks.Select(
                        t => t.Id + ":" + t.Period + ":" + t.MaxPerPeriod
                            + ":level" + t.RequireLevel + ":" + t.Contribution + "/" + t.Construction)));
                Console.WriteLine("sect_task_period=day:" + loaded.Calendar.DayKey(now).ToString("yyyy-MM-dd")
                    + " week:" + loaded.Calendar.WeekStartDate(now).ToString("yyyy-MM-dd")
                    + " timezone:" + loaded.Calendar.TimeZoneId
                    + " daily_reset_hour:" + loaded.Calendar.DailyResetHour);
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

        return null;
    }
}

