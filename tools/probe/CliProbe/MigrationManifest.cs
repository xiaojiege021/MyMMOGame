using System.Text.Json;
using System.Text.RegularExpressions;

namespace Character.Cli;

/// <summary>
/// D8：迁移清单。三个 schema 各一份有序清单，版本号从 1 连续排；
/// reapply=true 的是幂等主迁移（每次重跑），false 的是只跑一次的分卡迁移。
/// 只在 --migrate / --schema-status 时读；清单坏掉按 config_error 退出，不带着半截清单落库。
/// </summary>
internal sealed class MigrationManifest
{
    public const string SupportedSchema = "migration-manifest/1";

    public List<MigrationSchema> Schemas { get; } = new();

    public int EntryCount => Schemas.Sum(schema => schema.Entries.Count);

    public static MigrationManifest Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("迁移清单不存在：" + path);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigException("迁移清单不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("迁移清单顶层必须是对象");
            }

            if (Text(root, "schema") != SupportedSchema)
            {
                throw new ConfigException("迁移清单 schema 必须是 " + SupportedSchema);
            }

            if (Number(root, "version") != 1)
            {
                throw new ConfigException("迁移清单 version 必须是 1");
            }

            if (!root.TryGetProperty("schemas", out var schemas) || schemas.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("迁移清单缺 schemas 数组");
            }

            var manifest = new MigrationManifest();
            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in schemas.EnumerateArray())
            {
                var schema = MigrationSchema.Parse(node, manifest.Schemas.Count);
                foreach (var entry in schema.Entries)
                {
                    if (!files.Add(entry.File))
                    {
                        throw new ConfigException("迁移清单重复登记同一份 SQL：" + entry.File);
                    }
                }

                manifest.Schemas.Add(schema);
            }

            if (manifest.Schemas.Count == 0)
            {
                throw new ConfigException("迁移清单里一个 schema 都没有");
            }

            return manifest;
        }
    }

    internal static string Text(JsonElement node, string key)
    {
        if (!node.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ConfigException("迁移清单缺字符串字段 " + key);
        }

        return value.GetString() ?? "";
    }

    internal static int Number(JsonElement node, string key)
    {
        if (!node.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number))
        {
            throw new ConfigException("迁移清单缺整数字段 " + key);
        }

        return number;
    }

    internal static bool Flag(JsonElement node, string key)
    {
        if (!node.TryGetProperty(key, out var value) || (value.ValueKind != JsonValueKind.True
            && value.ValueKind != JsonValueKind.False))
        {
            throw new ConfigException("迁移清单字段 " + key + " 必须是 true / false");
        }

        return value.GetBoolean();
    }
}
