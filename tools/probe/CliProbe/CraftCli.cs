using Npgsql;

using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// K3 生产 / 配方的只读与连库核对。只读段把配方表打出来（与启动共用同一
/// RecipeTable.Load）；连库段直接调 CraftRepository 的两笔事务，用于 ubu 真库上的
/// 「扣材料 → 等 ready_at → 掷点 → 入包 / 失败」核对。业务入口仍在网关的 /craft/*。
/// </summary>
internal static class CraftCli
{
    /// <summary>
    /// CliParser 把 K3 的 flag 转到这个解析器：i 指向当前 flag，值参数由这里吃掉。
    /// 放在 CraftCli 里是为了守住 D1a 的「每个 Cli 文件 ≤600 行 / 一个文件一个类型」。
    /// </summary>
    public static void Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--recipe-list":
                recipeList = true;
                break;
            case "--recipe-table-path" when i + 1 < args.Length:
                recipeTablePath = args[++i];
                break;
            case "--craft-start" when i + 2 < args.Length:
                long.TryParse(args[++i], out craftStartOf);
                craftStartRecipe = args[++i];
                break;
            case "--craft-finish" when i + 1 < args.Length:
                long.TryParse(args[++i], out craftFinishOf);
                break;
            case "--craft-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out craftStateOf);
                break;
            case "--craft-txn" when i + 1 < args.Length:
                craftTxn = args[++i];
                break;
            case "--raw-item-write" when i + 4 < args.Length:
                long.TryParse(args[++i], out rawItemWriteId);
                int.TryParse(args[++i], out rawItemSlot);
                rawItemId = args[++i];
                int.TryParse(args[++i], out rawItemCount);
                break;
            // K3 造料：给货币加一笔（行不存在会建），与「只改已有行」的 raw 写分开。
            case "--raw-currency-add" when i + 3 < args.Length:
                long.TryParse(args[++i], out rawCurrencyAddId);
                rawCurrencyAddKind = args[++i];
                long.TryParse(args[++i], out rawCurrencyAddDelta);
                break;
        }
    }

    public static int? TryReadOnly()
    {
        if (!recipeList)
        {
            return null;
        }

        try
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var currencies = CurrencyTable.Load("config/currency.json");
            var table = RecipeTable.Load(
                recipeTablePath ?? characterConfig.RecipeTable, items, currencies);
            PrintTable(table);
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

    static void PrintTable(RecipeTable table)
    {
        Console.WriteLine("recipe_table_state=ok version=" + table.Version
            + " pending=" + (table.Pending ? "true" : "false")
            + " professions=" + table.Professions.Count
            + " recipes=" + table.Recipes.Count);

        foreach (var profession in table.Professions)
        {
            Console.WriteLine("recipe_profession=" + profession.Id
                + " name=" + profession.Name
                + " recipes=" + table.ForProfession(profession.Id).Count);
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var profession in table.Professions)
        {
            foreach (var recipe in table.ForProfession(profession.Id))
            {
                var inputs = string.Join("+", recipe.Inputs.Select(i => i.ItemId + ":" + i.Count));
                var fee = recipe.Fee.Count == 0
                    ? "none"
                    : string.Join("+", recipe.Fee.Select(f => f.Kind + ":" + f.Amount));
                Console.WriteLine("recipe=" + recipe.Id
                    + " profession=" + recipe.Profession
                    + " unlock=" + recipe.UnlockLevel
                    + " inputs=" + inputs
                    + " fee=" + fee
                    + " output=" + recipe.OutputItemId + ":" + recipe.OutputCount
                    + " success_pct=" + recipe.SuccessPct.ToString("0.##", culture)
                    + " craft_time_s=" + recipe.CraftTimeS);
            }
        }
    }

    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        if (rawItemWriteId == 0 && craftStartOf == 0 && craftFinishOf == 0 && craftStateOf == 0)
        {
            return null;
        }

        var dataSource = ctx.DataSource;
        var characterConfig = CharacterConfig.Load(configPath);
        var items = ItemTable.Load("config/items.json");

        if (rawItemWriteId != 0)
        {
            await RawItemWriteAsync(dataSource, items, characterConfig);
        }

        if (craftStateOf == 0 && craftStartOf == 0 && craftFinishOf == 0)
        {
            return 0;
        }

        var currencies = CurrencyTable.Load("config/currency.json");
        var table = RecipeTable.Load(
            recipeTablePath ?? characterConfig.RecipeTable, items, currencies);
        var grants = new GrantRepository(dataSource, items, currencies, characterConfig);
        var inventory = new InventoryRepository(dataSource);
        var crafts = new CraftRepository(
            dataSource, table, items, characterConfig, grants, inventory);

        if (craftStateOf != 0)
        {
            var state = await crafts.StateAsync(craftStateOf, CancellationToken.None);
            Console.WriteLine(state.Job is null
                ? "craft_state=ok id=" + craftStateOf + " active=false"
                : "craft_state=ok id=" + craftStateOf + " active=true"
                  + " recipe=" + state.Job.RecipeId
                  + " profession=" + state.Job.Profession
                  + " craft_time_s=" + state.Job.CraftTimeS
                  + " remaining_s=" + state.Job.RemainingSeconds
                  + " ready_at_unix=" + state.Job.ReadyAt.ToUnixTimeSeconds());
            foreach (var log in state.Logs)
            {
                Console.WriteLine("craft_log=id=" + craftStateOf
                    + " txn=" + log.TxnId
                    + " action=" + log.Action
                    + " recipe=" + log.RecipeId
                    + " success=" + (log.Success ? "true" : "false")
                    + " output=" + log.OutputItemId
                    + " count=" + log.OutputCount);
            }

            foreach (var currency in state.Currencies)
            {
                Console.WriteLine("craft_currency=id=" + craftStateOf
                    + " kind=" + currency.Kind + " amount=" + currency.Amount);
            }

            foreach (var slot in state.Slots)
            {
                Console.WriteLine("craft_slot=id=" + craftStateOf
                    + " slot=" + slot.Slot + " item=" + slot.ItemId
                    + " count=" + slot.Count + " bound=" + (slot.Bound ? "true" : "false"));
            }
        }

        if (craftStartOf != 0)
        {
            var recipeId = (craftStartRecipe ?? "").Trim();
            if (recipeId.Length == 0)
            {
                Console.WriteLine("craft_start=reject reason=recipe_missing id=" + craftStartOf);
            }
            else
            {
                var txn = (craftTxn ?? "").Trim();
                if (txn.Length == 0)
                {
                    txn = "craft_probe_start:" + Guid.NewGuid().ToString("N");
                }

                try
                {
                    var job = await crafts.StartAsync(
                        craftStartOf, recipeId, txn, CancellationToken.None);
                    Console.WriteLine("craft_start=ok id=" + craftStartOf
                        + " recipe=" + job.RecipeId
                        + " profession=" + job.Profession
                        + " txn=" + txn
                        + " craft_time_s=" + job.CraftTimeS
                        + " remaining_s=" + job.RemainingSeconds
                        + " ready_at_unix=" + job.ReadyAt.ToUnixTimeSeconds());
                }
                catch (CraftException ex)
                {
                    Console.WriteLine("craft_start=reject id=" + craftStartOf
                        + " recipe=" + recipeId + " txn=" + txn + " reason=" + ex.Code);
                }
            }
        }

        if (craftFinishOf != 0)
        {
            var txn = (craftTxn ?? "").Trim();
            if (txn.Length == 0)
            {
                txn = "craft_probe_finish:" + Guid.NewGuid().ToString("N");
            }

            try
            {
                var outcome = await crafts.FinishAsync(
                    craftFinishOf, txn, CancellationToken.None);
                if (outcome.Duplicate)
                {
                    Console.WriteLine("craft_finish=duplicate id=" + craftFinishOf + " txn=" + txn);
                }
                else
                {
                    Console.WriteLine("craft_finish=ok id=" + craftFinishOf
                        + " recipe=" + outcome.RecipeId
                        + " profession=" + outcome.Profession
                        + " txn=" + txn
                        + " success=" + (outcome.Success ? "true" : "false")
                        + " output=" + outcome.OutputItemId
                        + " count=" + outcome.OutputCount
                        + " success_pct=" + outcome.SuccessPct.ToString(
                            "0.##", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            catch (CraftException ex)
            {
                Console.WriteLine("craft_finish=reject id=" + craftFinishOf
                    + " txn=" + txn + " reason=" + ex.Code);
            }
        }

        return 0;
    }

    /// <summary>
    /// 只给核对用：绕过业务规则直写背包一格（0 件 = 删掉这一格）。
    /// 与 --raw-currency-write 同口径，不是业务入口，也不参与任何生产路径。
    /// </summary>
    static async Task RawItemWriteAsync(
        NpgsqlDataSource dataSource, ItemTable items, CharacterConfig config)
    {
        var itemId = (rawItemId ?? "").Trim();
        if (rawItemSlot < 1 || rawItemSlot > config.BagSlots || itemId.Length == 0)
        {
            Console.WriteLine("raw_item=rejected id=" + rawItemWriteId
                + " slot=" + rawItemSlot + " item=" + itemId + " reason=bad_request");
            return;
        }

        var item = items.Find(itemId);
        if (item is null)
        {
            Console.WriteLine("raw_item=rejected id=" + rawItemWriteId
                + " slot=" + rawItemSlot + " item=" + itemId + " reason=unknown_item");
            return;
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            if (rawItemCount == 0)
            {
                await using var delete = new NpgsqlCommand(
                    "delete from \"角色\".inventory where character_id = $1 and slot = $2",
                    connection);
                delete.Parameters.AddWithValue(rawItemWriteId);
                delete.Parameters.AddWithValue((short)rawItemSlot);
                await delete.ExecuteNonQueryAsync();
                Console.WriteLine("raw_item=deleted id=" + rawItemWriteId
                    + " slot=" + rawItemSlot + " item=" + itemId);
                return;
            }

            var bound = item.BindsOnArrival;
            await using var upsert = new NpgsqlCommand(
                "insert into \"角色\".inventory "
                + "(character_id, slot, item_id, count, bound, bound_at) "
                + "values ($1, $2, $3, $4, $5, case when $5 then now() else null end) "
                + "on conflict (character_id, slot) do update "
                + "set item_id = excluded.item_id, count = excluded.count, "
                + "bound = excluded.bound, bound_at = excluded.bound_at",
                connection);
            upsert.Parameters.AddWithValue(rawItemWriteId);
            upsert.Parameters.AddWithValue((short)rawItemSlot);
            upsert.Parameters.AddWithValue(itemId);
            upsert.Parameters.AddWithValue(rawItemCount);
            upsert.Parameters.AddWithValue(bound);
            await upsert.ExecuteNonQueryAsync();
            Console.WriteLine("raw_item=written id=" + rawItemWriteId
                + " slot=" + rawItemSlot + " item=" + itemId
                + " count=" + rawItemCount + " bound=" + (bound ? "true" : "false"));
        }
        catch (PostgresException ex)
        {
            // 核对要看到的就是库层 CHECK 把越界值挡住了（与 --raw-currency-write 同款）。
            Console.WriteLine("raw_item=rejected id=" + rawItemWriteId
                + " slot=" + rawItemSlot + " item=" + itemId + " sqlstate=" + ex.SqlState);
        }
    }
}
