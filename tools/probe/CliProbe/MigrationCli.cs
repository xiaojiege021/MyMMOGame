using System.Security.Cryptography;
using Npgsql;

namespace Character.Cli;

/// <summary>
/// D8：迁移版本表。一条命令按 db/migrations.json 的顺序把三个 schema 建齐：
/// 每个 schema 一张 &lt;schema&gt;.schema_version，记版本、文件与 sha256；
/// 主迁移（reapply=true）每次重跑，分卡迁移只跑一次，老库第二次跑只报 skipped。
/// </summary>
internal static class MigrationCli
{
    // 同一时刻只让一个进程迁移：会话级建议锁，进程退出自动释放。
    const long LockKey = 26111701;

    const string VersionTable = "schema_version";

    public static async Task<int> RunAsync(
        NpgsqlDataSource dataSource, string manifestPath, string schemaFilter, bool statusOnly)
    {
        MigrationManifest manifest;
        try
        {
            manifest = MigrationManifest.Load(manifestPath);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("config_error=" + ex.Message);
            return 2;
        }

        var selected = new List<MigrationSchema>();
        if (schemaFilter == "all")
        {
            selected.AddRange(manifest.Schemas);
        }
        else
        {
            var one = manifest.Schemas.FirstOrDefault(schema => schema.Key == schemaFilter);
            if (one is null)
            {
                Console.WriteLine("config_error=--schema 只认 all / "
                    + string.Join(" / ", manifest.Schemas.Select(schema => schema.Key)) + "：" + schemaFilter);
                return 2;
            }

            selected.Add(one);
        }

        Console.WriteLine("migration_manifest=ok path=" + manifestPath
            + " schemas=" + manifest.Schemas.Count
            + " entries=" + manifest.EntryCount);

        await using var connection = await dataSource.OpenConnectionAsync();
        var locked = false;
        try
        {
            if (!statusOnly)
            {
                await using var take = new NpgsqlCommand("select pg_advisory_lock($1)", connection);
                take.Parameters.AddWithValue(LockKey);
                await take.ExecuteNonQueryAsync();
                locked = true;
            }

            // 三个 schema 都先建出来：角色 schema 的主迁移里有 社交.city / 交易.stone_listing
            // 这类跨 schema 的表，只跑一个 schema 时也不能让它们缺壳。
            foreach (var schema in manifest.Schemas)
            {
                await ExecuteAsync(connection, null, "create schema if not exists " + Quote(schema.Name));
            }

            if (statusOnly)
            {
                return await StatusAsync(connection, manifest, selected);
            }

            // 交易的拍卖 / 寄售表带 角色.character 外键，单独补交易 schema 时必须先有角色表。
            if (selected.Any(schema => schema.Key == "trade") && !selected.Any(schema => schema.Key == "character"))
            {
                await using var probe = new NpgsqlCommand(
                    "select to_regclass('\"角色\".character') is not null", connection);
                var hasCore = (bool)(await probe.ExecuteScalarAsync() ?? false);
                if (!hasCore)
                {
                    Console.WriteLine("migrate_error=trade_requires_character 先跑 --schema character");
                    return 6;
                }
            }

            foreach (var schema in selected)
            {
                await EnsureVersionTableAsync(connection, schema);
            }

            var totalApplied = 0;
            var totalSkipped = 0;
            var totalReapplied = 0;
            var totalEntries = 0;
            foreach (var schema in selected)
            {
                var (applied, skipped, reapplied, code) = await ApplySchemaAsync(connection, schema);
                if (code != 0)
                {
                    return code;
                }

                totalApplied += applied;
                totalSkipped += skipped;
                totalReapplied += reapplied;
                totalEntries += schema.Entries.Count;
                Console.WriteLine("migrate_schema=" + schema.Name
                    + " applied=" + applied
                    + " skipped=" + skipped
                    + " reapplied=" + reapplied
                    + " total=" + schema.Entries.Count);
            }

            Console.WriteLine("migrate=ok schema=" + (schemaFilter == "all" ? "all" : schemaFilter)
                + " applied=" + totalApplied
                + " skipped=" + totalSkipped
                + " reapplied=" + totalReapplied
                + " total=" + totalEntries);
            return 0;
        }
        finally
        {
            if (locked)
            {
                try
                {
                    await using var release = new NpgsqlCommand("select pg_advisory_unlock($1)", connection);
                    release.Parameters.AddWithValue(LockKey);
                    await release.ExecuteNonQueryAsync();
                }
                catch (NpgsqlException)
                {
                    // 连接已经断了就不用解了：会话锁随连接一起没了。
                }
            }
        }
    }

