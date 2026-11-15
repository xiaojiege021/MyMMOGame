using System.Text.Json;

namespace World;

/// <summary>
/// C2 采集节点铺开的可选配置正本（config/gather_spread.json）。
///
/// 方案 9.5 写定「矿和药的节点长在大世界里，按地表的境界区域刷新；元婴之后，
/// 更高一级的矿和药随该大境界的版本一起开放」。P1-3b 已经把「矿脉给矿石、药草给药材、
/// 灵石矿脉不许个人采、采集不耗疲劳、产物由角色服入背包」这些规则做完，本表只补节点内容：
/// 地表境界区域的正本复用 config/flight.json（P1-3c），已开放境界也只认它的 realms，
/// 不另读一份境界开关，更不在世界服里自己判定角色境界。
///
/// 这张表是可选的：老卡的临时配置目录里没有它，世界服照常起来（缺表降级），
/// 因此 P1-3b 的四个基准节点、启动日志和老断言一个字都不用改。
/// 缺表 / 坏表不会把分线、飞行、采集这些老链路一起拖垮——坏表照样起不来。
/// </summary>
public sealed class GatherSpreadTable
{
    public const string Schema = "gather-spread/1";
    public const int Version = 1;
    public const string DefaultItemsConfig = "config/items.json";

    static readonly HashSet<string> TopKeys = new(StringComparer.Ordinal)
    {
        "schema", "version", "pending", "note", "items_config", "nodes",
    };

    static readonly HashSet<string> NodeKeys = new(StringComparer.Ordinal)
    {
        "id", "kind", "region_id", "realm", "item_id", "count", "bag_slot", "respawn_s",
    };

    GatherSpreadTable(IReadOnlyList<GatherNode> nodes, string itemsConfig)
    {
        Nodes = nodes;
        ItemsConfig = itemsConfig;
    }

    public IReadOnlyList<GatherNode> Nodes { get; }

    public string ItemsConfig { get; }

    public int RegionCount => Nodes.Select(n => n.RegionId).Distinct(StringComparer.Ordinal).Count();

    public string Realms => string.Join(",", Nodes.Select(n => n.Realm).Distinct(StringComparer.Ordinal));

    /// <summary>缺这份可选表就返回 null，世界服按 P1-3b 的四个基准节点照常启动。</summary>
    public static GatherSpreadTable? LoadIfPresent(
        string path,
        IReadOnlySet<string> regions,
        FlightTable flights,
        IReadOnlyList<GatherNode> baseNodes)
        => File.Exists(path) ? Load(path, regions, flights, baseNodes) : null;

