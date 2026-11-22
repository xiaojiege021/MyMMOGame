using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>一档品阶。顺序、等级带、基准攻击都只在这份配置里，代码不写第二份。</summary>
public sealed record GradeDef(string Id, int Order, int RequireLevel, long BaseAttack);

/// <summary>
/// config/grades.json —— 装备品阶的唯一正本（B2）。
/// 方案 `#s8` 第 1 层写定六档与顺序（白 → 绿（精良）→ 蓝 → 紫 → 橙 → 红），
/// 写定「同品阶装备通用，穿戴仍看等级和职业」；方案没给每档的基准攻击与等级带，
/// 那两列是待定值，只在这里记一次。
///
/// 代码只做三件事：认六档、给出顺序与基准、在加载期逐件校验物品表
/// （装备的 grade 必须在表里，attack 不得超过本档基准，require_level 不得超过本档等级带）。
/// 不按物品编号分支，也不在这里算套装加成（那是 sets.json 与 SetTable）。
/// </summary>
public sealed class GradeTable
{
    public const string WrittenSchema = "grade-table/1";

    /// <summary>方案写定的六档编号；这是枚举而不是可调数值，所以不进 config 的数值表。</summary>
    static readonly string[] WrittenIds = ["white", "green", "blue", "purple", "orange", "red"];

    readonly Dictionary<string, GradeDef> _byId = new(StringComparer.Ordinal);

    public int Version { get; }

    /// <summary>按 order 排好的六档，屏与只读入口都按这个顺序出。</summary>
    public IReadOnlyList<GradeDef> Ordered { get; }

    GradeTable(Dictionary<string, GradeDef> byId, IReadOnlyList<GradeDef> ordered, int version)
    {
        _byId = byId;
        Ordered = ordered;
        Version = version;
    }

    public GradeDef? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>认不出来返回 0（比任何一档都低），调用方不必自己判空。</summary>
    public int OrderOf(string id) => _byId.TryGetValue(id, out var def) ? def.Order : 0;

    public long BaseAttackOf(string id) => _byId.TryGetValue(id, out var def) ? def.BaseAttack : 0;

    /// <summary>物品编号 → 品阶；非装备或没写品阶的不进表。</summary>
    public Dictionary<string, string> ItemGrades(ItemTable items)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items.All)
        {
            if (item.IsEquipment && item.HasGrade)
            {
                map[item.Id] = item.Grade;
            }
        }

        return map;
    }

    public static GradeTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到品阶表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), Options)
                ?? throw new ConfigException("品阶表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("品阶表不是合法 JSON：" + ex.Message);
        }

        if (wire.Schema != WrittenSchema)
        {
            throw new ConfigException(path + "：schema 必须是 " + WrittenSchema);
        }

        if (wire.Grades is null || wire.Grades.Count != WrittenIds.Length)
        {
            throw new ConfigException(path + "：grades 必须正好 " + WrittenIds.Length + " 档");
        }

        var byId = new Dictionary<string, GradeDef>(StringComparer.Ordinal);
        foreach (var raw in wire.Grades)
        {
            var id = (raw.Id ?? "").Trim();
            if (Array.IndexOf(WrittenIds, id) < 0)
            {
                throw new ConfigException(path + "：不认识的品阶 " + id);
            }

            if (byId.ContainsKey(id))
            {
                throw new ConfigException(path + "：品阶重复 " + id);
            }

            var order = raw.Order ?? 0;
            if (order < 1 || order > WrittenIds.Length)
            {
                throw new ConfigException(path + "：品阶 " + id + " 的 order 要在 1.." + WrittenIds.Length + " 之间");
            }

            var level = raw.RequireLevel ?? 0;
            if (level is < 1 or > 100)
            {
                throw new ConfigException(path + "：品阶 " + id + " 的 require_level 要在 1..100 之间");
            }

            var attack = raw.BaseAttack ?? 0;
            if (attack < 1)
            {
                throw new ConfigException(path + "：品阶 " + id + " 的 base_attack 必须大于 0");
            }

            byId[id] = new GradeDef(id, order, level, attack);
        }

        var ordered = byId.Values.OrderBy(g => g.Order).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Order != i + 1)
            {
                throw new ConfigException(path + "：order 必须是 1.." + WrittenIds.Length + " 且不重复");
            }

            if (i > 0 && ordered[i].RequireLevel <= ordered[i - 1].RequireLevel)
            {
                throw new ConfigException(path + "：等级带必须随品阶严格递增（" + ordered[i].Id + "）");
            }

            if (i > 0 && ordered[i].BaseAttack <= ordered[i - 1].BaseAttack)
            {
                throw new ConfigException(path + "：基准攻击必须随品阶严格递增（" + ordered[i].Id + "）");
            }
        }

        return new GradeTable(byId, ordered, wire.Version ?? 0);
    }

    /// <summary>
    /// 加载期逐件对账：装备必须有本表认得的品阶，攻击不得超过本档基准，
    /// 等级带不得超过本档上限。老装备（试炼 / 行商）的 require_level 可以是 0（不限级），
    /// 只要不超过上限；attack 同样只判上限，不强制等于基准——基准是天花板，不是唯一值。
    /// </summary>
    public void CheckItems(ItemTable items)
    {
        foreach (var item in items.All)
        {
            if (!item.IsEquipment)
            {
                continue;
            }

            if (!item.HasGrade)
            {
                throw new ConfigException("装备 " + item.Id + " 没有品阶");
            }

            if (!_byId.TryGetValue(item.Grade, out var grade))
            {
                throw new ConfigException("装备 " + item.Id + " 的品阶不在品阶表里：" + item.Grade);
            }

            if (item.Attack > grade.BaseAttack)
            {
                throw new ConfigException(
                    "装备 " + item.Id + " 的攻击 " + item.Attack + " 超过 " + grade.Id + " 档基准 " + grade.BaseAttack);
            }

            if (item.RequireLevel > grade.RequireLevel)
            {
                throw new ConfigException(
                    "装备 " + item.Id + " 的 require_level " + item.RequireLevel
                    + " 超过 " + grade.Id + " 档等级带 " + grade.RequireLevel);
            }
        }
    }

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; set; }
        [JsonPropertyName("version")] public int? Version { get; set; }
        [JsonPropertyName("grades")] public List<WireGrade>? Grades { get; set; }
    }

    sealed class WireGrade
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("order")] public int? Order { get; set; }
        [JsonPropertyName("require_level")] public int? RequireLevel { get; set; }
        [JsonPropertyName("base_attack")] public long? BaseAttack { get; set; }
    }
}
