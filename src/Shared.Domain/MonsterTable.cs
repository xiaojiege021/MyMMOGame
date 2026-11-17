using System.Globalization;
using System.Text.RegularExpressions;

namespace Mmo.Shared.Domain;

/// <summary>一条掉落：物品编号 + 数量 + 概率。物品本体在 config/items.json。</summary>
public sealed record MonsterDropDef(string ItemId, int Count, int ChancePct);

/// <summary>地表刷怪点：在哪块地表、什么坐标、多久刷一次。</summary>
public sealed record MonsterSpawnDef(string Region, double X, double Y, double Z, int RespawnS);

/// <summary>
/// 一只怪物：编号、名字、等级、档位、三项基础数值、技能编号、掉落与刷怪点。
/// 入口在副本里的 Boss 不写 spawn；普通 / 精英怪必须有。
/// </summary>
public sealed record MonsterDef(
    string Id,
    string Name,
    int Level,
    string Tier,
    int Hp,
    int Attack,
    int Defense,
    IReadOnlyList<long> Skills,
    IReadOnlyList<MonsterDropDef> Drops,
    MonsterSpawnDef? Spawn,
    string Note)
{
    public bool IsBoss => Tier is "boss" or "world_boss";
}

/// <summary>
/// 怪物表（K6）。战斗服启动时读它：技能编号必须在 skills.json 里，掉落物品必须在
/// items.json 里；世界服读同一份时再补 spawn.region 必须在已配置地表。
/// 数值与掉率是占位，B15 按内容层再定。
/// </summary>
public sealed class MonsterTable
{
    public const string WrittenSchema = "monster-table/1";

    static readonly HashSet<string> Tiers = new(StringComparer.Ordinal)
    {
        "normal", "elite", "boss", "world_boss",
    };

    static readonly Regex IdPattern = new("^(mob|boss)_[a-z0-9_]+$", RegexOptions.CultureInvariant);

    readonly Dictionary<string, MonsterDef> _byId = new(StringComparer.Ordinal);

    public int Version { get; }

    public IReadOnlyCollection<MonsterDef> All => _byId.Values;

    public MonsterDef? Find(string id) => _byId.GetValueOrDefault(id);

    MonsterTable(Dictionary<string, MonsterDef> byId, int version)
    {
        _byId = byId;
        Version = version;
    }

