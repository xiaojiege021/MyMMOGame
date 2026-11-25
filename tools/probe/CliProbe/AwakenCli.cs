using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// B6：觉醒的核对入口。
///   · 只读不连库：--awaken-config（表现值）、--awaken-table-path（指到临时表试坏配置）。
///   · 连库：--awaken &lt;角色号&gt; &lt;分支&gt;、--awaken-state &lt;角色号&gt;。
/// 走的是起服同一套 AwakenTable.Load 与同一个 CharacterRepository.AwakenAsync，
/// 探针不另写一份门槛或分支映射。
/// </summary>
internal static class AwakenCli
{
    public static bool Handles(string flag) => flag is
        "--awaken-config" or "--awaken-table-path" or "--awaken" or "--awaken-state";

    public static int Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--awaken-config":
                awakenConfig = true;
                return 0;
            case "--awaken-table-path" when i + 1 < args.Length:
                awakenTablePath = args[++i];
                return 0;
            case "--awaken" when i + 2 < args.Length:
                long.TryParse(args[++i], out awakenOf);
                awakenBranch = args[++i];
                return 0;
            case "--awaken-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out awakenStateOf);
                return 0;
            default:
                return 64;
        }
    }

    public static int? TryReadOnly()
    {
        if (!awakenConfig && awakenTablePath is null)
        {
            return null;
        }

        try
        {
            var cfg = CharacterConfig.Load(configPath);
            var table = LoadTable(cfg);
            Console.WriteLine("awaken_config=ok"
                + " version=" + table.Version
                + " pending=" + (table.Pending ? "true" : "false")
                + " min_level=" + table.MinLevel
                + " gold_cost=" + table.GoldCost
                + " branches=" + table.BranchSkills.Count);
            foreach (var row in table.BranchSkills)
            {
                Console.WriteLine("awaken_row=branch=" + row.BranchId
                    + " class=" + row.ClassId
                    + " skill=" + row.SkillId);
            }
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

    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        if (awakenOf == 0 && awakenStateOf == 0)
        {
            return null;
        }

        if (awakenOf != 0)
        {
            var cfg = CharacterConfig.Load(configPath);
            var table = LoadTable(cfg);
            var outcome = await ctx.Characters.AwakenAsync(
                awakenOf, (awakenBranch ?? "").Trim(), table, CancellationToken.None);
            Console.WriteLine("awaken=" + (outcome.Reason.Length == 0 ? "ok" : "reject")
                + " reason=" + Stamp(outcome.Reason)
                + " branch=" + Stamp(outcome.Branch)
                + " skill=" + outcome.SkillId
                + " level=" + outcome.Level
                + " duplicate=" + (outcome.Duplicate ? "true" : "false"));
        }

        if (awakenStateOf != 0)
        {
            var state = await ctx.Characters.AwakenStateAsync(awakenStateOf, CancellationToken.None);
            if (state is null)
            {
                Console.WriteLine("awaken_state=missing");
            }
            else
            {
                Console.WriteLine("awaken_state=ok"
                    + " branch=" + Stamp(state.Value.Branch)
                    + " skill=" + state.Value.SkillId
                    + " level=" + state.Value.Level
                    + " awaken_at_ms=" + (state.Value.AwakenAt is { } at
                        ? at.ToUnixTimeMilliseconds()
                        : 0));
            }
        }

        return 0;
    }

    /// <summary>与起服同一条链：分支表 → 经验表（满级门槛）→ 觉醒表。</summary>
    static AwakenTable LoadTable(CharacterConfig cfg)
    {
        var branches = BranchTable.Load("config/branches.json");
        var exp = ExpTable.Load(cfg.ExpTable);
        return AwakenTable.Load(awakenTablePath ?? cfg.AwakenTable, branches, exp);
    }

    /// <summary>空串在日志里会吞掉后面的字段，统一写 none。</summary>
    static string Stamp(string value) => value.Length == 0 ? "none" : value;
}
