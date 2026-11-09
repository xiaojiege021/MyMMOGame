namespace Gateway;

/// <summary>
/// 连接串从哪来。优先级：环境变量 MMO_GAMEDB，其次是环境文件。
/// 环境文件是 systemd 的 EnvironmentFile 格式（KEY=VALUE），键名不固定，
/// 所以取「值里同时有 Host= 和 Database=」的那一行，避免把别的值当连接串。
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

            var value = line[(eq + 1)..].Trim().Trim('"', '\'');

            // 只认像连接串的值，单放密码的行不会被误认。
            if (value.Contains("Host=", StringComparison.OrdinalIgnoreCase)
                && value.Contains("Database=", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
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
