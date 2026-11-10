using System.Text.Json;
using System.Text.Json.Serialization;

namespace World;

/// <summary>
/// 一个采集节点。kind 只有三种：ore 矿脉、herb 药草、spirit_stone 灵石矿脉。
/// 灵石矿脉按方案 9.5 归主城辖区，不开放给玩家个人去挖，所以它没有产物字段；
/// 就算有人往配置里塞 item_id，也在这里直接被判成坏配置，世界服起不来。
/// </summary>
public sealed record GatherNode(
    string Id,
    string Kind,
    string RegionId,
    string ItemId,
    int Count,
    int BagSlot,
    int RespawnSeconds);

/// <summary>
/// 采集节点的唯一配置正本（config/gather.json）。方案只写定「矿脉给矿石、药草给药材、
/// 灵石矿脉不许个人采、采集不耗疲劳」，每次产量、刷新秒数、产物入哪一格与节点编号都没写定，
/// 全在这份配置里注明待定，代码里不给兜底。缺字段、编号重复、非法类型或非正数都起不来。
/// </summary>
public sealed class GatherTable
{
    public const string Ore = "ore";
    public const string Herb = "herb";
    public const string SpiritStone = "spirit_stone";

    readonly Dictionary<string, GatherNode> byId;

    GatherTable(string characterUpstream, IReadOnlyList<GatherNode> nodes)
    {
        CharacterUpstream = characterUpstream;
        Nodes = nodes;
        byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
    }

    public string CharacterUpstream { get; }

    public IReadOnlyList<GatherNode> Nodes { get; }

    public GatherNode? Find(string id) => byId.GetValueOrDefault((id ?? "").Trim());

    /// <summary>regions 给定时顺带核对节点确实长在已配置的地表上（启动用）；只读入口可不传。</summary>
    public static GatherTable Load(string path, IReadOnlySet<string>? regions = null)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到采集配置 " + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("采集配置是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("采集配置不是合法 JSON：" + ex.Message);
        }

        var version = wire.Version ?? throw new ConfigException("gather.json 缺 version");
        if (version < 1)
        {
            throw new ConfigException("gather.json version 必须大于 0，现在是 " + version);
        }

        var upstream = (wire.CharacterUpstream ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(upstream, UriKind.Absolute, out var upstreamUri)
            || (upstreamUri.Scheme != Uri.UriSchemeHttp && upstreamUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ConfigException("gather.json 的 character_upstream 必须是 http(s) 地址：" + upstream);
        }

        if (wire.Nodes is null || wire.Nodes.Count == 0)
        {
            throw new ConfigException("gather.json 缺 nodes");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<GatherNode>(wire.Nodes.Count);
        foreach (var raw in wire.Nodes)
        {
            var id = (raw.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("gather.json 里有空节点编号");
            }

            if (!seen.Add(id))
            {
                throw new ConfigException("gather.json 里有重复节点编号 " + id);
            }

            var kind = (raw.Kind ?? "").Trim();
            if (kind is not (Ore or Herb or SpiritStone))
            {
                throw new ConfigException("gather.json 节点 " + id + " 的 kind 不认识：" + kind);
            }

            var region = (raw.RegionId ?? "").Trim();
            if (region.Length == 0)
            {
                throw new ConfigException("gather.json 节点 " + id + " 缺 region_id");
            }

            if (regions is not null && !regions.Contains(region))
            {
                throw new ConfigException("gather.json 节点 " + id + " 的地点不在 world.json 里：" + region);
            }

            if (kind == SpiritStone)
            {
                // 灵石矿脉不是个人采集点：连产物字段都不许配，避免有人从配置把它开出来。
                if (raw.ItemId is not null || raw.Count is not null
                    || raw.BagSlot is not null || raw.RespawnSeconds is not null)
                {
                    throw new ConfigException("gather.json 的灵石矿脉 " + id + " 不许配产物或刷新字段");
                }

                kinds.Add(kind);
                nodes.Add(new GatherNode(id, kind, region, "", 0, 0, 0));
                continue;
            }

            var itemId = (raw.ItemId ?? "").Trim();
            if (itemId.Length == 0)
            {
                throw new ConfigException("gather.json 节点 " + id + " 缺 item_id");
            }

            var count = Positive(raw.Count, "count", id);
            var slot = Positive(raw.BagSlot, "bag_slot", id);
            var respawn = Positive(raw.RespawnSeconds, "respawn_s", id);

            kinds.Add(kind);
            nodes.Add(new GatherNode(id, kind, region, itemId, count, slot, respawn));
        }

        // 三种都要在表里：删掉灵石矿脉就能绕过「个人采不到灵石」这条规则，不许这么配。
        foreach (var required in new[] { Ore, Herb, SpiritStone })
        {
            if (!kinds.Contains(required))
            {
                throw new ConfigException("gather.json 缺 kind=" + required + " 的节点");
            }
        }

        return new GatherTable(upstream, nodes);
    }

    static int Positive(int? value, string key, string id)
    {
        var number = value ?? throw new ConfigException("gather.json 节点 " + id + " 缺 " + key);
        if (number < 1)
        {
            throw new ConfigException("gather.json 节点 " + id + " 的 " + key + " 必须大于 0，现在是 " + number);
        }

        return number;
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    sealed record Wire
    {
        [JsonPropertyName("version")] public int? Version { get; init; }
        [JsonPropertyName("character_upstream")] public string? CharacterUpstream { get; init; }
        [JsonPropertyName("nodes")] public List<NodeWire>? Nodes { get; init; }
    }

    sealed record NodeWire
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("region_id")] public string? RegionId { get; init; }
        [JsonPropertyName("item_id")] public string? ItemId { get; init; }
        [JsonPropertyName("count")] public int? Count { get; init; }
        [JsonPropertyName("bag_slot")] public int? BagSlot { get; init; }
        [JsonPropertyName("respawn_s")] public int? RespawnSeconds { get; init; }
    }
}
