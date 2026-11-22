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

/// <summary>
/// 一件物品的静态定义。价格、发货、掉落不在这里：那些只在商店 / 配方 / 掉落表。
/// grade / set_id / desc 是 K5 加的展示与品阶字段，代码只读，不按物品编号分支。
/// 图标地址不进配置（第 17 节 17.1）：客户端按 icon/<物品编号> 自己拼，服务端不认这一格。
/// </summary>
public sealed record ItemDef(
    string Id, string Name, string Kind, int StackMax, BindRule Bind,
    string WeaponKind = "", int RequireLevel = 0,
    string Grade = "", string SetId = "", string Desc = "",
    string Slot = "", int Attack = 0)
{
    /// <summary>有没有品阶。材料、结晶、称号、外观可以不写。</summary>
    public bool HasGrade => Grade.Length > 0;

    /// <summary>这一件到账时是不是绑定。绑定物品不能直接交易。</summary>
    public bool BindsOnArrival => Bind != BindRule.None;

    /// <summary>是不是武器。没有 weapon_kind 的装备（衣服、戒指）不受武器锁约束。</summary>
    public bool IsWeapon => WeaponKind.Length > 0;

    /// <summary>B9：这件是不是装备（决定能不能穿）。部位在 Slot。</summary>
    public bool IsEquipment => string.Equals(Kind, "equipment", StringComparison.Ordinal);
}

public sealed class ItemTable
{
    // 配置设计 2 节写定的六档；装备必须有品阶，其余种类可以先空着。
    static readonly HashSet<string> Grades = new(StringComparer.Ordinal)
    {
        "white", "green", "blue", "purple", "orange", "red",
    };

    readonly Dictionary<string, ItemDef> _byId = new(StringComparer.Ordinal);

    /// <summary>
    /// B9：三个部位。同部位只许穿一件；这是装备定义里的必填项，不是可调数值，
    /// 所以不进 config 的数值表，也不许在代码里按物品编号分支。
    /// </summary>
    public static readonly string[] EquipSlots = ["weapon", "armor", "accessory"];

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

            // K5：三个展示 / 品阶字段。加载期硬校验，坏配置直接 config_error，
            // 不静默落到空串上——空串会让背包与后续 B2 的品阶逻辑当成「没配」。
            // 图标地址不在这里：v1.172 起 icon 不进配置（第 17 节 17.1）。
            var kind = (item.Kind ?? "material").Trim();
            var grade = (item.Grade ?? "").Trim();
            var setId = (item.SetId ?? "").Trim();
            var desc = (item.Desc ?? "").Trim();
            if (grade.Length > 0 && !Grades.Contains(grade))
            {
                throw new ConfigException("物品 " + id + " 的 grade 不认识：" + grade);
            }
            if (string.Equals(kind, "equipment", StringComparison.Ordinal) && grade.Length == 0)
            {
                throw new ConfigException("装备 " + id + " 必须写 grade（白 / 绿 / 蓝 / 紫 / 橙 / 红）");
            }
            if (setId.Length > 0)
            {
                if (!string.Equals(kind, "equipment", StringComparison.Ordinal))
                {
                    throw new ConfigException("只有装备能写 set_id：" + id);
                }
                if (!IsSetId(setId))
                {
                    throw new ConfigException("物品 " + id + " 的 set_id 要写成 set_<小写编号>：" + setId);
                }
            }
            if (desc.Length == 0)
            {
                throw new ConfigException("物品 " + id + " 缺 desc");
            }

            // B9：部位与装备固定攻击。装备必须写部位（穿在哪一格由它决定），
            // 非装备不许写；攻击只能是装备上的，且不许为负。
            var slot = (item.Slot ?? "").Trim();
            var attack = item.Attack ?? 0;
            if (kind == "equipment")
            {
                if (Array.IndexOf(EquipSlots, slot) < 0)
                {
                    throw new ConfigException(
                        "装备 " + id + " 的 slot 必须是 weapon / armor / accessory：" + (slot.Length == 0 ? "（没写）" : slot));
                }
            }
            else
            {
                if (slot.Length > 0)
                {
                    throw new ConfigException("只有装备能写 slot：" + id);
                }
                if (attack != 0)
                {
                    throw new ConfigException("只有装备能写 attack：" + id);
                }
            }

            if (attack < 0)
            {
                throw new ConfigException("装备 " + id + " 的 attack 不能是负数");
            }

            if (map.ContainsKey(id))
            {
                // 配置设计 10.1：同一文件内 id 唯一。重复时以前是后者覆盖前者，
                // 看起来能起来、实际少一件，属于 K4 要拦的「引用对不上」。
                throw new ConfigException("items.json 的物品编号重复：" + id);
            }
            map[id] = new ItemDef(
                id, (item.Name ?? id).Trim(), kind, stackMax,
                item.Bind.Value, weaponKind, requireLevel, grade, setId, desc, slot, attack);
        }

        if (map.Count == 0)
        {
            throw new ConfigException("items.json 里一件物品都没有");
        }

        return new ItemTable(map, wire.Version);
    }

    /// <summary>套装编号：set_ 开头，后面只许小写字母、数字、下划线。B2 的 SetTable 也用它，只留一份。</summary>
    internal static bool IsSetId(string value)
    {
        if (!value.StartsWith("set_", StringComparison.Ordinal) || value.Length <= 4)
        {
            return false;
        }
        foreach (var c in value.AsSpan(4))
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
            {
                return false;
            }
        }
        return true;
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

        [JsonPropertyName("grade")]
        public string? Grade { get; init; }

        [JsonPropertyName("set_id")]
        public string? SetId { get; init; }

        [JsonPropertyName("desc")]
        public string? Desc { get; init; }

        [JsonPropertyName("slot")]
        public string? Slot { get; init; }

        [JsonPropertyName("attack")]
        public int? Attack { get; init; }
    }
}
