using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gateway;

/// <summary>
/// 网关的启动配置。听地址、令牌有效期、口令哈希的迭代次数，以及战斗服在上游哪个地址。
/// 连接串不在这里：它带密码，只从环境变量或 /srv/mmo/gamedb.env 读。
/// </summary>
public sealed record GatewayConfig(
    string Listen,
    int TokenTtlSeconds,
    int Pbkdf2Iterations,
    string BattleUpstream,
    string CharacterUpstream)
{
    public const int DefaultTokenTtlSeconds = 7200;

    public const int DefaultPbkdf2Iterations = 210_000;

    public const string DefaultBattleUpstream = "ws://127.0.0.1:60001/battle";

    public const string DefaultCharacterUpstream = "http://127.0.0.1:62000";

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
        if (ttl < 1 || ttl > 86_400)
        {
            throw new ConfigException("token_ttl_s 必须在 1 到 86400 之间，现在是 " + ttl);
        }

        var iterations = wire.Pbkdf2Iterations ?? DefaultPbkdf2Iterations;
        if (iterations < 10_000 || iterations > 1_000_000)
        {
            throw new ConfigException("pbkdf2_iterations 必须在 10000 到 1000000 之间，现在是 " + iterations);
        }

        var upstream = (wire.BattleUpstream ?? DefaultBattleUpstream).Trim();
        if (!Uri.TryCreate(upstream, UriKind.Absolute, out var upstreamUri)
            || (upstreamUri.Scheme != "ws" && upstreamUri.Scheme != "wss"))
        {
            throw new ConfigException("battle_upstream 必须是 ws:// 或 wss:// 地址：" + upstream);
        }

        var character = (wire.CharacterUpstream ?? DefaultCharacterUpstream).Trim().TrimEnd('/');
        if (!Uri.TryCreate(character, UriKind.Absolute, out var characterUri)
            || (characterUri.Scheme != "http" && characterUri.Scheme != "https"))
        {
            throw new ConfigException("character_upstream 必须是 http:// 或 https:// 地址：" + character);
        }

        return new GatewayConfig(listen, ttl, iterations, upstream, character);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        // 名字逐个标出来。命名策略会把 TokenTtlSeconds 折成 token_ttl_seconds，
        // 与配置里写的 token_ttl_s 对不上，结果悄悄用了默认值——这个坑踩过一次。
        [JsonPropertyName("listen")]
        public string? Listen { get; init; }

        [JsonPropertyName("token_ttl_s")]
        public int? TokenTtlSeconds { get; init; }

        [JsonPropertyName("pbkdf2_iterations")]
        public int? Pbkdf2Iterations { get; init; }

        [JsonPropertyName("battle_upstream")]
        public string? BattleUpstream { get; init; }

        [JsonPropertyName("character_upstream")]
        public string? CharacterUpstream { get; init; }
    }
}

public sealed class ConfigException(string message) : Exception(message);
