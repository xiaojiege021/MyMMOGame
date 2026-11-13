using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 一个副本：编号、名字、难度档、人数区间、进本时扣不扣疲劳与周份额。
/// FatiguePerPlayer 是单人进一次的基础消耗，扣的时候再乘进本人数；
/// 不扣疲劳的本（主线）它是 0。
///
/// P2-2a：tier=hard 的高难本（龙巢）另有三条口径——至少 MinPlayers 人、
/// 每人每周 EntriesPerWeek 次、进一次得 RewardPerPlayer 份（不乘人数）。
/// 普通本这三项固定为 1 / 0 / 0，由 DungeonTable.Load 填，不另存一套。
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
/// 副本表。与战斗服读同一份 config/dungeons.json：战斗服按它开本，
/// 角色服按它校验人数与扣减，不各存一套。
/// </summary>
public sealed class DungeonTable
{
    readonly Dictionary<string, DungeonDef> _byId = new(StringComparer.Ordinal);

    public int Version { get; }

    /// <summary>一天能累计到的疲劳上限（方案写定 6000）。</summary>
    public int FatigueDailyCap { get; }

    /// <summary>温泉用一次恢复多少疲劳（方案写定 1500），只补疲劳。</summary>
    public int HotSpringRecovery { get; }

    /// <summary>一周能用掉多少个单人份（方案写定 12）。</summary>
    public int WeeklyShareCap { get; }

    /// <summary>每多一名队员，怪物生命多出的倍数。方案没写定曲线，数值在配置里。</summary>
    public double MonsterHpScalePerPlayer { get; }

    /// <summary>每多一名队员，怪物伤害多出的倍数。方案没写定曲线，数值在配置里。</summary>
    public double MonsterDamageScalePerPlayer { get; }

    public IReadOnlyCollection<DungeonDef> All => _byId.Values;

    public DungeonDef? Find(string id) => _byId.GetValueOrDefault(id);

    DungeonTable(
        Dictionary<string, DungeonDef> byId,
        int version,
        int fatigueDailyCap,
        int hotSpringRecovery,
        int weeklyShareCap,
        double monsterHpScalePerPlayer,
        double monsterDamageScalePerPlayer)
    {
        _byId = byId;
        Version = version;
        FatigueDailyCap = fatigueDailyCap;
        HotSpringRecovery = hotSpringRecovery;
        WeeklyShareCap = weeklyShareCap;
        MonsterHpScalePerPlayer = monsterHpScalePerPlayer;
        MonsterDamageScalePerPlayer = monsterDamageScalePerPlayer;
    }

    public static DungeonTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到副本表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("副本表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("副本表不是合法 JSON：" + ex.Message);
        }

        var cap = wire.FatigueDailyCap ?? 6000;
        if (cap < 1 || cap > 1_000_000)
        {
            throw new ConfigException("fatigue_daily_cap 必须在 1 到 1000000 之间，现在是 " + cap);
        }

        // 温泉恢复量必须写在配置里，不在代码里给默认值：缺了就起不来，
        // 免得两处各记一个数字。方案写定 1500，配置是唯一正本。
        var recovery = wire.HotSpringRecovery
            ?? throw new ConfigException("dungeons.json 缺 hot_spring_recovery");
        if (recovery < 1 || recovery > cap)
        {
            throw new ConfigException(
                "hot_spring_recovery 必须在 1 到 " + cap + " 之间，现在是 " + recovery);
        }

        // 周份额上限与怪物缩放系数同样只有这一份配置，缺字段就起不来。
        var weeklyCap = wire.WeeklyShareCap
            ?? throw new ConfigException("dungeons.json 缺 weekly_share_cap");
        if (weeklyCap < 1 || weeklyCap > 1_000_000)
        {
            throw new ConfigException("weekly_share_cap 必须在 1 到 1000000 之间，现在是 " + weeklyCap);
        }

        static double Scale(string key, double? value) =>
            value is null
                ? throw new ConfigException("dungeons.json 缺 " + key)
                : double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= 100
                    ? value.Value
                    : throw new ConfigException(key + " 必须在 0 到 100 之间，现在是 " + value.Value);

        var hpScale = Scale("monster_hp_scale_per_player", wire.MonsterHpScalePerPlayer);
        var damageScale = Scale("monster_damage_scale_per_player", wire.MonsterDamageScalePerPlayer);

