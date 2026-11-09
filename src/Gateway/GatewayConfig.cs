using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gateway;

/// <summary>
/// 网关的启动配置。只听地址、令牌有效期和口令哈希的迭代次数。
/// 连接串不在这里：它带密码，只从环境变量或 /srv/mmo/gamedb.env 读。
/// </summary>
public sealed record GatewayConfig(string Listen, int TokenTtlSeconds, int Pbkdf2Iterations)
{
    public const int DefaultTokenTtlSeconds = 7200;

    public const int DefaultPbkdf2Iterations = 210_000;

    public static GatewayConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配置文件 " + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("配置是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配置不是合法 JSON：" + ex.Message);
        }

        var listen = (wire.Listen ?? "").Trim();
        if (listen.Length == 0)
        {
            throw new ConfigException("listen 不能为空");
        }

        if (!listen.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !listen.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigException("listen 必须是 http:// 或 https:// 开头的地址：" + listen);
        }

        var ttl = wire.TokenTtlSeconds ?? DefaultTokenTtlSeconds;
        if (ttl < 60 || ttl > 86_400)
        {
            throw new ConfigException("token_ttl_s 必须在 60 到 86400 之间，现在是 " + ttl);
        }

        var iterations = wire.Pbkdf2Iterations ?? DefaultPbkdf2Iterations;
        if (iterations < 10_000 || iterations > 1_000_000)
        {
            throw new ConfigException("pbkdf2_iterations 必须在 10000 到 1000000 之间，现在是 " + iterations);
        }

        return new GatewayConfig(listen, ttl, iterations);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        public string? Listen { get; init; }

        public int? TokenTtlSeconds { get; init; }

        public int? Pbkdf2Iterations { get; init; }
    }
}

public sealed class ConfigException(string message) : Exception(message);
