using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// A9 属性加点的唯一配置正本（方案 8.2 的四属性表、14.6 第 16 行 A9）。
/// 方案只写定四属性各管什么，既没写单属性上限、也没写洗点怎么收费，所以这里只有两格：
///   · per_attr_cap —— 单个属性最多能加多少点；0 表示不设单属性上限（只受手上点数约束）；
///   · free_respec  —— 本版「不做免费洗点」，必须 false。填 true 直接起不来：
///     洗点要真开就单独派卡，改表、改事务、改拒绝码，不在这份配置里悄悄放开。
/// 四属性的基础值不在这里：那是 config/classes.json 的职业表（力量 / 敏捷 / 精神 / 体力），
/// 角色服只读它算「基础 + 加点」，战斗服按同一份表加同一份点数，不落第二份属性数值。
/// 未知键在加载期直接拒绝，省得以后从这份配置里长出掉落、金币、战力一类的字段。
/// </summary>
public sealed class AttrPointTable
{
    public const string WrittenSchema = "attr-point-table/1";

    /// <summary>加点只认这四个键。顺序就是屏上的顺序（力量 / 敏捷 / 精神 / 体力）。</summary>
    public static readonly string[] Attributes = ["strength", "agility", "spirit", "vitality"];

    static readonly string[] AllowedTopKeys =
        ["schema", "version", "note", "pending", "per_attr_cap", "free_respec"];

    static readonly string[] RequiredTopKeys = ["schema", "version", "per_attr_cap", "free_respec"];

    /// <summary>
    /// 加点与装备属性门槛的校验上限。不是玩法数值，只用来挡写错的配置：
    /// A8 一段最多给 50 点、十一大境界合计也就几百点，十万以上一定是写错了。
    /// 真实上限在 attr_points.json 的 per_attr_cap；装备门槛的上限也借这一个闸。
    /// </summary>
    public const int MaxAttrValue = 100000;

    public int Version { get; }
    public bool Pending { get; }
    public int PerAttrCap { get; }
    public bool FreeRespec { get; }

    AttrPointTable(int version, bool pending, int perAttrCap, bool freeRespec)
    {
        Version = version;
        Pending = pending;
        PerAttrCap = perAttrCap;
        FreeRespec = freeRespec;
    }

    /// <summary>这个键是不是四属性之一。</summary>
    public static bool IsAttribute(string key) => Array.IndexOf(Attributes, key) >= 0;

    /// <summary>读表并与真实启动共用同一条校验链；坏配置直接 config_error。</summary>
    public static AttrPointTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配置文件 " + path);
        }

        var text = File.ReadAllText(path);
        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(text, JsonOptions)
                ?? throw new ConfigException("配置是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配置不是合法 JSON：" + ex.Message);
        }

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException("attr_points.json 顶层必须是一个对象");
        }

        foreach (var key in RequiredTopKeys)
        {
            if (!root.TryGetProperty(key, out _))
            {
                throw new ConfigException("attr_points.json 缺 " + key);
            }
        }

        foreach (var property in root.EnumerateObject())
        {
            if (Array.IndexOf(AllowedTopKeys, property.Name) < 0)
            {
                throw new ConfigException("attr_points.json 不认识的键：" + property.Name);
            }
        }

        if (!string.Equals(wire.Schema, WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException("attr_points.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version is null or < 1)
        {
            throw new ConfigException("attr_points.json 的 version 必须是正数");
        }

        if (wire.PerAttrCap is null || wire.PerAttrCap < 0 || wire.PerAttrCap > MaxAttrValue)
        {
            throw new ConfigException(
                "attr_points.json 的 per_attr_cap 要在 0.." + MaxAttrValue + " 之间（0 = 不限）");
        }

        if (wire.FreeRespec is null)
        {
            throw new ConfigException("attr_points.json 缺 free_respec");
        }

        // 本版不做免费洗点：填 true 就是玩法被改了，起不来。
        if (wire.FreeRespec.Value)
        {
            throw new ConfigException("attr_points.json 的 free_respec 必须是 false（本版不做免费洗点）");
        }

        return new AttrPointTable(
            wire.Version.Value, wire.Pending ?? false, wire.PerAttrCap.Value, false);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        [JsonPropertyName("version")]
        public int? Version { get; init; }

        [JsonPropertyName("pending")]
        public bool? Pending { get; init; }

        [JsonPropertyName("per_attr_cap")]
        public int? PerAttrCap { get; init; }

        [JsonPropertyName("free_respec")]
        public bool? FreeRespec { get; init; }
    }
}