        var map = new Dictionary<string, DungeonDef>(StringComparer.Ordinal);
        foreach (var item in wire.Dungeons)
        {
            var id = (item.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("dungeons.json 里有条目缺 id");
            }

            if (item.MaxPlayers is null or < 1 or > 100)
            {
                throw new ConfigException("副本 " + id + " 的人数上限要在 1 到 100 之间");
            }

            // tier 缺省是 normal；高难本才允许出现 min_players / entries_per_week /
            // reward_per_player。老四条不写这四个字段，走 normal 的固定值。
            var tier = (item.Tier ?? "normal").Trim();
            if (tier is not ("normal" or "hard"))
            {
                throw new ConfigException("副本 " + id + " 的 tier 只能是 normal 或 hard，现在是 " + tier);
            }

            int minPlayers;
            int entriesPerWeek;
            int rewardPerPlayer;
            if (tier == "hard")
            {
                if (item.MinPlayers is null)
                {
                    throw new ConfigException("高难副本 " + id + " 缺 min_players");
                }

                if (item.MinPlayers.Value < 2 || item.MinPlayers.Value > item.MaxPlayers.Value)
                {
                    throw new ConfigException(
                        "高难副本 " + id + " 的 min_players 要在 2 到人数上限 "
                        + item.MaxPlayers.Value + " 之间，现在是 " + item.MinPlayers.Value);
                }

                if (item.EntriesPerWeek is null)
                {
                    throw new ConfigException("高难副本 " + id + " 缺 entries_per_week");
                }

                if (item.EntriesPerWeek.Value < 1)
                {
                    throw new ConfigException("高难副本 " + id + " 的 entries_per_week 必须是正数");
                }

                if (item.RewardPerPlayer is null)
                {
                    throw new ConfigException("高难副本 " + id + " 缺 reward_per_player");
                }

                if (item.RewardPerPlayer.Value < 1)
                {
                    throw new ConfigException("高难副本 " + id + " 的 reward_per_player 必须是正数");
                }

                minPlayers = item.MinPlayers.Value;
                entriesPerWeek = item.EntriesPerWeek.Value;
                rewardPerPlayer = item.RewardPerPlayer.Value;
            }
            else
            {
                // 普通本不许配高难三项：配了说明配置写错了，不让它悄悄生效。
                if (item.MinPlayers is not null || item.EntriesPerWeek is not null
                    || item.RewardPerPlayer is not null)
                {
                    throw new ConfigException(
                        "副本 " + id + " 不是高难本，只有高难本才配 min_players / "
                        + "entries_per_week / reward_per_player");
                }

                minPlayers = 1;
                entriesPerWeek = 0;
                rewardPerPlayer = 0;
            }

            var charges = item.ChargesFatigue ?? false;
            var perPlayer = item.FatiguePerPlayer ?? 0;
            if (charges && perPlayer < 1)
            {
                throw new ConfigException("副本 " + id + " 扣疲劳，fatigue_per_player 必须是正数");
            }

            if ((long)perPlayer * item.MaxPlayers.Value > cap)
            {
                throw new ConfigException("副本 " + id + " 满员一次的疲劳就超过日上限，永远进不去");
            }

            var takesShare = item.TakesWeeklyShare ?? false;
            if (takesShare && item.MaxPlayers.Value > weeklyCap)
            {
                throw new ConfigException("副本 " + id + " 满员一次的份额就超过周上限，永远进不去");
            }

            map[id] = new DungeonDef(
                id,
                (item.Name ?? id).Trim(),
                tier,
                minPlayers,
                item.MaxPlayers.Value,
                charges,
                takesShare,
                charges ? perPlayer : 0,
                entriesPerWeek,
                rewardPerPlayer);
        }

        if (map.Count == 0)
        {
            throw new ConfigException("dungeons.json 里一个副本都没有");
        }

        return new DungeonTable(map, wire.Version, cap, recovery, weeklyCap, hpScale, damageScale);
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

        [JsonPropertyName("fatigue_daily_cap")]
        public int? FatigueDailyCap { get; init; }

        [JsonPropertyName("hot_spring_recovery")]
        public int? HotSpringRecovery { get; init; }

        [JsonPropertyName("weekly_share_cap")]
        public int? WeeklyShareCap { get; init; }

        [JsonPropertyName("monster_hp_scale_per_player")]
        public double? MonsterHpScalePerPlayer { get; init; }

        [JsonPropertyName("monster_damage_scale_per_player")]
        public double? MonsterDamageScalePerPlayer { get; init; }

        [JsonPropertyName("dungeons")]
        public List<DungeonItem> Dungeons { get; init; } = [];
    }

    sealed record DungeonItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        // P2-2a：缺省 normal；只有 hard 才配下面三项。
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
