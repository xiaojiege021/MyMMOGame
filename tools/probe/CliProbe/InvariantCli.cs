using System.Data;
using Npgsql;
using NpgsqlTypes;

using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// D17 不变量核对：把「钱 / 背包 / 拍卖 / 经验 / 迁移 / 角色位」的语义不变量跑一遍。
///
/// 只读不是靠约定：连接上先 `set transaction read only`，之后任何 insert / update /
/// delete 都会被 Postgres 直接拒绝（25006），所以这个入口写不了库。每条不变量输出一行
/// `invariant=ok|fail id=INV-nn name=... violations=N [sample=...]`，末行
/// `invariants=ok|fail total=... violations=...`；有违反时退出 7（固定 7，便于脚本判断）。
///
/// 判定口径与编号只写在 docs/不变量清单.md；这里逐条对应，编号不重排、不改名。
/// 查询结果为空就是 0 违反；某张表不存在（老库没跑过迁移）按 1 条违反报，不静默跳过。
/// </summary>
internal static class InvariantCli
{
    public const int ExitViolations = 7;
    public const int ExitConfig = 2;

    sealed record Check(string Id, string Name, string Sql, Action<NpgsqlParameterCollection>? Bind = null);

    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        if (!invariants)
        {
            return null;
        }

        CharacterConfig cfg;
        CurrencyTable currencies;
        ItemTable items;
        ExpTable exp;
        CharacterSlotTable slots;
        try
        {
            // 与真实启动共用同一份 Load：库里的值要按服务端真正会加载的那套规则判。
            cfg = CharacterConfig.Load(configPath);
            currencies = CurrencyTable.Load("config/currency.json");
            items = ItemTable.Load("config/items.json");
            exp = ExpTable.Load(cfg.ExpTable);
            slots = CliLoaders.LoadCharacterSlot(cfg, null);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return ExitConfig;
        }

        var kinds = currencies.All.Select(c => c.Id).ToArray();
        var itemIds = items.All.Select(i => i.Id).ToArray();
        var checks = BuildChecks(cfg, exp, slots, kinds, itemIds);

