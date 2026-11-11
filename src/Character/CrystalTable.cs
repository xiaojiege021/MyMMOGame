using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>一颗结晶的写定类别与待定固定值。固定值只作展示，不参与本卡的装上 / 拆下判定。</summary>
public sealed record CrystalDef(string Id, string Category, long FixedValue);

/// <summary>
/// config/crystal.json 是能量结晶的唯一正本。方案第 9 章写定的三类和「部位限定、
/// 可拆卸复用」在这里逐条校验；没写定的部位映射、镶嵌位数量、结晶固定值只从表里读，
/// 代码里不写第二套数字。角色服是唯一读表方，改配置重启生效。
/// </summary>
public sealed class CrystalTable
{
    readonly Dictionary<string, CrystalDef> crystals = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> equipmentParts = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string>> categoryParts = new(StringComparer.Ordinal);

    public int Version { get; }

    /// <summary>方案写定的三类，顺序固定，配置里换顺序或改名都起不来。</summary>
    public static readonly string[] WrittenCategories = ["attack", "defense", "utility"];

    /// <summary>每件装备几个镶嵌位。方案没写定，只从配置读。</summary>
    public int SocketCount { get; }

    public IReadOnlyCollection<CrystalDef> Crystals => crystals.Values;

    public IReadOnlyDictionary<string, string> EquipmentParts => equipmentParts;

    public IReadOnlyDictionary<string, List<string>> CategoryParts => categoryParts;

    CrystalTable(int version, int socketCount)
    {
        Version = version;
        SocketCount = socketCount;
    }

    public CrystalDef? Find(string crystalId) => crystals.GetValueOrDefault(crystalId);

    /// <summary>装备属于哪个部位。配置漏了装备就起不来，所以这里正常不会是 null。</summary>
    public string? PartOf(string equipmentItemId) => equipmentParts.GetValueOrDefault(equipmentItemId);

    /// <summary>方案 9 章的「部位限定」：这一类结晶能不能装到这个部位。</summary>
    public bool Allows(string category, string part) =>
        categoryParts.TryGetValue(category, out var parts) && parts.Contains(part, StringComparer.Ordinal);

    public static CrystalTable Load(string path, ItemTable items)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到结晶表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("结晶表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("结晶表不是合法 JSON：" + ex.Message);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("crystal.json 的 version 必须大于 0");
        }