    static async Task<(int Applied, int Skipped, int Reapplied, int Code)> ApplySchemaAsync(
        NpgsqlConnection connection, MigrationSchema schema)
    {
        var applied = 0;
        var skipped = 0;
        var reapplied = 0;
        foreach (var entry in schema.Entries)
        {
            string sql;
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(entry.File);
                sql = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (IOException ex)
            {
                Console.WriteLine("config_error=迁移文件读不出来 " + entry.File + "：" + ex.Message);
                return (applied, skipped, reapplied, 2);
            }

            var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var existing = await ReadChecksumAsync(connection, schema, entry.Id);
            if (existing is not null && !entry.Reapply)
            {
                if (existing == checksum)
                {
                    skipped++;
                    continue;
                }

                // 已应用的版本原地改过：老库与仓库里的 DDL 已经对不上，不能装作没事。
                Console.WriteLine("migrate_error=drift id=" + entry.Id + " file=" + entry.File
                    + " 已应用版本的 sha256 变了；要改就新增一条迁移，不要改已应用的版本");
                return (applied, skipped, reapplied, 6);
            }

            try
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await ExecuteAsync(connection, transaction, sql);
                await using var record = new NpgsqlCommand(
                    "insert into " + Quote(schema.Name) + "." + VersionTable
                    + " (id, version, file, checksum, reapply) values ($1, $2, $3, $4, $5)"
                    + " on conflict (id) do update set version = excluded.version,"
                    + " file = excluded.file, checksum = excluded.checksum,"
                    + " reapply = excluded.reapply, updated_at = now()",
                    connection, transaction);
                record.Parameters.AddWithValue(entry.Id);
                record.Parameters.AddWithValue(entry.Version);
                record.Parameters.AddWithValue(entry.File);
                record.Parameters.AddWithValue(checksum);
                record.Parameters.AddWithValue(entry.Reapply);
                await record.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            catch (NpgsqlException ex)
            {
                Console.WriteLine("migrate_error=" + entry.Id + " sqlstate=" + (ex.SqlState ?? "")
                    + " " + ex.Message + " file=" + entry.File);
                return (applied, skipped, reapplied, 6);
            }

            if (existing is null)
            {
                applied++;
                Console.WriteLine("migration=applied id=" + entry.Id + " file=" + entry.File);
            }
            else
            {
                reapplied++;
                Console.WriteLine("migration=reapplied id=" + entry.Id + " file=" + entry.File);
            }
        }

        return (applied, skipped, reapplied, 0);
    }

    static async Task<int> StatusAsync(
        NpgsqlConnection connection, MigrationManifest manifest, List<MigrationSchema> selected)
    {
        var rows = 0;
        foreach (var schema in selected)
        {
            var tableCount = await CountTablesAsync(connection, schema.Name);
            await using var exists = new NpgsqlCommand(
                "select to_regclass('" + Quote(schema.Name) + "." + VersionTable + "') is not null", connection);
            var present = (bool)(await exists.ExecuteScalarAsync() ?? false);
            if (!present)
            {
                Console.WriteLine("schema_status=missing schema=" + schema.Name
                    + " rows=0 expected=" + schema.Entries.Count + " tables=" + tableCount);
                continue;
            }

            await using var read = new NpgsqlCommand(
                "select version, id, file, checksum, reapply from " + Quote(schema.Name) + "." + VersionTable
                + " order by version", connection);
            await using var reader = await read.ExecuteReaderAsync();
            var count = 0;
            var latestVersion = 0;
            var latestId = "";
            while (await reader.ReadAsync())
            {
                count++;
                var version = reader.GetInt32(0);
                latestVersion = version;
                latestId = reader.GetString(1);
                Console.WriteLine("schema_version_row=" + schema.Name
                    + " version=" + version
                    + " id=" + reader.GetString(1)
                    + " file=" + reader.GetString(2)
                    + " reapply=" + (reader.GetBoolean(4) ? "true" : "false")
                    + " checksum=" + reader.GetString(3)[..12]);
            }

            rows += count;
            Console.WriteLine("schema_status=ok schema=" + schema.Name
                + " rows=" + count
                + " expected=" + schema.Entries.Count
                + " tables=" + tableCount
                + " latest_version=" + latestVersion
                + " latest_id=" + latestId);
        }

        Console.WriteLine("schema_status=ok schemas=" + selected.Count + " rows=" + rows);
        return 0;
    }

    static async Task EnsureVersionTableAsync(NpgsqlConnection connection, MigrationSchema schema)
    {
        var sql = "create table if not exists " + Quote(schema.Name) + "." + VersionTable + " ("
            + " id text primary key,"
            + " version integer not null,"
            + " file text not null,"
            + " checksum text not null,"
            + " reapply boolean not null default false,"
            + " applied_at timestamptz not null default now(),"
            + " updated_at timestamptz not null default now(),"
            + " unique (version))";
        await ExecuteAsync(connection, null, sql);
    }

    static async Task<long> CountTablesAsync(NpgsqlConnection connection, string schemaName)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from information_schema.tables where table_schema = $1", connection);
        command.Parameters.AddWithValue(schemaName);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    static async Task<string?> ReadChecksumAsync(
        NpgsqlConnection connection, MigrationSchema schema, string id)
    {
        await using var command = new NpgsqlCommand(
            "select checksum from " + Quote(schema.Name) + "." + VersionTable + " where id = $1", connection);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync() as string;
    }

    static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    // 标识符只来自清单里正则校验过的 schema 名；这里仍统一加引号，避免大小写与保留字问题。
    static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