    public static MonsterTable Load(
        string path,
        IReadOnlyCollection<string>? skillIds = null,
        IReadOnlyCollection<string>? itemIds = null,
        IReadOnlyCollection<string>? regionIds = null)
    {
        var wire = ConfigJson.Read<Wire>(path);
        if (!string.Equals((wire.Schema ?? "").Trim(), WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException(path + "：schema 必须是 " + WrittenSchema);
        }
        if (wire.Version is not > 0)
        {
            throw new ConfigException(path + "：version 必须大于 0");
        }
        if (wire.Monsters is null || wire.Monsters.Count == 0)
        {
            throw new ConfigException(path + "：一只怪物都没有");
        }

        var skills = skillIds is null ? null : new HashSet<string>(skillIds, StringComparer.Ordinal);
        var items = itemIds is null ? null : new HashSet<string>(itemIds, StringComparer.Ordinal);
        var regions = regionIds is null ? null : new HashSet<string>(regionIds, StringComparer.Ordinal);

        var map = new Dictionary<string, MonsterDef>(StringComparer.Ordinal);
        foreach (var monster in wire.Monsters)
        {
            var id = (monster.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException(path + "：有怪物缺 id");
            }
            if (!IdPattern.IsMatch(id))
            {
                throw new ConfigException(path + "：怪物编号要写成 mob_ / boss_<小写编号>：" + id);
            }
            if (map.ContainsKey(id))
            {
                throw new ConfigException(path + "：怪物编号重复：" + id);
            }

            var name = (monster.Name ?? "").Trim();
            if (name.Length == 0)
            {
                throw new ConfigException(path + "：怪物 " + id + " 缺 name");
            }

            var level = monster.Level ?? 0;
            if (level < 1 || level > 100)
            {
                throw new ConfigException(path + "：怪物 " + id + " 的 level 要在 1..100 之间");
            }

            var tier = (monster.Tier ?? "").Trim();
            if (!Tiers.Contains(tier))
            {
                throw new ConfigException(path + "：怪物 " + id + " 的 tier 不认识：" + tier);
            }

            var stats = monster.Stats ?? throw new ConfigException(path + "：怪物 " + id + " 缺 stats");
            var hp = stats.Hp ?? 0;
            var attack = stats.Attack ?? 0;
            var defense = stats.Defense ?? -1;
            if (hp <= 0 || attack <= 0 || defense < 0)
            {
                throw new ConfigException(path + "：怪物 " + id + " 的 hp / attack 要大于 0，defense 不能为负");
            }

            var skillList = new List<long>();
            foreach (var skill in monster.Skills ?? [])
            {
                var text = skill.ToString(CultureInfo.InvariantCulture);
                if (skills is not null && !skills.Contains(text))
                {
                    throw new ConfigException(path + "：怪物 " + id + " 的技能不在 skills.json：" + text);
                }
                if (!skillList.Contains(skill))
                {
                    skillList.Add(skill);
                }
            }

            var dropList = new List<MonsterDropDef>();
            var dropSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var drop in monster.Drops ?? [])
            {
                var itemId = (drop.ItemId ?? "").Trim();
                if (items is not null && !items.Contains(itemId))
                {
                    throw new ConfigException(path + "：怪物 " + id + " 的掉落不在 items.json：" + itemId);
                }
                var count = drop.Count ?? 0;
                var chance = drop.ChancePct ?? -1;
                if (count <= 0)
                {
                    throw new ConfigException(path + "：怪物 " + id + " 的掉落数量要大于 0：" + itemId);
                }
                if (chance < 0 || chance > 100)
                {
                    throw new ConfigException(path + "：怪物 " + id + " 的掉率要在 0..100 之间：" + itemId);
                }
                if (!dropSeen.Add(itemId))
                {
                    throw new ConfigException(path + "：怪物 " + id + " 对同一件物品写了两次掉落：" + itemId);
                }
                dropList.Add(new MonsterDropDef(itemId, count, chance));
            }

            MonsterSpawnDef? spawn = null;
            if (monster.Spawn is not null)
            {
                var region = (monster.Spawn.Region ?? "").Trim();
                if (regions is not null && !regions.Contains(region))
                {
                    throw new ConfigException(path + "：怪物 " + id + " 的 spawn.region 不是已配置地表：" + region);
                }
                if (!double.IsFinite(monster.Spawn.X) || !double.IsFinite(monster.Spawn.Y)
                    || !double.IsFinite(monster.Spawn.Z))
                {
                    throw new ConfigException(path + "：怪物 " + id + " 的 spawn 坐标必须是有限数");
                }
                if (monster.Spawn.RespawnS is not (> 0 and <= 86400))
                {
                    throw new ConfigException(path + "：怪物 " + id + " 的 respawn_s 要在 1..86400 之间");
                }
                spawn = new MonsterSpawnDef(
                    region, monster.Spawn.X, monster.Spawn.Y, monster.Spawn.Z, monster.Spawn.RespawnS.Value);
            }

            if (spawn is null && tier is "normal" or "elite")
            {
                throw new ConfigException(path + "：普通 / 精英怪必须有 spawn：" + id);
            }

            map[id] = new MonsterDef(
                id, name, level, tier, hp, attack, defense,
                skillList, dropList, spawn, (monster.Note ?? "").Trim());
        }

        return new MonsterTable(map, wire.Version.Value);
    }

    sealed record Wire
    {
        public string? Schema { get; init; }
        public int? Version { get; init; }
        public List<MonsterItem>? Monsters { get; init; }
    }

    sealed record MonsterItem
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public int? Level { get; init; }
        public string? Tier { get; init; }
        public StatsItem? Stats { get; init; }
        public List<long>? Skills { get; init; }
        public List<DropItem>? Drops { get; init; }
        public SpawnItem? Spawn { get; init; }
        public string? Note { get; init; }
    }

    sealed record StatsItem
    {
        public int? Hp { get; init; }
        public int? Attack { get; init; }
        public int? Defense { get; init; }
    }

    sealed record DropItem
    {
        public string? ItemId { get; init; }
        public int? Count { get; init; }
        public int? ChancePct { get; init; }
    }

    sealed record SpawnItem
    {
        public string? Region { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double Z { get; init; }
        public int? RespawnS { get; init; }
    }
}
