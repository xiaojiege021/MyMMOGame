using System.Text.Json;
using System.Text.RegularExpressions;

namespace Character.Cli;

internal sealed class MigrationSchema
{
    // 清单里的名字会直接拼进 CREATE SCHEMA / schema_version 的标识符，只认字母、数字、下划线。
    static readonly Regex Identifier = new(@"^[\p{L}][\p{L}\p{N}_]*$", RegexOptions.Compiled);

    public string Key { get; private init; } = "";
    public string Name { get; private init; } = "";
    public List<MigrationEntry> Entries { get; } = new();

    public static MigrationSchema Parse(JsonElement node, int index)
    {
        var key = MigrationManifest.Text(node, "key");
        var name = MigrationManifest.Text(node, "name");
        if (!Identifier.IsMatch(key) || !Identifier.IsMatch(name))
        {
            throw new ConfigException("迁移清单第 " + (index + 1) + " 个 schema 的 key / name 不合法");
        }

        if (!node.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigException("迁移清单 schema=" + key + " 缺 entries 数组");
        }

        var schema = new MigrationSchema { Key = key, Name = name };
        foreach (var entryNode in entries.EnumerateArray())
        {
            schema.Entries.Add(MigrationEntry.Parse(entryNode, schema, schema.Entries.Count));
        }

        if (schema.Entries.Count == 0)
        {
            throw new ConfigException("迁移清单 schema=" + key + " 一条迁移都没有");
        }

        return schema;
    }
}
