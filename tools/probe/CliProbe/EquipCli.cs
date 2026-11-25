using Npgsql;

using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>B9 / B10 穿戴的连库核对：穿、脱、读已穿（部位 / 格号 / 物品 / 攻击 / 强化 / 结晶）。</summary>
internal static class EquipCli
{
    /// <summary>B9 的三个核对 flag 归本类解析（CliParser 一行分发，行数闸门仍 ≤600）。</summary>
    public static bool Handles(string flag) => flag is "--equip" or "--unequip" or "--equip-row";

    public static int Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--equip" when i + 2 < args.Length:
                long.TryParse(args[++i], out equipOf);
                int.TryParse(args[++i], out equipBagSlot);
                return 0;
            case "--unequip" when i + 2 < args.Length:
                long.TryParse(args[++i], out unequipOf);
                unequipSlot = args[++i];
                return 0;
            case "--equip-row" when i + 1 < args.Length:
                long.TryParse(args[++i], out equipRowOf);
                return 0;
            default:
                return 64;
        }
    }

    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        if (equipOf == 0 && unequipOf == 0 && equipRowOf == 0)
        {
            return null;
        }

        var items = ItemTable.Load("config/items.json");
        var branches = BranchTable.Load("config/branches.json");
        // A9：属性门槛要读加点，仓储与真实启动同一套依赖（职业表 + 加点仓储）。
        var classes = new ClassIds("config/classes.json");
        var attrTable = AttrPointTable.Load(CharacterConfig.Load(configPath).AttrPointTable);
        var attrPoints = new AttrPointRepository(ctx.DataSource, attrTable);
        var equips = new EquipRepository(ctx.DataSource, classes, attrPoints);

        if (equipOf != 0)
        {
            var outcome = await equips.EquipAsync(
                equipOf, null, equipBagSlot, items, branches, CancellationToken.None);
            Console.WriteLine("equip=" + (outcome.Reason.Length == 0 ? "ok" : "reject")
                + " reason=" + Stamp(outcome.Reason)
                + " item=" + Stamp(outcome.ItemId)
                + " worn=" + Stamp(outcome.Slot)
                + " attack=" + outcome.Attack
                + " level=" + outcome.Level
                + " class=" + Stamp(outcome.ClassId)
                + " bag_slot=" + outcome.BagSlot);
        }

        if (unequipOf != 0)
        {
            var outcome = await equips.UnequipAsync(
                unequipOf, null, unequipSlot ?? "", items, CancellationToken.None);
            Console.WriteLine("unequip=" + (outcome.Reason.Length == 0 ? "ok" : "reject")
                + " reason=" + Stamp(outcome.Reason)
                + " item=" + Stamp(outcome.ItemId)
                + " worn=" + Stamp(outcome.Slot)
                + " bag_slot=" + outcome.BagSlot
                + " attack=" + outcome.Attack);
        }

        if (equipRowOf != 0)
        {
            // B10：同一次连库顺手给「已穿装备」的三样事实——攻击、强化等级之和、攻击结晶固定值之和。
            // 三项都由 WornTotalsAsync 从库里现取，探针不自己算第二套；强化系数按 enhance.json 折算，
            // 与战斗快照回包同一个公式。原有 worn= / attack= 两段不删，老断言照旧解析。
            var currencies = CurrencyTable.Load("config/currency.json");
            var enhance = EnhanceTable.Load("config/enhance.json", items, currencies);
            var crystals = CrystalTable.Load("config/crystal.json", items);
            // B2：套装加成与起服同一套算法（SetTable 只查门槛，件数从库里现数）。
            var grades = GradeTable.Load("config/grades.json");
            var sets = SetTable.Load("config/sets.json", items, grades);
            var worn = await equips.WornTotalsAsync(
                equipRowOf, items, crystals, sets, CancellationToken.None);

            await using var connection = await ctx.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "select ce.slot, i.slot, i.item_id from \"角色\".character_equip ce "
                + "join \"角色\".inventory i on i.id = ce.inventory_id "
                + "where ce.character_id = $1 order by ce.slot", connection);
            command.Parameters.AddWithValue(equipRowOf);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetString(0) + ":" + reader.GetInt16(1) + ":" + reader.GetString(2));
            }

            Console.WriteLine("equip_row=ok worn=" + (rows.Count == 0 ? "-" : string.Join(",", rows))
                + " attack=" + worn.Attack
                + " enhance=" + worn.EnhanceTotal
                + " enhance_coef_bps=" + enhance.CoefBpsFor(worn.EnhanceTotal)
                + " crystal_flat=" + worn.CrystalFlatAttack
                + " set_bonus=" + worn.SetBonus
                + " attack_total=" + worn.TotalAttack);
        }

        return 0;
    }

    /// <summary>空串在日志里会吞掉后面的字段，统一写 none。</summary>
    static string Stamp(string value) => value.Length == 0 ? "none" : value;
}