        // 方案第 9 章写死「攻击、防御、功能三类」：多一类、少一类、换名字都是改玩法。
        var categories = wire.Categories ?? throw new ConfigException("crystal.json 缺 categories");
        if (categories.Count != WrittenCategories.Length
            || categories.Any(c => c is null || string.IsNullOrWhiteSpace(c))
            || !categories.Select(c => c!.Trim()).OrderBy(c => c, StringComparer.Ordinal)
                .SequenceEqual(WrittenCategories.OrderBy(c => c, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new ConfigException("crystal.json 的 categories 必须正好是方案写定的 attack / defense / utility");
        }

        // 镶嵌位数量方案没写定，只要求是个正数；具体几个由配置说了算。
        var socketCount = wire.SocketCount
            ?? throw new ConfigException("crystal.json 缺 socket_count");
        if (socketCount < 1)
        {
            throw new ConfigException("crystal.json 的 socket_count 必须大于 0");
        }

        var table = new CrystalTable(wire.Version, socketCount);

        // 装备部位归属：方案没写定，暂由本表登记；items.json 里每一件装备都要有归属，
        // 漏一件就起不来，免得上线后出现「这件装备不知道算哪个部位」的静默放行。
        var parts = wire.EquipmentParts
            ?? throw new ConfigException("crystal.json 缺 equipment_parts");
        foreach (var (itemId, part) in parts)
        {
            var id = (itemId ?? "").Trim();
            var name = (part ?? "").Trim();
            if (id.Length == 0 || name.Length == 0)
            {
                throw new ConfigException("crystal.json 的 equipment_parts 有空键或空部位");
            }

            var item = items.Find(id);
            if (item is null)
            {
                throw new ConfigException("crystal.json 的 equipment_parts 里有不在物品表的编号：" + id);
            }

            if (!string.Equals(item.Kind, "equipment", StringComparison.Ordinal))
            {
                throw new ConfigException("crystal.json 的 equipment_parts 只能登记装备：" + id);
            }

            table.equipmentParts[id] = name;
        }

        foreach (var item in items.All.Where(i => string.Equals(i.Kind, "equipment", StringComparison.Ordinal)))
        {
            if (!table.equipmentParts.ContainsKey(item.Id))
            {
                throw new ConfigException("crystal.json 的 equipment_parts 漏了装备部位：" + item.Id);
            }
        }

        // 类别允许哪些部位：键必须正好是写定的三类，部位名要真的在装备部位表里出现过。
        var byCategory = wire.CategoryParts
            ?? throw new ConfigException("crystal.json 缺 category_parts");
        // 先逐类查缺，再查多：少写一类时报出具体是哪一类，别被「必须正好三类」
        // 这个笼统错误盖过去。
        var knownParts = table.equipmentParts.Values.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (var category in WrittenCategories)
        {
            if (!byCategory.TryGetValue(category, out var allowed) || allowed is null || allowed.Count == 0)
            {
                throw new ConfigException("crystal.json 的 category_parts 缺 " + category + " 的部位");
            }

            var list = new List<string>();
            foreach (var raw in allowed)
            {
                var part = (raw ?? "").Trim();
                if (part.Length == 0)
                {
                    throw new ConfigException("crystal.json 的 " + category + " 部位列表里有空值");
                }

                if (!knownParts.Contains(part))
                {
                    throw new ConfigException("crystal.json 的 " + category
                        + " 写了没有装备归属的部位：" + part);
                }

                if (list.Contains(part, StringComparer.Ordinal))
                {
                    throw new ConfigException("crystal.json 的 " + category + " 部位重复：" + part);
                }

                list.Add(part);
            }

            table.categoryParts[category] = list;
        }

        foreach (var key in byCategory.Keys)
        {
            if (!WrittenCategories.Contains(key, StringComparer.Ordinal))
            {
                throw new ConfigException("crystal.json 的 category_parts 必须正好三类");
            }
        }

        // 结晶条目：编号要在 items.json 里、kind 必须是 crystal、类别属于写定三类，
        // 固定值大于 0；三类各至少有一颗，不然「部位不对」没法逐类核对。
        var defs = wire.Crystals ?? throw new ConfigException("crystal.json 缺 crystals");
        if (defs.Count == 0)
        {
            throw new ConfigException("crystal.json 一颗结晶都没有");
        }

        foreach (var def in defs)
        {
            var id = (def.Id ?? "").Trim();
            var category = (def.Category ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("crystal.json 的 crystals 有条目缺 id");
            }

            if (table.crystals.ContainsKey(id))
            {
                throw new ConfigException("crystal.json 的结晶编号重复：" + id);
            }

            var item = items.Find(id);
            if (item is null)
            {
                throw new ConfigException("crystal.json 的结晶不在 items.json 里：" + id);
            }

            if (!string.Equals(item.Kind, "crystal", StringComparison.Ordinal))
            {
                throw new ConfigException("crystal.json 的结晶必须是 kind=crystal：" + id);
            }

            if (!WrittenCategories.Contains(category, StringComparer.Ordinal))
            {
                throw new ConfigException("crystal.json 的结晶类别不在写定三类里：" + category);
            }

            var fixedValue = def.FixedValue
                ?? throw new ConfigException("crystal.json 的结晶缺 fixed_value：" + id);
            if (fixedValue < 1)
            {
                throw new ConfigException("crystal.json 的结晶 fixed_value 必须大于 0：" + id);
            }

            table.crystals[id] = new CrystalDef(id, category, fixedValue);
        }

        foreach (var category in WrittenCategories)
        {
            if (!table.crystals.Values.Any(c => string.Equals(c.Category, category, StringComparison.Ordinal)))
            {
                throw new ConfigException("crystal.json 的 " + category + " 类一颗结晶都没有");
            }
        }

        return table;
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("categories")]
        public List<string?>? Categories { get; init; }

        [JsonPropertyName("socket_count")]
        public int? SocketCount { get; init; }

        [JsonPropertyName("equipment_parts")]
        public Dictionary<string, string?>? EquipmentParts { get; init; }

        [JsonPropertyName("category_parts")]
        public Dictionary<string, List<string?>?>? CategoryParts { get; init; }

        [JsonPropertyName("crystals")]
        public List<CrystalItem>? Crystals { get; init; }
    }

    sealed record CrystalItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("category")]
        public string? Category { get; init; }

        [JsonPropertyName("fixed_value")]
        public long? FixedValue { get; init; }
    }
}
