using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>物品到账时怎么绑定。来自方案：副本装备首次拾取绑定，商城道具到账绑定。</summary>
public enum BindRule
{
    /// <summary>不绑，可以正常流通。</summary>
    None = 0,

    /// <summary>拾取即绑：副本掉落的装备。</summary>
    OnPickup = 1,

    /// <summary>到账即绑：商城买的符、券、卷轴、经验道具。</summary>
    Always = 2,
}

public sealed record ItemDef(
    string Id, string Name, string Kind, int StackMax, BindRule Bind,
    string WeaponKind = "", int RequireLevel = 0)
{
    /// <summary>这一件到账时是不是绑定。绑定物品不能直接交易。</summary>
    public bool BindsOnArrival => Bind != BindRule.None;

    /// <summary>是不是武器。没有 weapon_kind 的装备（衣服、戒指）不受武器锁约束。</summary>
    public bool IsWeapon => WeaponKind.Length > 0;
}

public sealed class ItemTable
{
    readonly Dictionary<string, ItemDef> _byId = new(StringComparer.Ordinal);

    public int Version { get; }

    public IReadOnlyCollection<ItemDef> All => _byId.Values;

    public ItemDef? Find(string id) => _byId.GetValueOrDefault(id);

    ItemTable(Dictionary<string, ItemDef> byId, int version)
    {
        _byId = byId;
        Version = version;
    }

    public static ItemTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到物品表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("物品表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("物品表不是合法 JSON：" + ex.Message);
        }

        var map = new Dictionary<string, ItemDef>(StringComparer.Ordinal);

        foreach (var item in wire.Items)
        {
            var id = (item.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("items.json 里有条目缺 id");
            }

            var stackMax = item.StackMax is > 0 ? item.StackMax.Value : 1;
            if (item.Bind is null)
            {
                throw new ConfigException("物品 " + id + " 没有 bind 规则");
            }

            var weaponKind = (item.WeaponKind ?? "").Trim();
            var requireLevel = item.RequireLevel ?? 0;
            if (requireLevel < 0 || requireLevel > 100)
            {
                throw new ConfigException("物品 " + id + " 的 require_level 要在 0..100 之间");
            }
            if (weaponKind.Length > 0 && requireLevel == 0)
            {
                throw new ConfigException("武器 " + id + " 必须写 require_level，不写就是任意等级都能穿");
            }
            map[id] = new ItemDef(
                id, (item.Name ?? id).Trim(), (item.Kind ?? "material").Trim(), stackMax,
                item.Bind.Value, weaponKind, requireLevel);
        }

        if (map.Count == 0)
        {
            throw new ConfigException("items.json 里一件物品都没有");
        }

        return new ItemTable(map, wire.Version);
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

        [JsonPropertyName("items")]
        public List<ItemItem> Items { get; init; } = [];
    }

    sealed record ItemItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("weapon_kind")]
        public string? WeaponKind { get; init; }

        [JsonPropertyName("require_level")]
        public int? RequireLevel { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("stack_max")]
        public int? StackMax { get; init; }

        [JsonPropertyName("bind")]
        public BindRule? Bind { get; init; }
    }
}
