using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// B1 丹药的核对入口：
///   --pill-list                 只读，不连库：六种丹的池 / 回复值 / 冷却组 / 档位，
///                               以及符表里的两个上限（两份文件互校过的值）
///   --pill-table-path &lt;path&gt;   指到临时丹药表试坏配置
///   --pill-state &lt;角色号&gt;       连库只读：这个角色背包里六种丹各几颗
/// 与真实启动共用同一套 PillTable.Load：坏配置在只读入口就能看出来。
/// </summary>
internal static class PillCli
{
    /// <summary>这几个 flag 归本类解析（CliParser 一行分发）。</summary>
    public static bool Handles(string flag) => flag
        is "--pill-list" or "--pill-table-path" or "--pill-state"
        or "--pill-spend" or "--pill-txn";

    public static int Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--pill-list":
                pillList = true;
                return 0;
            case "--pill-table-path" when i + 1 < args.Length:
                pillList = true;
                pillTablePath = args[++i];
                return 0;
            case "--pill-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out pillStateOf);
                return 0;
            // 扣一颗丹：角色号 + 编号 + 事务号。与 ConsumePill 调的是同一个仓储。
            case "--pill-spend" when i + 2 < args.Length:
                long.TryParse(args[++i], out pillSpendOf);
                pillSpendItem = args[++i];
                // 事务号可以跟在后面（--pill-spend 角色 编号 事务号），
                // 也可以用 --pill-txn 单独给；两种写法都只认这一个号。
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    pillSpendTxn = args[++i];
                }

                return 0;
            case "--pill-txn" when i + 1 < args.Length:
                pillSpendTxn = args[++i];
                return 0;
            default:
                return 64;
        }
    }

    public static int? TryReadOnly()
    {
        if (!pillList)
        {
            return null;
        }

        try
        {
            var config = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var talismans = TalismanTable.Load(config.TalismanTable, items);
            var currencies = CurrencyTable.Load("config/currency.json");
            var dungeons = DungeonTable.Load(config.DungeonTable);
            var realms = RealmTable.Load(config.RealmTable, dungeons, currencies);
            var table = PillTable.Load(
                pillTablePath ?? config.PillTable, items, talismans, realms);

            Console.WriteLine("pill_list_state=ok version=" + table.Version
                + " pills=" + table.All.Count
                + " hp_top=" + table.Strongest(PillTable.KindHp)
                + " mana_top=" + table.Strongest(PillTable.KindMana)
                + " talisman_hp_cap=" + (talismans.RedTalisman?.PotionHpRestore ?? 0)
                + " talisman_mana_cap=" + (talismans.BlueTalisman?.PotionManaRestore ?? 0));
            foreach (var pill in table.All)
            {
                Console.WriteLine("pill=" + pill.ItemId
                    + " name=" + pill.Name
                    + " kind=" + pill.Kind
                    + " tier=" + pill.Tier
                    + " hp_restore=" + pill.HpRestore
                    + " mana_restore=" + pill.ManaRestore
                    + " cooldown_group=" + (pill.CooldownGroup.Length == 0 ? "-" : pill.CooldownGroup)
                    + " cooldown_ms=" + pill.CooldownMs);
            }

            foreach (var requirement in realms.Requirements)
            {
                Console.WriteLine("realm_pill=" + requirement.From + "->" + requirement.Next
                    + " pill=" + (requirement.Pill.Length == 0 ? "-" : requirement.Pill)
                    + " count=" + requirement.PillCount);
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
        if (pillSpendOf != 0)
        {
            if (string.IsNullOrWhiteSpace(pillSpendItem) || string.IsNullOrWhiteSpace(pillSpendTxn))
            {
                Console.WriteLine("pill_spend_error=item_missing_or_txn_missing");
                return 2;
            }

            var repo = new PillRepository(ctx.DataSource);
            var spend = await repo.SpendAsync(
                pillSpendOf, pillSpendItem!, pillSpendTxn!, CancellationToken.None);
            Console.WriteLine("pill_spend_ok=" + (spend.Ok ? "true" : "false")
                + " reason=" + spend.Reason
                + " item=" + spend.ItemId
                + " count_left=" + spend.CountLeft
                + " slot=" + spend.Slot
                + " duplicate=" + (spend.Duplicate ? "true" : "false"));
            return 0;
        }

        if (pillStateOf == 0)
        {
            return null;
        }

        try
        {
            var config = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var talismans = TalismanTable.Load(config.TalismanTable, items);
            var currencies = CurrencyTable.Load("config/currency.json");
            var dungeons = DungeonTable.Load(config.DungeonTable);
            var realms = RealmTable.Load(config.RealmTable, dungeons, currencies);
            var table = PillTable.Load(config.PillTable, items, talismans, realms);
            var pills = new PillRepository(ctx.DataSource);

            var parts = new List<string>();
            foreach (var pill in table.All)
            {
                var left = await pills.CountAsync(pillStateOf, pill.ItemId, CancellationToken.None);
                parts.Add(pill.ItemId + "=" + left);
            }

            Console.WriteLine("pill_state=ok id=" + pillStateOf
                + " pills=" + string.Join(" ", parts));
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        return 0;
    }
}
