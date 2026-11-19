using Npgsql;

using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>B9 穿戴的连库核对：穿、脱、读已穿（部位 / 格号 / 物品 / 装备固定攻击之和）。</summary>
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
        var equips = new EquipRepository(ctx.DataSource);

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
            await using var connection = await ctx.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "select ce.slot, i.slot, i.item_id from \"角色\".character_equip ce "
                + "join \"角色\".inventory i on i.id = ce.inventory_id "
                + "where ce.character_id = $1 order by ce.slot", connection);
            command.Parameters.AddWithValue(equipRowOf);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            var attack = 0L;
            while (await reader.ReadAsync())
            {
                var itemId = reader.GetString(2);
                attack += items.Find(itemId)?.Attack ?? 0;
                rows.Add(reader.GetString(0) + ":" + reader.GetInt16(1) + ":" + itemId);
            }

            Console.WriteLine("equip_row=ok worn=" + (rows.Count == 0 ? "-" : string.Join(",", rows))
                + " attack=" + attack);
        }

        return 0;
    }

    /// <summary>空串在日志里会吞掉后面的字段，统一写 none。</summary>
    static string Stamp(string value) => value.Length == 0 ? "none" : value;
}
