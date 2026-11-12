using System.Text.Json;
using System.Text.Json.Serialization;

namespace Social;

/// <summary>
/// 社交服的启动配置：gRPC 听哪儿、连接池多大、数值正本在哪、角色服在哪。
/// 连接串不在这里，从环境变量或 /srv/mmo/gamedb.env 读。
/// 数值正本（五级上限）只有 config/sect.json 一份，这里只记它的路径。
/// </summary>
public sealed record SocialConfig(
    string Listen, int DbPoolMax, string SectTable, string CharacterUpstream)
{
    public const string DefaultSectTable = "config/sect.json";
    public const string DefaultCharacterUpstream = "http://127.0.0.1:62000";

    public static SocialConfig Load(string path)
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

        // 五级人数上限只在这份正本里，核对脚本可以用 --sect-table 指到临时表改数值。
        var sectTable = (wire.SectTable ?? DefaultSectTable).Trim();
        if (sectTable.Length == 0)
        {
            throw new ConfigException("sect_table 不能为空");
        }

        // 创建金币的扣减经角色服 ApplyGrant，社交服不碰角色 schema。
        var character = (wire.CharacterUpstream ?? DefaultCharacterUpstream).Trim().TrimEnd('/');
        if (!Uri.TryCreate(character, UriKind.Absolute, out var characterUri)
            || (characterUri.Scheme != "http" && characterUri.Scheme != "https"))
        {
            throw new ConfigException("character_upstream 必须是 http:// 或 https:// 地址：" + character);
        }

        return new SocialConfig(listen.TrimEnd('/'), pool, sectTable, character);
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

        [JsonPropertyName("sect_table")]
        public string? SectTable { get; init; }

        [JsonPropertyName("character_upstream")]
        public string? CharacterUpstream { get; init; }
    }
}

public sealed class ConfigException(string message) : Exception(message);
