using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>
/// Q12/K3 角色名的核对入口：
///   --name-rules                    只读，不连库（坏表 / 坏词表在这里就 config_error）
///   --name-rules-path &lt;path&gt;        指到临时规则表试坏配置
///   --name-check &lt;名字&gt;             只读判一个名字（长度 / 可见字符 / 敏感词）
///   --character-rename &lt;角色号&gt; &lt;新名&gt;  走一遍改名（同一笔事务：抢号 + 扣卡 + 换名 + 换登记）
///   --rename-txn &lt;事务号&gt;           这一笔的号；不给就现取。给同一个号重放回 duplicate
///   --name-state &lt;角色号&gt;           登记表里的名字与总行数
///   --name-backfill                 旧库回填登记表（幂等，冲突逐条打出来）
/// 回填要用与代码同一套归一（NFKC），所以不在迁移 SQL 里做，放这里。
/// </summary>
internal static class NameCli
{
    /// <summary>这几个 flag 归本类解析（CliParser 一行分发，避免把行数推过闸门）。</summary>
    public static bool Handles(string flag) => flag
        is "--name-rules" or "--name-rules-path" or "--name-check"
        or "--character-rename" or "--rename-txn" or "--name-state" or "--name-backfill"
        or "--name-registry" or "--name-registry-limit";

