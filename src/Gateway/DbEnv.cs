namespace Gateway;

/// <summary>
/// 连接串从哪来。优先级：环境变量 MMO_GAMEDB，其次是环境文件。
/// 环境文件是 systemd 的 EnvironmentFile 格式（KEY=VALUE），有两种写法：
/// 一整条连接串，或者拆成 GAMEDB_HOST / GAMEDB_PORT / GAMEDB_NAME /
/// GAMEDB_USER / GAMEDB_PASSWORD 几个键（笔记本上现用的是后者）。
/// 两种都认，省得为了这一行去改机器上的环境文件。
/// 连接串带密码，这里只返回给调用方，不打印、不落盘。
/// </summary>
public static class DbEnv
{
    public const string EnvName = "MMO_GAMEDB";

    public const string DefaultEnvFile = "/srv/mmo/gamedb.env";

    public static string? Resolve(string envFile = DefaultEnvFile)
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        return FromFile(envFile);
    }

    /// <summary>返回连接串的来源，用于日志。不返回连接串本身。</summary>
    public static string Source(string envFile = DefaultEnvFile)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvName)))
        {
            return "env:" + EnvName;
        }

        return FromFile(envFile) != null ? "file:" + envFile : "missing";
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

        // 写法一：某个值本身就是一整条连接串。只认像连接串的值，单放密码的行不会被误认。
        foreach (var value in values.Values)
        {
            if (value.Contains("Host=", StringComparison.OrdinalIgnoreCase)
                && value.Contains("Database=", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        // 写法二：拆成几个键（笔记本上的 /srv/mmo/gamedb.env 就是这种）。
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

    /// <summary>连接池留在这个数以内：库的连接上限是 20，别的进程还要用。</summary>
    public static string WithPoolLimit(string connectionString, int maxPool)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = maxPool,
        };
        return builder.ConnectionString;
    }
}
