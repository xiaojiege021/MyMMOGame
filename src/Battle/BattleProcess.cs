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

        Console.WriteLine("config=" + config.SourcePath);
        Console.WriteLine("settlement_dir=" + settlements.Dir);
        Console.WriteLine("settlements=" + settlements.Count());
        Console.WriteLine($"skills={config.Skills.All.Count} skill_table_version={config.Skills.Version}");
        Console.WriteLine($"caster={config.Caster.Id} hp_max={config.Caster.HpMax}");
        Console.WriteLine($"boss={config.Boss.Id} hp_max={config.Boss.HpMax} exp_on_clear={config.Boss.ExpOnClear}");
        // 场次全在内存里，进程一停就没了。重启不恢复、不补发，这句是给核对脚本看的。
        Console.WriteLine("resume=off");
        Console.WriteLine("ready name=Battle pid=" + Environment.ProcessId);

        try
        {
            await new BattleServer(config, settlements).RunAsync(stopping);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopped name=Battle pid=" + Environment.ProcessId);
        return 0;
    }
}
