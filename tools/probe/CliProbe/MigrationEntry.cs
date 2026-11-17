using System.Text.Json;

namespace Character.Cli;

internal sealed class MigrationEntry
{
    public int Version { get; private init; }
    public string File { get; private init; } = "";
    public bool Reapply { get; private init; }
    public string Id => Schema.Key + "/" + Version.ToString("0000");

    public MigrationSchema Schema { get; private init; } = null!;

    public static MigrationEntry Parse(JsonElement node, MigrationSchema schema, int index)
    {
        var version = MigrationManifest.Number(node, "version");
        if (version != index + 1)
        {
            // 版本号必须从 1 连续排：留着空号会让「已跑到哪一版」没法一眼看清。
            throw new ConfigException("迁移清单 schema=" + schema.Key + " 的版本号必须从 1 连续，"
                + "第 " + (index + 1) + " 条写的是 " + version);
        }

        var file = MigrationManifest.Text(node, "file").Replace('\\', '/');
        var prefix = "db/" + schema.Key + "/";
        if (!file.StartsWith(prefix, StringComparison.Ordinal) || file.Contains("..", StringComparison.Ordinal)
            || !file.EndsWith(".sql", StringComparison.Ordinal))
        {
            throw new ConfigException("迁移清单 " + schema.Key + "/" + version + " 的 file 必须落在 " + prefix + "："
                + file);
        }

        if (!System.IO.File.Exists(file))
        {
            throw new ConfigException("迁移清单登记的 SQL 不存在：" + file);
        }

        var reapply = false;
        if (node.TryGetProperty("reapply", out var flag))
        {
            if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new ConfigException("迁移清单 " + file + " 的 reapply 必须是 true / false");
            }

            reapply = flag.GetBoolean();
        }

        return new MigrationEntry
        {
            Version = version,
            File = file,
            Reapply = reapply,
            Schema = schema,
        };
    }
}
