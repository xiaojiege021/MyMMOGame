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
        using IReviveScrollUse scrolls = string.IsNullOrEmpty(config.CharacterUpstream)
            ? new NoReviveScrollUse()
            : new GrpcReviveScrollUse(config.CharacterUpstream);
        // P2-3b：战场要按装备算属性，装备只在角色库里。没配上游就是本机口径：
        // 战场一律拒（battle_profile_missing），不从别处编一份属性出来。
        using IBattleProfileSource profiles = string.IsNullOrEmpty(config.CharacterUpstream)
            ? new NoBattleProfileSource()
            : new GrpcBattleProfileSource(config.CharacterUpstream);

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
        Console.WriteLine($"classes={config.Classes.All.Count} class_table_version={config.Classes.Version} default_class={config.DefaultClass}");
        // 副本表的难度档与人数上下限打出来：高难本至少 2 人，普通本 min_players 是 1。
        Console.WriteLine($"dungeons={config.Dungeons.All.Count} dungeon_table_version={config.Dungeons.Version}");
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
        // 场次全在内存里，进程一停就没了。重启不恢复、不补发，这句是给核对脚本看的。
        Console.WriteLine("resume=off");
        Console.WriteLine("ready name=Battle pid=" + Environment.ProcessId);

        try
        {
            await new BattleServer(config, settlements, characters, expSettlement, talismans, scrolls, profiles)
                .RunAsync(stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Battle pid=" + Environment.ProcessId);
        return 0;
    }
}
