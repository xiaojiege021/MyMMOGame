namespace Global;

/// <summary>
/// 连接串从哪来。优先级：环境变量 MMO_GAMEDB，其次是环境文件。
/// 与网关、角色服里那份同源；全局服务只写「交易」schema 的挂单，不碰角色背包。
/// 连接串带密码，只返回给调用方，不打印、不落盘。
/// </summary>
public static class DbEnv
{
    public const string EnvName = "MMO_GAMEDB";

    public const string DefaultEnvFile = "/srv/mmo/gamedb.env";

    public static string? Resolve()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        return FromFile(DefaultEnvFile);
    }

    public static string Source()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvName)))
        {
            return "env:" + EnvName;
        }

        return FromFile(DefaultEnvFile) != null ? "file:" + DefaultEnvFile : "missing";
    }

    static string? FromFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            values[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"', '\'');
        }

        foreach (var value in values.Values)
        {
            if (value.Contains("Host=", StringComparison.OrdinalIgnoreCase)
                && value.Contains("Database=", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        if (values.TryGetValue("GAMEDB_HOST", out var host)
            && values.TryGetValue("GAMEDB_NAME", out var database)
            && host.Length > 0 && database.Length > 0)
        {
            var port = 5432;
            if (values.TryGetValue("GAMEDB_PORT", out var portText))
            {
                _ = int.TryParse(portText, out port);
            }

            var builder = new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = port > 0 ? port : 5432,
                Database = database,
                Username = values.GetValueOrDefault("GAMEDB_USER"),
                Password = values.GetValueOrDefault("GAMEDB_PASSWORD"),
            };
            return builder.ConnectionString;
        }

        return null;
    }

    public static string WithPoolLimit(string connectionString, int maxPool)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = maxPool,
        };
        return builder.ConnectionString;
    }
}
