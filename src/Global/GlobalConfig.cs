using System.Text.Json;
using System.Text.Json.Serialization;

namespace Global;

/// <summary>
/// 全局服务的启动配置：gRPC 听哪儿、连到哪个角色服、连接池多大、灵石档位表在哪。
/// 连接串不在这里，从环境变量或 /srv/mmo/gamedb.env 读。
/// 税率不在这里：成交由角色服按 config/auction.json 判定，全局服务不写货币。
/// </summary>
public sealed record GlobalConfig(
    string Listen, int DbPoolMax, string CharacterUpstream, string StoneExchangeTable)
{
    public const string DefaultCharacterUpstream = "http://127.0.0.1:62000";
    public const string DefaultStoneExchangeTable = "config/stone_exchange.json";

    public static GlobalConfig Load(string path)
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
        if (listen.Length == 0 || !Uri.TryCreate(listen, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ConfigException("listen 必须是 http:// 或 https:// 的地址：" + listen);
        }

        var pool = wire.DbPoolMax ?? 8;
        if (pool < 1 || pool > 20)
        {
            throw new ConfigException("db_pool_max 必须在 1 到 20 之间，现在是 " + pool);
        }

        var character = (wire.CharacterUpstream ?? DefaultCharacterUpstream).Trim().TrimEnd('/');
        if (!Uri.TryCreate(character, UriKind.Absolute, out var characterUri)
            || (characterUri.Scheme != "http" && characterUri.Scheme != "https"))
        {
            throw new ConfigException("character_upstream 必须是 http:// 或 https:// 地址：" + character);
        }

        // P3-2a：挂单校验档位用的表。老卡的临时 global.json 不带这一项时走默认值。
        var stoneExchangeTable = (wire.StoneExchangeTable ?? DefaultStoneExchangeTable).Trim();
        if (stoneExchangeTable.Length == 0)
        {
            throw new ConfigException("stone_exchange_table 不能为空");
        }

        return new GlobalConfig(listen.TrimEnd('/'), pool, character, stoneExchangeTable);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        [JsonPropertyName("listen")]
        public string? Listen { get; init; }

        [JsonPropertyName("db_pool_max")]
        public int? DbPoolMax { get; init; }

        [JsonPropertyName("character_upstream")]
        public string? CharacterUpstream { get; init; }

        [JsonPropertyName("stone_exchange_table")]
        public string? StoneExchangeTable { get; init; }
    }
}

public sealed class ConfigException(string message) : Exception(message);
