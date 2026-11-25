using System.Text.Json;

namespace Character;

/// <summary>
/// 一个职业的四条基础属性（力量 / 敏捷 / 精神 / 体力），只从 config/classes.json 读，
/// 不落第二份数值。战斗服读同一份文件的同一段算生命 / 蓝量 / 攻击，角色服只在
/// 「加点之后够不够装备门槛」这一处用到它（A9：装备门槛读加点）。
/// </summary>
public readonly record struct ClassAttributes(int Strength, int Agility, int Spirit, int Vitality)
{
    public static readonly ClassAttributes None = default;

    /// <summary>按加点键取一条基础值；不认识的键回 0（调用方已经先校验过键）。</summary>
    public long Get(string key) => key switch
    {
        "strength" => Strength,
        "agility" => Agility,
        "spirit" => Spirit,
        "vitality" => Vitality,
        _ => 0,
    };

    /// <summary>基础 + 加点。加点是玩家手动加的那部分，存在 character_attr_points 里。</summary>
    public ClassAttributes Plus(int strength, int agility, int spirit, int vitality) =>
        new(Strength + strength, Agility + agility, Spirit + spirit, Vitality + vitality);
}

/// <summary>
/// 建号只认配置里有的职业（config/classes.json，与战斗服同一份）。
/// A9 起这里连带读四属性基础值：装备的属性门槛按「基础 + 加点」判，
/// 属性数值仍只在职业表一处，角色服不另存一份。
/// </summary>
public sealed class ClassIds
{
    readonly Dictionary<string, ClassAttributes> _byId;

    public ClassIds(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到职业表：" + path);
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            _byId = new Dictionary<string, ClassAttributes>(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.GetProperty("classes").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var attributes = item.GetProperty("attributes");
                var strength = attributes.GetProperty("strength").GetInt32();
                var agility = attributes.GetProperty("agility").GetInt32();
                var spirit = attributes.GetProperty("spirit").GetInt32();
                var vitality = attributes.GetProperty("vitality").GetInt32();
                // 与战斗服同一条闸：属性不能是负数、体力必须大于 0（生命是体力 × 表级系数）。
                if (strength < 0 || agility < 0 || spirit < 0 || vitality <= 0)
                {
                    throw new ConfigException(
                        "职业 " + id.Trim() + " 的属性不能是负数，体力必须大于 0");
                }

                _byId[id.Trim()] = new ClassAttributes(strength, agility, spirit, vitality);
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ConfigException("职业表读不出来：" + ex.Message);
        }

        if (_byId.Count == 0)
        {
            throw new ConfigException("职业表里一个职业都没有：" + path);
        }
    }

    public bool Contains(string classId) => _byId.ContainsKey(classId);

    /// <summary>这个职业的四属性基础值；职业不在表里回 null。</summary>
    public ClassAttributes? AttributesOf(string classId) =>
        _byId.TryGetValue(classId, out var attributes) ? attributes : null;
}
