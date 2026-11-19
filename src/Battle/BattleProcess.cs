namespace Battle;

public sealed class BattleProcess(string configPath)
{
    public async Task<int> RunAsync(CancellationToken stopping)
    {
        BattleConfig config;
        try
        {
            config = BattleConfig.Load(configPath);
        }
        catch (ConfigException ex)
        {
            // 配置不对就起不来，不带着半截配置跑。
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        var settlements = new SettlementStore(config.SettlementDir);
        Directory.CreateDirectory(settlements.Dir);
        var characters = new FileCharacterStore(config.CharacterDir);
        // 没配上游就是 P0 的本机结算口径；配了才把这一笔经验交给角色服。
        using IExpSettlement expSettlement = string.IsNullOrEmpty(config.ExpUpstream)
            ? new NoExpSettlement()
            : new GrpcExpSettlement(config.ExpUpstream);
        // 红符要从背包里扣一张，背包只有角色服能写。没配上游就是本机口径：
        // 这一场用不了红符（回 upstream_off），不从别的地方变出一张来。
        using ITalismanSpend talismans = string.IsNullOrEmpty(config.CharacterUpstream)
            ? new NoTalismanSpend()
            : new GrpcTalismanSpend(config.CharacterUpstream);
        // P1-5c6：卷轴的背包、当前级经验与全局冷却都在角色服；没配上游就一律拒绝，
        // 不在本进程里造一张卷轴或记一份「这个人的冷却」。
        // B1：丹和符同一条切法——背包在角色服，没配上游就一律拒绝，
        // 不在本进程里造一颗丹，也不假装备份回复量。
        using IPillSpend pills = string.IsNullOrEmpty(config.CharacterUpstream)
            ? new NoPillSpend()
            : new GrpcPillSpend(config.CharacterUpstream);
        using IReviveScrollUse scrolls = string.IsNullOrEmpty(config.CharacterUpstream)
            ? new NoReviveScrollUse()
            : new GrpcReviveScrollUse(config.CharacterUpstream);
        // P2-3b：战场要按装备算属性，装备只在角色库里。没配上游就是本机口径：
        // 战场一律拒（battle_profile_missing），不从别处编一份属性出来。
        using IBattleProfileSource profiles = string.IsNullOrEmpty(config.CharacterUpstream)
            ? new NoBattleProfileSource()
            : new GrpcBattleProfileSource(config.CharacterUpstream);
        // P3-3a3：城战开场要读社交服的宗门科技（开发架构 4.4 / 8 / 9 节）。
        // 没配 social_upstream 就不查，科技恒 0；上游不可达也由客户端降级为 0，
        // 不让「社交服没起」拖垮练习场 / 副本 / 竞技场 / 战场 / 城战老链路。
        using ISectTechSource sectTech = string.IsNullOrEmpty(config.SocialUpstream)
            ? new NoSectTechSource()
            : new GrpcSectTechSource(config.SocialUpstream);
        // P3-3a4：胜方宗门只从社交服只读查，城池写回只经角色服（持久数据的唯一写入）。
        // 没配上游就不启用；老卡临时配置没有领地表时本来也不会判胜负、不会调这两个。
        using ISiegeSectSource siegeSect = string.IsNullOrEmpty(config.SocialUpstream)
            ? new NoSiegeSectSource()
            : new GrpcSiegeSectSource(config.SocialUpstream);
        using ISiegeResultSink siegeResult = string.IsNullOrEmpty(config.CharacterUpstream)
            ? new NoSiegeResultSink()
            : new GrpcSiegeResultSink(config.CharacterUpstream);
        // P3-5a：入口在世界服，这一场打完只把事实值交回去（打掉没有、伤害最高的宗门）。
        // 没配 world_upstream 就不交：技能与经验照常结算，只是入口上不会记名字。
        using IWorldBossSink worldBoss = string.IsNullOrEmpty(config.WorldUpstream)
            ? new NoWorldBossSink()
            : new GrpcWorldBossSink(config.WorldUpstream);
        // Q9：逃跑惩罚的事实值落角色库（持久数据的唯一写入方）。没配角色服就是本机
        // 口径：惩罚只活在战斗服内存里，进程重启就没了——不编一个跨进程的假事实。
        using IDeserterPenalty deserter = string.IsNullOrEmpty(config.CharacterUpstream)
            ? new NoDeserterPenalty()
            : new GrpcDeserterPenalty(config.CharacterUpstream);

        Console.WriteLine("config=" + config.SourcePath);
        Console.WriteLine("exp_upstream=" + (config.ExpUpstream.Length == 0 ? "off" : config.ExpUpstream));
        Console.WriteLine("character_upstream="
            + (config.CharacterUpstream.Length == 0 ? "off" : config.CharacterUpstream));
        Console.WriteLine("revive_scroll_upstream="
            + (config.CharacterUpstream.Length == 0 ? "off" : "on"));
        Console.WriteLine("settlement_dir=" + settlements.Dir);
        Console.WriteLine("settlements=" + settlements.Count());
        Console.WriteLine("character_dir=" + characters.Dir);
        Console.WriteLine($"skills={config.Skills.All.Count} skill_table_version={config.Skills.Version}");
        // Q2：效果表在不在打出来。缺表时伤害走恒等、不施加效果——老卡临时配置目录就是这个口径。
        Console.WriteLine("effects="
            + (config.Effects.IsMissing
                ? "missing"
                : $"ok version={config.Effects.Version} effects={config.Effects.All.Count}"
                  + $" pending={(config.Effects.Pending ? "true" : "false")}"));
        Console.WriteLine($"classes={config.Classes.All.Count} class_table_version={config.Classes.Version} default_class={config.DefaultClass}");
        Console.WriteLine(config.Monsters is null
            ? "monster_table=missing"
            : $"monster_table=ok version={config.Monsters.Version} monsters={config.Monsters.All.Count}");
        // 副本表的难度档与人数上下限打出来：高难本至少 2 人，普通本 min_players 是 1。
        Console.WriteLine($"dungeons={config.Dungeons.All.Count} dungeon_table_version={config.Dungeons.Version}");
        Console.WriteLine("dungeon_drops="
            + (config.DungeonDrops.IsMissing ? "missing" : "ok")
            + " version=" + config.DungeonDrops.Version);
        foreach (var dungeon in config.Dungeons.All)
        {
            Console.WriteLine($"dungeon={dungeon.Id} tier={dungeon.Tier}"
                + $" min_players={dungeon.MinPlayers} max_players={dungeon.MaxPlayers}");
        }
        Console.WriteLine($"boss={config.Boss.Id} hp_max={config.Boss.HpMax} exp_on_clear={config.Boss.ExpOnClear}"
            + (config.Boss.Phases is { Count: > 0 } phases ? $" phases={phases.Count}" : ""));
        // 竞技场：模式与模板数打出来，核对脚本按这几行验「模板只有职业与分支这一层」。
        Console.WriteLine($"arena_match_table={config.ArenaMatchTablePath}"
            + (config.ArenaMatch.IsMissing
                ? " arena_table=missing"
                : $" arena_table_version={config.ArenaMatch.Version}"
                  + $" arena_modes={config.ArenaMatch.Modes.Count}"
                  + $" arena_templates={config.ArenaMatch.Templates.Count}"));
        foreach (var mode in config.ArenaMatch.Modes)
        {
            Console.WriteLine($"arena_mode={mode.Id} name={mode.Name}"
                + $" team_size={mode.TeamSize} teams={mode.Teams} players={mode.Players}");
        }

        // 战场：10 对 10、同队 1.3 倍、每级强化折算与战力权重都在 arena 之外的同一份表。
        Console.WriteLine($"battlefield_table={config.BattlefieldTablePath}"
            + (config.Battlefield.IsMissing
                ? " battlefield=missing"
                : $" battlefield_version={config.Battlefield.Version}"
                  + $" battlefield_mode={config.Battlefield.ModeId}"
                  + $" team_size={config.Battlefield.TeamSize}"
                  + $" teams={config.Battlefield.Teams}"
                  + $" players={config.Battlefield.Players}"
                  + $" ratio={config.Battlefield.Ratio}"
                  + $" fill_timeout_ms={config.Battlefield.FillTimeoutMs}"));
        // 城战：40 对 40、装备全额生效。键名都带 siege_ 前缀，免得与上面战场那组
        // team_size / players 混在一起，老核对脚本 grep 到的是各自那一行。
        Console.WriteLine($"siege_table={config.SiegeTablePath}"
            + (config.Siege.IsMissing
                ? " siege=missing"
                : $" siege_version={config.Siege.Version}"
                  + $" siege_mode={config.Siege.ModeId}"
                  + $" siege_team_size={config.Siege.TeamSize}"
                  + $" siege_teams={config.Siege.Teams}"
                  + $" siege_players={config.Siege.Players}"
                  + $" balance=none"
                  + $" siege_fill_timeout_ms={config.Siege.FillTimeoutMs}"
                  + $" siege_view_limit={config.Siege.ViewFullModelLimit}"));
        // 场次全在内存里，进程一停就没了。重启不恢复、不补发，这句是给核对脚本看的。
        // 宗门科技表与社交服地址单独一行，免得混进 siege_table= 那行的老断言里。
        Console.WriteLine($"sect_tech_table={config.SectTechTablePath}"
            + (config.SectTech.IsMissing
                ? " sect_tech=missing"
                : $" sect_tech_version={config.SectTech.Version}"
                  + $" sect_tech_pct={config.SectTech.PowerPctPerLevel}"
                  + $" sect_tech_max_level={config.SectTech.MaxLevel}")
            + $" social_upstream={(config.SocialUpstream.Length == 0 ? "off" : config.SocialUpstream)}");
        // 领地表单独一行，免得混进 siege_table= 那行的老断言里；missing 表示这一进程
        // 的城战不判胜负、不写回，老卡临时配置目录没有这张表时就是这个状态。
        Console.WriteLine($"siege_territory_table={config.SiegeTerritoryTablePath}"
            + (config.SiegeTerritory.IsMissing
                ? " siege_territory=missing"
                : $" siege_territory_version={config.SiegeTerritory.Version}"
                  + $" siege_territory_cities={config.SiegeTerritory.Cities.Count}"
                  + $" siege_territory_pending={(config.SiegeTerritory.Pending ? "true" : "false")}"));
        // 世界 Boss 表单独一行：entrance / cap / 窗口数 / 首领全从这里现取。
        // missing 表示这一进程没有入口，带 worldboss 的连接一律拒，别的模式照跑。
        Console.WriteLine($"world_boss_table={config.WorldBossTablePath}"
            + (config.WorldBoss.IsMissing
                ? " world_boss=missing"
                : $" world_boss_version={config.WorldBoss.Version}"
                  + $" world_boss_entrance={config.WorldBoss.EntranceId}"
                  + $" world_boss_region={config.WorldBoss.RegionId}"
                  + $" world_boss_cap={config.WorldBoss.Cap}"
                  + $" world_boss_windows={config.WorldBoss.Windows.Count}"
                  + $" world_boss_boss={config.WorldBoss.Boss.Id}"
                  + $" world_boss_pending={(config.WorldBoss.Pending ? "true" : "false")}")
            + $" world_upstream={(config.WorldUpstream.Length == 0 ? "off" : config.WorldUpstream)}");
        Console.WriteLine("resume=off");
        // Q9：重连表单独一行，三个数字全部现取（核对脚本不写死产品现值）；
        // missing 表示这一进程按老口径断线即离场，不重连也不记惩罚。
        Console.WriteLine($"reconnect_table={config.ReconnectTablePath}"
            + (config.Reconnect.IsMissing
                ? " reconnect=missing"
                : $" reconnect_version={config.Reconnect.Version}"
                  + $" battle_grace_ms={config.Reconnect.GraceMs}"
                  + $" reconnect_window_ms={config.Reconnect.WindowMs}"
                  + $" deserter_penalty_ms={config.Reconnect.PenaltyMs}"));
        Console.WriteLine("ready name=Battle pid=" + Environment.ProcessId);

        try
        {
            await new BattleServer(config, settlements, characters, expSettlement, talismans,
                pills, scrolls, profiles, sectTech, siegeSect, siegeResult, worldBoss, deserter)
                .RunAsync(stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Battle pid=" + Environment.ProcessId);
        return 0;
    }
}
