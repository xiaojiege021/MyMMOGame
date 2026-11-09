using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 角色服的启动配置：gRPC 听哪儿、连接池多大。
/// 连接串不在这里，从环境或 /srv/mmo/gamedb.env 读。
/// </summary>
public sealed record CharacterConfig(string Listen, int DbPoolMax, int BagSlots, string DungeonTable)
{
    public const string DefaultDungeonTable = "config/dungeons.json";

    public static CharacterConfig Load(string path)
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

        // 背包格子数走配置：扩容道具留给后面的行，这一行只认这个默认值。
        var bagSlots = wire.BagSlots ?? 40;
        if (bagSlots < 1 || bagSlots > 200)
        {
            throw new ConfigException("bag_slots 必须在 1 到 200 之间，现在是 " + bagSlots);
        }

        // 副本表缺省读仓库那一份；核对脚本可以指到临时副本，改上限做隔离测试。
        var dungeonTable = (wire.DungeonTable ?? DefaultDungeonTable).Trim();
        if (dungeonTable.Length == 0)
        {
            throw new ConfigException("dungeon_table 不能为空");
        }

        return new CharacterConfig(listen.TrimEnd('/'), pool, bagSlots, dungeonTable);
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

        [JsonPropertyName("bag_slots")]
        public int? BagSlots { get; init; }

        [JsonPropertyName("dungeon_table")]
        public string? DungeonTable { get; init; }
    }
}

public sealed class ConfigException(string message) : Exception(message);