        var stopping = CancellationToken.None;
        await using var connection = await ctx.DataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, stopping);
        await ExecAsync(connection, transaction, "set transaction read only", stopping);

        // 只读与库名都打出来：日志里能直接证明「这段没有写库」，也不靠外面传进来的名字猜。
        Console.WriteLine("invariants_db="
            + await ScalarAsync(connection, transaction, "select current_database()", stopping));
        Console.WriteLine("invariants_read_only="
            + await ScalarAsync(connection, transaction, "select current_setting('transaction_read_only')", stopping));

        var total = 0;
        var passed = 0;
        long violations = 0;
        foreach (var check in checks)
        {
            total++;
            await ExecAsync(connection, transaction, "savepoint inv", stopping);
            long count;
            string sample;
            try
            {
                (count, sample) = check.Id == "INV-15"
                    ? await SchemaVersionAsync(connection, transaction, migrationsPath, stopping)
                    : await RunCountAsync(connection, transaction, check, stopping);
                await ExecAsync(connection, transaction, "release savepoint inv", stopping);
            }
            catch (PostgresException ex)
            {
                // 单条坏掉（表缺 / 查询被只读挡住）不能把整笔事务带崩：回到 savepoint 继续跑。
                await ExecAsync(connection, transaction, "rollback to savepoint inv", stopping);
                await ExecAsync(connection, transaction, "release savepoint inv", stopping);
                (count, sample) = (1L, "error=" + ex.SqlState + " " + Short(ex.MessageText));
            }

            violations += count;
            if (count == 0)
            {
                passed++;
                Console.WriteLine("invariant=ok id=" + check.Id + " name=" + check.Name + " violations=0");
            }
            else
            {
                Console.WriteLine("invariant=fail id=" + check.Id + " name=" + check.Name
                    + " violations=" + count + " sample=" + sample);
            }
        }

        await transaction.RollbackAsync(stopping);
        Console.WriteLine("invariants=" + (violations == 0 ? "ok" : "fail")
            + " total=" + total + " ok=" + passed + " violations=" + violations);
        return violations == 0 ? 0 : ExitViolations;
    }

    static List<Check> BuildChecks(
        CharacterConfig cfg, ExpTable exp, CharacterSlotTable slots, string[] kinds, string[] itemIds)
    {
        return
        [
            new("INV-01", "currency_non_negative", """
                select 'character=' || character_id || ' kind=' || kind || ' amount=' || amount as detail
                from "角色".currency
                where amount < 0
                """),
            new("INV-02", "currency_kind_known", """
                select 'currency character=' || character_id || ' kind=' || kind as detail
                from "角色".currency
                where not (kind = any($1))
                union all
                select 'stone_listing=' || listing_id || ' kind=' || stone_kind as detail
                from "交易".stone_listing
                where not (stone_kind = any($1))
                """, p => p.AddWithValue(kinds)),
            new("INV-03", "inventory_count_and_slot", """
                select 'character=' || character_id || ' slot=' || slot
                       || ' count=' || count || ' item=' || item_id as detail
                from "角色".inventory
                where count <= 0 or slot < 1 or slot > $1
                """, p => p.AddWithValue(cfg.BagSlots)),
            new("INV-04", "inventory_bound_pair", """
                select 'character=' || character_id || ' slot=' || slot
                       || ' bound=' || bound::text
                       || ' bound_at=' || coalesce(bound_at::text, 'null') as detail
                from "角色".inventory
                where bound <> (bound_at is not null)
                """),
            new("INV-05", "inventory_item_known", """
                select 'character=' || character_id || ' slot=' || slot || ' item=' || item_id as detail
                from "角色".inventory
                where not (item_id = any($1))
                """, p => p.AddWithValue(itemIds)),
            new("INV-06", "live_listing_not_bound", """
                select 'listing=' || l.listing_id || ' seller=' || l.seller_id
                       || ' slot=' || l.slot || ' item=' || i.item_id as detail
                from "交易".auction_listing l
                join "角色".inventory i
                  on i.character_id = l.seller_id and i.slot = l.slot
                where l.state = 'listed' and i.bound
                """),
            new("INV-07", "live_listing_item_present", """
                select 'listing=' || l.listing_id || ' seller=' || l.seller_id
                       || ' slot=' || l.slot || ' need=' || l.count
                       || ' have=' || coalesce(i.count, 0) as detail
                from "交易".auction_listing l
                left join "角色".inventory i
                  on i.character_id = l.seller_id and i.slot = l.slot
                where l.state = 'listed' and (i.id is null or i.count < l.count)
                """),
            new("INV-08", "item_settlement_balance", """
                select 'txn=' || txn_id || ' net=' || net_gold
                       || ' tax=' || tax_gold || ' price=' || price_gold as detail
                from "交易".auction_settlement
                where net_gold + tax_gold <> price_gold
                """),
            new("INV-09", "ingot_settlement_balance", """
                select 'txn=' || txn_id || ' net=' || net_gold
                       || ' tax=' || tax_gold || ' price=' || price_gold as detail
                from "交易".auction_ingot_settlement
                where net_gold + tax_gold <> price_gold
                """),
            new("INV-10", "stone_settlement_balance", """
                select 'txn=' || txn_id || ' net=' || net_gold
                       || ' tax=' || tax_gold || ' price=' || price_gold as detail
                from "交易".stone_settlement
                where net_gold + tax_gold <> price_gold
                """),
            new("INV-11", "auction_ledger_linkage", """
                select 'item sold_without_settlement listing=' || l.listing_id as detail
                from "交易".auction_listing l
                where l.state = 'sold'
                  and not exists (select 1 from "交易".auction_settlement s
                                  where s.listing_id = l.listing_id)
                union all
                select 'item settlement_without_sold txn=' || s.txn_id as detail
                from "交易".auction_settlement s
                join "交易".auction_listing l on l.listing_id = s.listing_id
                where l.state <> 'sold'
                union all
                select 'item repeated_settlement listing=' || s.listing_id as detail
                from "交易".auction_settlement s
                group by s.listing_id having count(*) > 1
                union all
                select 'item txn_without_settlement txn=' || t.txn_id as detail
                from "交易".auction_txn t
                where not exists (select 1 from "交易".auction_settlement s
                                  where s.txn_id = t.txn_id)
                union all
                select 'ingot sold_without_settlement listing=' || l.listing_id as detail
                from "交易".auction_ingot_listing l
                where l.state = 'sold'
                  and not exists (select 1 from "交易".auction_ingot_settlement s
                                  where s.listing_id = l.listing_id)
                union all
                select 'ingot settlement_without_sold txn=' || s.txn_id as detail
                from "交易".auction_ingot_settlement s
                join "交易".auction_ingot_listing l on l.listing_id = s.listing_id
                where l.state <> 'sold'
                union all
                select 'ingot repeated_settlement listing=' || s.listing_id as detail
                from "交易".auction_ingot_settlement s
                group by s.listing_id having count(*) > 1
                union all
                select 'ingot txn_without_settlement txn=' || t.txn_id as detail
                from "交易".auction_ingot_txn t
                where not exists (select 1 from "交易".auction_ingot_settlement s
                                  where s.txn_id = t.txn_id)
                union all
                select 'stone sold_without_settlement listing=' || l.listing_id as detail
                from "交易".stone_listing l
                where l.state = 'sold'
                  and not exists (select 1 from "交易".stone_settlement s
                                  where s.listing_id = l.listing_id)
                union all
                select 'stone settlement_without_sold txn=' || s.txn_id as detail
                from "交易".stone_settlement s
                join "交易".stone_listing l on l.listing_id = s.listing_id
                where l.state <> 'sold'
                union all
                select 'stone repeated_settlement listing=' || s.listing_id as detail
                from "交易".stone_settlement s
                group by s.listing_id having count(*) > 1
                union all
                select 'stone txn_without_settlement txn=' || t.txn_id as detail
                from "交易".stone_auction_txn t
                where not exists (select 1 from "交易".stone_settlement s
                                  where s.txn_id = t.txn_id)
                """),
            new("INV-12", "character_level_exp", """
                select 'character=' || id || ' level=' || level || ' exp=' || exp as detail
                from "角色".character
                where level < 1 or level > $1 or exp < 0 or (level < $1 and exp >= $2)
                """, p =>
            {
                p.AddWithValue(exp.LevelCap);
                p.AddWithValue(exp.ExpPerLevel);
            }),
            new("INV-13", "fatigue_share_non_negative", """
                select 'character=' || id || ' fatigue=' || fatigue
                       || ' weekly_share=' || weekly_share as detail
                from "角色".character
                where fatigue < 0 or weekly_share < 0
                """),
            new("INV-14", "exp_settlement_no_level_drop", """
                select 'txn=' || txn_id || ' before=' || level_before
                       || ' after=' || level_after as detail
                from "角色".exp_settlement
                where level_after < level_before
                """),
            // INV-15 不在这张表里：它按 db/migrations.json 逐个 schema 比 id 集合，见 SchemaVersionAsync。
            new("INV-15", "schema_version_matches_manifest", ""),
            new("INV-16", "character_slot_range", """
                select 'account=' || a.account || ' characters=' || count(c.id)
                       || ' slots=' || coalesce(s.slots, $1) as detail
                from "角色".account a
                left join "角色".character c on c.account = a.account
                left join "角色".character_slot s on s.account = a.account
                group by a.account, s.slots
                having count(c.id) > coalesce(s.slots, $1)
                union all
                select 'account=' || account || ' slots=' || slots as detail
                from "角色".character_slot
                where slots < 1 or slots > 16
                """, p => p.AddWithValue(slots.BaseSlots)),
            new("INV-17", "recharge_order_status_pair", """
                select 'order=' || order_id || ' status=' || status
                       || ' paid_at=' || coalesce(paid_at::text, 'null') as detail
                from "角色".recharge_order
                where (status = 'paid') <> (paid_at is not null)
                """),
            // Q4：个人贡献与任务流水是同一笔事务写的；贡献行是累计，流水记每一笔。
            new("INV-18", "sect_contribution_matches_ledger", """
                select 'sect=' || c.sect_id || ' character=' || c.character_id
                       || ' contribution=' || c.contribution
                       || ' ledger=' || coalesce(sum(l.contribution), 0) as detail
                from "社交".sect_contribution c
                left join "社交".sect_task_ledger l
                       on l.sect_id = c.sect_id and l.character_id = c.character_id
                group by c.sect_id, c.character_id, c.contribution
                having c.contribution <> coalesce(sum(l.contribution), 0)
                union all
                select 'ledger-only sect=' || l.sect_id || ' character=' || l.character_id as detail
                from "社交".sect_task_ledger l
                where not exists (
                    select 1 from "社交".sect_contribution c
                    where c.sect_id = l.sect_id and c.character_id = l.character_id)
                """),
            // Q4：进度 +1 与流水一行同笔提交；次数上限、重置、面板都读进度。
            new("INV-19", "sect_task_progress_matches_ledger", """
                select 'character=' || p.character_id || ' task=' || p.task_id
                       || ' period=' || p.period_key || ' done=' || p.done_count
                       || ' ledger=' || count(l.txn_id) as detail
                from "社交".sect_task_progress p
                left join "社交".sect_task_ledger l
                       on l.character_id = p.character_id and l.task_id = p.task_id
                      and l.period_key = p.period_key
                group by p.character_id, p.task_id, p.period_key, p.done_count
                having p.done_count <> count(l.txn_id)
                union all
                select 'ledger-only character=' || l.character_id || ' task=' || l.task_id
                       || ' period=' || l.period_key as detail
                from "社交".sect_task_ledger l
                where not exists (
                    select 1 from "社交".sect_task_progress p
                    where p.character_id = l.character_id and p.task_id = l.task_id
                      and p.period_key = l.period_key)
                """),
            // Q5：城池任务的进度次数与流水笔数同笔提交。一条奖励一个币种写一行流水，
            // 所以这里数的是 distinct txn_id（一次完成），不是行数。
            new("INV-20", "city_task_progress_matches_ledger", """
                select 'character=' || p.character_id || ' task=' || p.task_id
                       || ' period=' || p.period_key || ' done=' || p.done_count
                       || ' ledger=' || count(distinct l.txn_id) as detail
                from "角色".city_task_progress p
                left join "角色".city_task_ledger l
                       on l.character_id = p.character_id and l.task_id = p.task_id
                      and l.period_key = p.period_key
                group by p.character_id, p.task_id, p.period_key, p.done_count
                having p.done_count <> count(distinct l.txn_id)
                union all
                select 'ledger-only character=' || l.character_id || ' task=' || l.task_id
                       || ' period=' || l.period_key as detail
                from "角色".city_task_ledger l
                where not exists (
                    select 1 from "角色".city_task_progress p
                    where p.character_id = l.character_id and p.task_id = l.task_id
                      and p.period_key = l.period_key)
                """),
            // Q5：灵石只从唯一发放入口来——每笔城池任务流水都能在「角色」.grant_txn
            // 里找到同一个事务号。找不到说明有人绕开 GrantRepository 直写了货币。
            new("INV-21", "city_task_ledger_has_grant_txn", """
                select 'txn=' || l.txn_id || ' character=' || l.character_id
                       || ' task=' || l.task_id || ' currency=' || l.currency as detail
                from "角色".city_task_ledger l
                where not exists (
                    select 1 from "角色".grant_txn g where g.txn_id = l.txn_id)
                """),
        ];
    }

    /// <summary>
    /// 一条不变量 = 一段「返回违反行的 select」，每行必须有一列 detail。
    /// 外层只做两件事：数行数、取前三条 detail 当 sample；查询本身不改库。
    /// </summary>
    static async Task<(long Count, string Sample)> RunCountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Check check, CancellationToken stopping)
    {
        var sql = "select count(*)::bigint, (array_agg(detail))[1:3] from (" + check.Sql + ") v";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        check.Bind?.Invoke(command.Parameters);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return (0L, "");
        }

        var count = reader.GetInt64(0);
        var sample = reader.IsDBNull(1) ? "" : string.Join(" | ", reader.GetFieldValue<string[]>(1));
        return (count, Short(sample));
    }

    /// <summary>
    /// INV-15：每个 schema 的 schema_version 必须与清单的 id 集合逐条相等。
    /// 缺表按一条违反报：老库没跑过 D8 的迁移时，这一条会最先红。
    /// </summary>
    static async Task<(long Count, string Sample)> SchemaVersionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string manifestPath, CancellationToken stopping)
    {
        var manifest = MigrationManifest.Load(manifestPath);
        var problems = new List<string>();
        foreach (var schema in manifest.Schemas)
        {
            var table = Quote(schema.Name) + ".schema_version";
            // 先问 to_regclass 再看表：直接查不存在的表会把整笔事务置为 aborted，
            // 后面两个 schema 就只剩 25P02 了（第一版真踩过）。
            if (!await ScalarBoolAsync(connection, transaction,
                    "select to_regclass('" + table + "') is not null", stopping))
            {
                problems.Add("schema=" + schema.Key + " table_missing");
                continue;
            }

            var rows = new List<string>();
            await using (var command = new NpgsqlCommand(
                "select id from " + table, connection, transaction))
            {
                await using var reader = await command.ExecuteReaderAsync(stopping);
                while (await reader.ReadAsync(stopping))
                {
                    rows.Add(reader.GetString(0));
                }
            }

            var want = schema.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var got = rows.ToHashSet(StringComparer.Ordinal);
            foreach (var id in want.Except(got).OrderBy(x => x, StringComparer.Ordinal))
            {
                problems.Add("schema=" + schema.Key + " missing=" + id);
            }

            foreach (var id in got.Except(want).OrderBy(x => x, StringComparer.Ordinal))
            {
                problems.Add("schema=" + schema.Key + " extra=" + id);
            }
        }

        return (problems.Count, Short(string.Join(" | ", problems.Take(3))));
    }

    static async Task ExecAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(stopping);
    }

    static async Task<string> ScalarAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = await command.ExecuteScalarAsync(stopping);
        return value?.ToString() ?? "";
    }

    static async Task<bool> ScalarBoolAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync(stopping) is true;
    }

    /// <summary>库名来自清单，仍统一加引号：中文 schema 名与保留字都不该靠调用方拼对。</summary>
    static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>一行日志别把整张表倒出来：sample 只留前 160 字，细节回库里按 id 查。</summary>
    static string Short(string text) => text.Length <= 160 ? text : text[..160] + "…";
}