    public static int Parse(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--name-rules":
                nameRulesState = true;
                return 0;
            case "--name-rules-path" when i + 1 < args.Length:
                nameRulesState = true;
                nameRulesPath = args[++i];
                return 0;
            case "--name-check" when i + 1 < args.Length:
                nameCheck = args[++i];
                return 0;
            case "--character-rename" when i + 2 < args.Length:
                long.TryParse(args[++i], out characterRenameOf);
                characterRenameTo = args[++i];
                return 0;
            case "--rename-txn" when i + 1 < args.Length:
                renameTxn = args[++i];
                return 0;
            case "--name-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out nameStateOf);
                return 0;
            case "--name-backfill":
                nameBackfill = true;
                return 0;
            case "--name-registry":
                nameRegistry = true;
                return 0;
            case "--name-registry-limit" when i + 1 < args.Length:
                nameRegistry = true;
                int.TryParse(args[++i], out nameRegistryLimit);
                return 0;
            default:
                return 64;
        }
    }

    public static int? TryReadOnly()
    {
        if (!nameRulesState && nameCheck is null)
        {
            return null;
        }

        try
        {
            var cfg = CharacterConfig.Load(configPath);
            var rules = LoadNameRules(cfg, nameRulesPath);

            if (nameRulesState)
            {
                Console.WriteLine("name_rules=ok version=" + rules.Version
                    + " global_unique=" + (rules.GlobalUnique ? "true" : "false")
                    + " allow_rename=" + (rules.AllowRename ? "true" : "false")
                    + " min_length=" + rules.MinLength
                    + " max_length=" + rules.MaxLength
                    + " rename_item_id=" + rules.RenameItemId
                    + " banned_words=" + rules.BannedCount
                    + " banned_words_file=" + rules.BannedWordsFile);
            }

            if (nameCheck is not null)
            {
                var verdict = rules.Check(nameCheck);
                Console.WriteLine("name_check=" + (verdict.Ok ? "ok" : "reject")
                    + " reason=" + (verdict.Reason.Length == 0 ? "-" : verdict.Reason)
                    + " length=" + (verdict.Ok
                        ? new System.Globalization.StringInfo(verdict.Display).LengthInTextElements
                        : 0)
                    + " name_key=" + (verdict.Normalized.Length == 0 ? "-" : verdict.Normalized)
                    + " name=" + (verdict.Display.Length == 0 ? "-" : verdict.Display));
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
        if (characterRenameOf == 0 && nameStateOf == 0 && !nameBackfill && !nameRegistry)
        {
            return null;
        }

        CharacterConfig cfg;
        NameRulesTable rules;
        try
        {
            cfg = CharacterConfig.Load(configPath);
            rules = LoadNameRules(cfg, nameRulesPath);
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

        var characters = ctx.Characters;
        var inventory = new InventoryRepository(ctx.DataSource);
        var items = ItemTable.Load("config/items.json");
        var currencies = CurrencyTable.Load("config/currency.json");
        var cityTable = CityTable.Load("config/city.json");
        var auctionTable = AuctionTable.Load(cfg.AuctionTable, currencies);
        var cityTax = new CityTaxRepository(ctx.DataSource, cityTable, auctionTable.GoldKind);
        var grants = new GrantRepository(ctx.DataSource, items, currencies, cfg, cityTax);

        if (nameBackfill)
        {
            // 旧库升级：把已有角色补进登记表。冲突（归一后同名）逐条报出来，退出 5，
            // 由人决定留哪个——迁移里不做「悄悄挑一个」。
            var rows = await BackfillAsync(ctx);
            Console.WriteLine("name_backfill=" + (rows.Conflicts.Count == 0 ? "ok" : "conflict")
                + " total=" + rows.Total
                + " inserted=" + rows.Inserted
                + " existing=" + rows.Existing
                + " conflicts=" + rows.Conflicts.Count);
            foreach (var conflict in rows.Conflicts)
            {
                Console.WriteLine("name_backfill_conflict key=" + conflict.Key
                    + " character_id=" + conflict.CharacterId
                    + " name=" + conflict.Name);
            }

            return rows.Conflicts.Count == 0 ? 0 : 5;
        }

        if (nameRegistry)
        {
            var limit = nameRegistryLimit <= 0 ? 100 : nameRegistryLimit;
            await using var read = ctx.DataSource.CreateCommand(
                "select name_key, name, character_id from \"角色\".character_name "
                + "order by character_id limit $1");
            read.Parameters.AddWithValue(limit);
            var count = 0;
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                count++;
                Console.WriteLine("name_registry_row=" + reader.GetString(0)
                    + " name=" + reader.GetString(1)
                    + " id=" + reader.GetInt64(2));
            }

            Console.WriteLine("name_registry=ok rows=" + count);
        }

        if (nameStateOf != 0)
        {
            var registered = await characters.RegisteredNameAsync(nameStateOf, CancellationToken.None);
            var total = await characters.NameRegistryCountAsync(CancellationToken.None);
            Console.WriteLine("name_state=ok id=" + nameStateOf
                + " registered=" + (registered ?? "-")
                + " rows=" + total);
        }

        if (characterRenameOf != 0 && !string.IsNullOrWhiteSpace(characterRenameTo))
        {
            var verdict = rules.Check(characterRenameTo);
            if (!verdict.Ok)
            {
                Console.WriteLine("character_rename=reject reason=" + verdict.Reason
                    + " id=" + characterRenameOf);
                return 0;
            }

            var txn = string.IsNullOrWhiteSpace(renameTxn)
                ? "rename_cli:" + Guid.NewGuid().ToString("N")
                : renameTxn!;
            var outcome = await characters.RenameAsync(
                inventory, grants, "", characterRenameOf, verdict.Display, verdict.Normalized,
                rules.RenameItemId, txn, CancellationToken.None);
            Console.WriteLine("character_rename=" + (outcome.Ok ? "ok" : "reject")
                + " id=" + characterRenameOf
                + " reason=" + (outcome.Reason.Length == 0 ? "-" : outcome.Reason)
                + " name=" + (outcome.Name.Length == 0 ? "-" : outcome.Name)
                + " count_left=" + outcome.CountLeft
                + " txn=" + txn);
        }

        return 0;
    }

    /// <summary>回填结果。Total = 库里的角色数，Inserted = 本次新插的登记行。</summary>
    sealed record BackfillReport(
        int Total, int Inserted, int Existing, List<(string Key, long CharacterId, string Name)> Conflicts);

    /// <summary>
    /// 把没有登记行的角色补进 character_name。归一用代码这一份，
    /// SQL 只负责「有没有」与插入；同一名字的第二个人记成冲突，不覆盖先来的。
    /// </summary>
    static async Task<BackfillReport> BackfillAsync(CliContext ctx)
    {
        var conflicts = new List<(string, long, string)>();
        var inserted = 0;
        var existing = 0;
        var total = 0;

        var rows = new List<(long Id, string Name)>();
        await using (var read = ctx.DataSource.CreateCommand(
            "select id, name from \"角色\".character order by id"))
        {
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        total = rows.Count;
        foreach (var row in rows)
        {
            var key = NameRulesTable.Normalize(row.Name);
            if (key.Length == 0)
            {
                conflicts.Add((key, row.Id, row.Name));
                continue;
            }

            await using var insert = ctx.DataSource.CreateCommand(
                "insert into \"角色\".character_name (name_key, name, character_id) "
                + "values ($1, $2, $3) on conflict do nothing");
            insert.Parameters.AddWithValue(key);
            insert.Parameters.AddWithValue(row.Name);
            insert.Parameters.AddWithValue(row.Id);
            var changed = await insert.ExecuteNonQueryAsync();
            if (changed == 1)
            {
                inserted++;
            }
            else
            {
                // 没插进去：要么这个人已经有登记行（幂等），要么名字被别人占了。
                await using var check = ctx.DataSource.CreateCommand(
                    "select character_id from \"角色\".character_name where name_key = $1");
                check.Parameters.AddWithValue(key);
                var owner = await check.ExecuteScalarAsync();
                if (owner is not null and not DBNull && Convert.ToInt64(owner) == row.Id)
                {
                    existing++;
                }
                else
                {
                    conflicts.Add((key, row.Id, row.Name));
                }
            }
        }

        return new BackfillReport(total, inserted, existing, conflicts);
    }
}
