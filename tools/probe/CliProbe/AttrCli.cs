using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// A9：属性加点的核对入口。
///   · 只读不连库：--attr-points-config（表现值）、--attr-point-table-path（指到临时表试坏配置）。
///   · 连库：--attr-points-state &lt;角色号&gt;、--allocate-attr &lt;角色号&gt; &lt;事务号&gt; &lt;属性&gt; &lt;点数&gt;、
///     --raw-attr-points-write &lt;角色号&gt; &lt;点数&gt;（只给核对用，直写剩余点数验加点门槛）。
/// 与真实启动共用 AttrPointTable.Load 与同一个 AttrPointRepository，探针不另写一份判定。
/// </summary>
internal static class AttrCli
{
    public static bool Handles(string flag) => flag is
        "--attr-points-config" or "--attr-point-table-path" or "--attr-points-state"
        or "--allocate-attr" or "--raw-attr-points-write" or "--item-table-path";

    public static int Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--attr-points-config":
                attrPointsConfig = true;
                return 0;
            case "--attr-point-table-path" when i + 1 < args.Length:
                attrPointTablePath = args[++i];
                return 0;
            // A9：物品表指到临时表，试坏 require_attr 与临时门槛；--item-list / --can-equip 共用。
            case "--item-table-path" when i + 1 < args.Length:
                itemTablePath = args[++i];
                return 0;
            case "--attr-points-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out attrPointsStateOf);
                return 0;
            case "--allocate-attr" when i + 3 < args.Length:
                long.TryParse(args[++i], out allocateAttrOf);
                allocateAttrTxn = args[++i];
                allocateAttrKey = args[++i];
                int.TryParse(args[++i], out allocateAttrPoints);
                return 0;
            case "--raw-attr-points-write" when i + 2 < args.Length:
                long.TryParse(args[++i], out rawAttrPointsOf);
                int.TryParse(args[++i], out rawAttrPointsValue);
                return 0;
            default:
                return 64;
        }
    }

    public static int? TryReadOnly()
    {
        if (!attrPointsConfig && attrPointTablePath is null)
        {
            return null;
        }

        try
        {
            var cfg = CharacterConfig.Load(configPath);
            var path = attrPointTablePath ?? cfg.AttrPointTable;
            var table = AttrPointTable.Load(path);
            Console.WriteLine("attr_points_config=ok"
                + " path=" + path
                + " version=" + table.Version
                + " pending=" + (table.Pending ? "true" : "false")
                + " per_attr_cap=" + table.PerAttrCap
                + " free_respec=" + (table.FreeRespec ? "true" : "false")
                + " attributes=" + string.Join(",", AttrPointTable.Attributes));
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
        if (attrPointsStateOf == 0 && allocateAttrOf == 0 && rawAttrPointsOf == 0)
        {
            return null;
        }

        var cfg = CharacterConfig.Load(configPath);
        var table = AttrPointTable.Load(cfg.AttrPointTable);
        var repository = new AttrPointRepository(ctx.DataSource, table);

        if (rawAttrPointsOf != 0)
        {
            await repository.RawWritePointsAsync(
                rawAttrPointsOf, rawAttrPointsValue, CancellationToken.None);
            Console.WriteLine("raw_attr_points=written id=" + rawAttrPointsOf
                + " points=" + rawAttrPointsValue);
        }

        if (allocateAttrOf != 0)
        {
            var outcome = await repository.AllocateAsync(
                allocateAttrOf, null, allocateAttrTxn ?? "",
                allocateAttrKey ?? "", allocateAttrPoints, CancellationToken.None);
            Console.WriteLine("allocate_attr=" + (outcome.Reason.Length == 0 ? "ok" : "reject")
                + " reason=" + (outcome.Reason.Length == 0 ? "none" : outcome.Reason)
                + " duplicate=" + (outcome.Duplicate ? "true" : "false")
                + " id=" + allocateAttrOf
                + " attribute=" + (allocateAttrKey ?? "")
                + " points=" + allocateAttrPoints
                + " class=" + outcome.ClassId
                + " level=" + outcome.Level
                + " attr_points=" + outcome.Remaining
                + " strength=" + outcome.Allocated.Strength
                + " agility=" + outcome.Allocated.Agility
                + " spirit=" + outcome.Allocated.Spirit
                + " vitality=" + outcome.Allocated.Vitality);
        }

        if (attrPointsStateOf != 0)
        {
            var snapshot = await repository.DescribeAsync(attrPointsStateOf, CancellationToken.None);
            if (!snapshot.Found)
            {
                Console.WriteLine("attr_points_state=missing id=" + attrPointsStateOf);
            }
            else
            {
                Console.WriteLine("attr_points_state=ok id=" + attrPointsStateOf
                    + " class=" + snapshot.ClassId
                    + " level=" + snapshot.Level
                    + " attr_points=" + snapshot.Remaining
                    + " strength=" + snapshot.Allocated.Strength
                    + " agility=" + snapshot.Allocated.Agility
                    + " spirit=" + snapshot.Allocated.Spirit
                    + " vitality=" + snapshot.Allocated.Vitality);
            }
        }

        return 0;
    }
}
