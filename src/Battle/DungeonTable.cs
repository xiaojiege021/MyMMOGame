using System.Text.Json.Serialization;

namespace Battle;

/// <summary>一个副本：编号、名字、人数上限、进本时扣不扣疲劳与周份额。</summary>
public sealed record DungeonDef(string Id, string Name, int MaxPlayers, bool ChargesFatigue, bool TakesWeeklyShare);

/// <summary>
/// 副本表。战斗服与角色服读同一份 config/dungeons.json，不各存一套，
/// 免得战斗服按 1 人开、角色服按 3 人算。
/// </summary>
public sealed class DungeonTable
{
    readonly Dictionary<string, DungeonDef> _byId = new(StringComparer.Ordinal);

    public int Version { get; }

    /// <summary>每多一名队员，怪物生命多出的倍数（倍率 = 1 + (人数 - 1) × 系数）。</summary>
    public double MonsterHpScalePerPlayer { get; }

    /// <summary>每多一名队员，怪物伤害多出的倍数。方案没写定曲线，数值在配置里。</summary>
    public double MonsterDamageScalePerPlayer { get; }

    public IReadOnlyCollection<DungeonDef> All => _byId.Values;

    public DungeonDef? Find(string id) => _byId.GetValueOrDefault(id);

    DungeonTable(
        Dictionary<string, DungeonDef> byId,
        int version,
        double monsterHpScalePerPlayer,
        double monsterDamageScalePerPlayer)
    {
        _byId = byId;
        Version = version;
        MonsterHpScalePerPlayer = monsterHpScalePerPlayer;
        MonsterDamageScalePerPlayer = monsterDamageScalePerPlayer;
    }

    public static DungeonTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"副本表不存在：{path}");
        }

        Wire wire;
        try
        {
            wire = ConfigJson.Read<Wire>(path);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException)
        {
            throw new ConfigException($"副本表不是合法 JSON：{ex.Message}");
        }

        // 缩放系数与周份额表放在同一份配置里，战斗服只读自己要用的两个。
        // 缺字段或越界直接起不来，不给代码里的兜底数字。
        static double Scale(string key, double? value) =>
            value is null
                ? throw new ConfigException($"副本表缺 {key}")
                : double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= 100
                    ? value.Value
                    : throw new ConfigException($"{key} 必须在 0 到 100 之间，现在是 {value.Value}");

        var hpScale = Scale("monster_hp_scale_per_player", wire.MonsterHpScalePerPlayer);
        var damageScale = Scale("monster_damage_scale_per_player", wire.MonsterDamageScalePerPlayer);

        var map = new Dictionary<string, DungeonDef>(StringComparer.Ordinal);
        foreach (var item in wire.Dungeons)
        {
            var id = (item.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException($"{path}：副本编号不能为空");
            }

            if (item.MaxPlayers is null or < 1 or > 100)
            {
                throw new ConfigException($"{path}：副本 {id} 的人数上限要在 1 到 100 之间");
            }

            map[id] = new DungeonDef(
                id,
                (item.Name ?? id).Trim(),
                item.MaxPlayers.Value,
                item.ChargesFatigue ?? false,
                item.TakesWeeklyShare ?? false);
        }

        if (map.Count == 0)
        {
            throw new ConfigException($"{path}：一个副本都没有");
        }

        return new DungeonTable(map, wire.Version, hpScale, damageScale);
    }

    sealed record Wire
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("monster_hp_scale_per_player")]
        public double? MonsterHpScalePerPlayer { get; init; }

        [JsonPropertyName("monster_damage_scale_per_player")]
        public double? MonsterDamageScalePerPlayer { get; init; }

        [JsonPropertyName("dungeons")]
        public List<Item> Dungeons { get; init; } = [];
    }

    sealed record Item
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("max_players")]
        public int? MaxPlayers { get; init; }

        [JsonPropertyName("charges_fatigue")]
        public bool? ChargesFatigue { get; init; }

        [JsonPropertyName("takes_weekly_share")]
        public bool? TakesWeeklyShare { get; init; }
    }
}
