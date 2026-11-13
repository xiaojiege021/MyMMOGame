using System.Text.Json.Serialization;

namespace Battle;

/// <summary>
/// 一个副本：编号、名字、难度档、进本人数上下限、进本时扣不扣疲劳与周份额。
/// 疲劳按单人份记，扣的时候再乘进本人数；不扣疲劳的本它是 0。
/// 高难本（tier=hard）另记每人每周的进入次数与每人产出份数，
/// 普通本这两个值为 0：不限次数、产出本来就按人数各一份。
/// 与角色服的 DungeonDef 同名同形，两个服读同一份 config/dungeons.json。
/// </summary>
public sealed record DungeonDef(
    string Id,
    string Name,
    string Tier,
    int MinPlayers,
    int MaxPlayers,
    bool ChargesFatigue,
    bool TakesWeeklyShare,
    int FatiguePerPlayer,
    int EntriesPerWeek,
    int RewardPerPlayer)
{
    public bool IsHard => Tier == "hard";
}

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

        // 战斗服不扣疲劳与份额，但校验口径要和角色服一致：同一份配置在一个服能起、
        // 在另一个服起不来，就等于两套规则。上限值只从这份配置读，不写第二套数字。
        var fatigueCap = wire.FatigueDailyCap ?? 6000;
        if (fatigueCap < 1 || fatigueCap > 1_000_000)
        {
            throw new ConfigException($"{path}：fatigue_daily_cap 必须在 1 到 1000000 之间，现在是 {fatigueCap}");
        }

        var weeklyCap = wire.WeeklyShareCap
            ?? throw new ConfigException($"{path}：副本表缺 weekly_share_cap");
        if (weeklyCap < 1 || weeklyCap > 1_000_000)
        {
            throw new ConfigException($"{path}：weekly_share_cap 必须在 1 到 1000000 之间，现在是 {weeklyCap}");
        }

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

            var maxPlayers = item.MaxPlayers.Value;

            // 难度档缺省 normal。只有高难本允许写人数下限、每周次数与每人产出份数；
            // 普通本写了这三个字段就报错，免得有人以为改了也没用。
            var tier = (item.Tier ?? "normal").Trim();
            if (tier != "normal" && tier != "hard")
            {
                throw new ConfigException($"{path}：副本 {id} 的 tier 只能是 normal 或 hard，现在是「{tier}」");
            }

            int minPlayers;
            int entriesPerWeek;
            int rewardPerPlayer;
            if (tier == "hard")
            {
                if (item.MinPlayers is null)
                {
                    throw new ConfigException($"{path}：副本 {id} 是高难本，必须写 min_players");
                }

                if (item.MinPlayers < 2 || item.MinPlayers > maxPlayers)
                {
                    throw new ConfigException(
                        $"{path}：副本 {id} 的 min_players 要在 2 到人数上限 {maxPlayers} 之间，现在是 {item.MinPlayers}");
                }

                if (item.EntriesPerWeek is null or < 1)
                {
                    throw new ConfigException($"{path}：副本 {id} 是高难本，entries_per_week 必须是正数");
                }

                if (item.RewardPerPlayer is null or < 1)
                {
                    throw new ConfigException($"{path}：副本 {id} 是高难本，reward_per_player 必须是正数");
                }

                minPlayers = item.MinPlayers.Value;
                entriesPerWeek = item.EntriesPerWeek.Value;
                rewardPerPlayer = item.RewardPerPlayer.Value;
            }
            else
            {
                if (item.MinPlayers is not null || item.EntriesPerWeek is not null || item.RewardPerPlayer is not null)
                {
                    throw new ConfigException(
                        $"{path}：副本 {id} 是普通本，min_players / entries_per_week / reward_per_player 只对高难本生效");
                }

                // 普通本没有人数下限（至少 1 人），也不限次数；产出按人数各一份，
                // 没有「每人份数」这个数，所以两个值都记 0。
                minPlayers = 1;
                entriesPerWeek = 0;
                rewardPerPlayer = 0;
            }

            var charges = item.ChargesFatigue ?? false;
            var perPlayer = item.FatiguePerPlayer ?? 0;
            if (charges && perPlayer < 1)
            {
                throw new ConfigException($"{path}：副本 {id} 扣疲劳，fatigue_per_player 必须是正数");
            }

            if ((long)perPlayer * maxPlayers > fatigueCap)
            {
                throw new ConfigException($"{path}：副本 {id} 满员一次的疲劳就超过日上限，永远进不去");
            }

            var takesShare = item.TakesWeeklyShare ?? false;
            if (takesShare && maxPlayers > weeklyCap)
            {
                throw new ConfigException($"{path}：副本 {id} 满员一次的份额就超过周上限，永远进不去");
            }

            map[id] = new DungeonDef(
                id,
                (item.Name ?? id).Trim(),
                tier,
                minPlayers,
                maxPlayers,
                charges,
                takesShare,
                charges ? perPlayer : 0,
                entriesPerWeek,
                rewardPerPlayer);
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

        [JsonPropertyName("fatigue_daily_cap")]
        public int? FatigueDailyCap { get; init; }

        [JsonPropertyName("weekly_share_cap")]
        public int? WeeklyShareCap { get; init; }

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

        [JsonPropertyName("tier")]
        public string? Tier { get; init; }

        [JsonPropertyName("min_players")]
        public int? MinPlayers { get; init; }

        [JsonPropertyName("max_players")]
        public int? MaxPlayers { get; init; }

        [JsonPropertyName("charges_fatigue")]
        public bool? ChargesFatigue { get; init; }

        [JsonPropertyName("takes_weekly_share")]
        public bool? TakesWeeklyShare { get; init; }

        [JsonPropertyName("fatigue_per_player")]
        public int? FatiguePerPlayer { get; init; }

        [JsonPropertyName("entries_per_week")]
        public int? EntriesPerWeek { get; init; }

        [JsonPropertyName("reward_per_player")]
        public int? RewardPerPlayer { get; init; }
    }
}
