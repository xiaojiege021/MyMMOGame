using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// B2：品阶与套装的只读入口。单独一个文件是因为 D1a 给每个 Cli 文件定了 600 行天花板，
/// CliParser 与 ConfigTableCli 都已贴近上限（同 CraftCli / NpcShopCli 的拆法）。
/// 三个入口都不连库，走与起服同一个 Load（含物品表逐件对账）。
/// </summary>
internal static class GradeSetCli
{
    /// <summary>--grade-list / --set-list / --set-bonus &lt;套编号&gt; &lt;件数&gt;</summary>
    public static void Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--grade-list": gradeTableState = true; break;
            case "--set-list": setTableState = true; break;
            case "--set-bonus" when i + 2 < args.Length:
                setBonusSetId = args[++i];
                setBonusPieces = int.Parse(args[++i]);
                break;
        }
    }

    /// <summary>不是本文件的入口就返回 null，交给下一个只读段。</summary>
    public static int? TryReadOnly()
    {
        if (setBonusSetId is not null)
        {
            return SetBonus();
        }

        if (!gradeTableState && !setTableState)
        {
            return null;
        }

        try
        {
            var (grades, items, sets) = LoadAll();

            if (gradeTableState)
            {
                Console.WriteLine("grade_table_state=ok version=" + grades.Version
                    + " grades=" + grades.Ordered.Count);
                foreach (var grade in grades.Ordered)
                {
                    Console.WriteLine("grade=" + grade.Id
                        + " order=" + grade.Order
                        + " require_level=" + grade.RequireLevel
                        + " base_attack=" + grade.BaseAttack
                        + " items=" + items.All.Count(
                            i => i.IsEquipment && string.Equals(i.Grade, grade.Id, StringComparison.Ordinal))
                        + " sets=" + sets.All.Count(
                            s => string.Equals(s.Grade, grade.Id, StringComparison.Ordinal)));
                }
            }

            if (setTableState)
            {
                Console.WriteLine("set_table_state=ok version=" + sets.Version
                    + " sets=" + sets.All.Count);
                foreach (var set in sets.All
                             .OrderBy(x => x.Grade, StringComparer.Ordinal)
                             .ThenBy(x => x.Id, StringComparer.Ordinal))
                {
                    var pieces = items.All.Count(i => string.Equals(i.SetId, set.Id, StringComparison.Ordinal));
                    Console.WriteLine("set=" + set.Id
                        + " name=" + set.Name
                        + " grade=" + set.Grade
                        + " pieces=" + pieces
                        + " bonuses=" + string.Join("/", set.Bonuses.Select(b => b.Pieces + ":" + b.Attack)));
                }
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

    static int SetBonus()
    {
        try
        {
            var (_, items, sets) = LoadAll();
            var def = sets.Find(setBonusSetId!)
                ?? throw new ConfigException("没有这套：" + setBonusSetId);
            var pieces = items.All.Count(i => string.Equals(i.SetId, def.Id, StringComparison.Ordinal));
            Console.WriteLine("set_bonus=id=" + def.Id
                + " pieces=" + setBonusPieces
                + " attack=" + def.BonusFor(setBonusPieces)
                + " max_pieces=" + pieces
                + " thresholds=" + string.Join("/", def.Bonuses.Select(b => b.Pieces + ":" + b.Attack)));
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        return 0;
    }

    static (GradeTable Grades, ItemTable Items, SetTable Sets) LoadAll()
    {
        var grades = GradeTable.Load("config/grades.json");
        var items = ItemTable.Load("config/items.json");
        grades.CheckItems(items);
        return (grades, items, SetTable.Load("config/sets.json", items, grades));
    }
}