    public static GatherSpreadTable Load(
        string path,
        IReadOnlySet<string> regions,
        FlightTable flights,
        IReadOnlyList<GatherNode> baseNodes)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到采集铺开配置 " + path);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigException("采集铺开配置不是合法 JSON：" + ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("gather_spread.json 顶层必须是对象");
            }

            RequireKeys(root, TopKeys, "gather_spread.json 顶层");

            var schema = Str(root, "schema");
            if (schema != Schema)
            {
                throw new ConfigException("gather_spread.json 的 schema 必须是 " + Schema);
            }

            var version = Num(root, "version");
            if (version != Version)
            {
                throw new ConfigException("gather_spread.json 的 version 必须是 " + Version);
            }

            var itemsConfig = (Str(root, "items_config") ?? "").Trim();
            if (itemsConfig.Length == 0)
            {
                itemsConfig = DefaultItemsConfig;
            }

            if (!root.TryGetProperty("nodes", out var nodeArray) || nodeArray.ValueKind != JsonValueKind.Array
                || nodeArray.GetArrayLength() == 0)
            {
                throw new ConfigException("gather_spread.json 缺 nodes");
            }

            var items = LoadItems(itemsConfig);
            var baseIds = baseNodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var nodes = new List<GatherNode>(nodeArray.GetArrayLength());

            foreach (var raw in nodeArray.EnumerateArray())
            {
                if (raw.ValueKind != JsonValueKind.Object)
                {
                    throw new ConfigException("gather_spread.json 的 nodes 里有一条不是对象");
                }

                var id = (Str(raw, "id") ?? "").Trim();
                if (id.Length == 0)
                {
                    throw new ConfigException("gather_spread.json 里有空节点编号");
                }

                RequireKeys(raw, NodeKeys, "gather_spread.json 节点 " + id);

                if (!seen.Add(id))
                {
                    throw new ConfigException("gather_spread.json 里有重复节点编号 " + id);
                }

                if (baseIds.Contains(id))
                {
                    throw new ConfigException("gather_spread.json 的节点编号与基准表重复：" + id);
                }

                var kind = (Str(raw, "kind") ?? "").Trim();
                if (kind is not (GatherTable.Ore or GatherTable.Herb))
                {
                    // 灵石矿脉只在 gather.json 一处，铺开表里把它配出来等于从后门开个人采灵石。
                    throw new ConfigException(
                        "gather_spread.json 只许配 ore / herb，不许配 spirit_stone 或其它 kind：" + id);
                }

                var region = (Str(raw, "region_id") ?? "").Trim();
                if (region.Length == 0)
                {
                    throw new ConfigException("gather_spread.json 节点 " + id + " 缺 region_id");
                }

                if (!regions.Contains(region))
                {
                    throw new ConfigException("gather_spread.json 节点 " + id + " 的地点不在 world.json 里：" + region);
                }

                var realm = (Str(raw, "realm") ?? "").Trim();
                if (realm.Length == 0)
                {
                    throw new ConfigException("gather_spread.json 节点 " + id + " 缺 realm");
                }

                // 「元婴之后随大境界版本一起开放」：flight.json 的 realms 就是已开放段，
                // 元婴及以后还没进这张表，配出来直接起不来，不需要改代码也不用判玩家境界。
                if (!flights.Realms.Contains(realm, StringComparer.Ordinal))
                {
                    throw new ConfigException("gather_spread.json 节点 " + id + " 的 realm 不在已开放境界里：" + realm);
                }

                // 节点长在哪块地表，就按那块地表的境界区域刷新，不自己另起一套分档。
                var required = flights.RequiredFor(region);
                if (required is null || !string.Equals(required, realm, StringComparison.Ordinal))
                {
                    throw new ConfigException("gather_spread.json 节点 " + id + " 的 realm 与地表 "
                        + region + " 的境界区域不符：应是 " + (required ?? "无") + "，配了 " + realm);
                }

                var itemId = (Str(raw, "item_id") ?? "").Trim();
                if (itemId.Length == 0)
                {
                    throw new ConfigException("gather_spread.json 节点 " + id + " 缺 item_id");
                }

                if (!items.TryGetValue(itemId, out var facts))
                {
                    throw new ConfigException("gather_spread.json 节点 " + id + " 的 item_id 不在 items.json 里：" + itemId);
                }

                // 方案 9.5：矿、药可以玩家交易；绑定的、非材料的物品不能当采集产物。
                if (facts.Kind != "material" || facts.Bind != "none")
                {
                    throw new ConfigException("gather_spread.json 节点 " + id
                        + " 的 item_id 不是可交易的 material：" + itemId);
                }

                var count = Positive(Num(raw, "count"), "count", id);
                if (count > facts.StackMax)
                {
                    throw new ConfigException("gather_spread.json 节点 " + id
                        + " 的 count 超过 " + itemId + " 的 stack_max " + facts.StackMax);
                }

                var slot = Positive(Num(raw, "bag_slot"), "bag_slot", id);
                var respawn = Positive(Num(raw, "respawn_s"), "respawn_s", id);

                nodes.Add(new GatherNode(id, kind, region, itemId, count, slot, respawn, realm));
            }

            // 「铺开」是结构保证：基准表 + 本表合并之后，每一块已配置地表都得有矿也有药。
            var all = baseNodes.Concat(nodes).ToList();
            foreach (var region in regions)
            {
                if (!all.Any(n => n.RegionId == region && n.Kind == GatherTable.Ore)
                    || !all.Any(n => n.RegionId == region && n.Kind == GatherTable.Herb))
                {
                    throw new ConfigException("gather_spread.json 铺开后地表 " + region + " 缺 ore 或 herb 节点");
                }
            }

            return new GatherSpreadTable(nodes, itemsConfig);
        }
    }

    static Dictionary<string, ItemFacts> LoadItems(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到物品表 " + path);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigException("物品表不是合法 JSON：" + ex.Message);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("items", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("items.json 缺 items");
            }

            var result = new Dictionary<string, ItemFacts>(StringComparer.Ordinal);
            foreach (var raw in array.EnumerateArray())
            {
                var id = (Str(raw, "id") ?? "").Trim();
                if (id.Length == 0)
                {
                    continue;
                }

                var stack = Num(raw, "stack_max") ?? 0;
                result[id] = new ItemFacts(
                    (Str(raw, "kind") ?? "").Trim(),
                    (Str(raw, "bind") ?? "").Trim(),
                    stack);
            }

            return result;
        }
    }

    static void RequireKeys(JsonElement obj, HashSet<string> allowed, string where)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ConfigException(where + " 出现不认识的键 " + property.Name);
            }
        }
    }

    static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static int? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    static int Positive(int? value, string key, string id)
    {
        var number = value ?? throw new ConfigException("gather_spread.json 节点 " + id + " 缺 " + key);
        if (number < 1)
        {
            throw new ConfigException("gather_spread.json 节点 " + id + " 的 " + key + " 必须大于 0，现在是 " + number);
        }

        return number;
    }

    sealed record ItemFacts(string Kind, string Bind, int StackMax);
}
