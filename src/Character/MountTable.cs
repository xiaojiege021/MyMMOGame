using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// B4 玩法坐骑正本（方案 9.4：普通和高级只差移动速度；商城坐骑只换外观、
/// 不叠加速度、不进战斗）。本表只有两档与一个 speed_bps：
///   · 没有攻击 / 防御 / 技能 / 冷却字段，battle.json 里也一条都不引用；
///   · speed_bps 是「相对 world.json 的 max_move_speed 再加多少万分比」，
///     只由世界服算进地表水平位移上限，不改飞行（飞行仍只由境界打开）。
/// 加载期硬校验：档位顺序、区间不交叉、坐骑必须是物品表里的 kind=mount 且不可叠。
/// 数值方案没写定（占位、pending=true），改这份文件重启角色服与世界服生效。
/// </summary>
public sealed class MountTable
{
    public const string WrittenSchema = "mount-table/1";
    /// <summary>两档，顺序就是高低：advanced 必须比 normal 快。</summary>
    public static readonly string[] TierIds = ["normal", "advanced"];
    public const int MaxSpeedBpsCap = 20000;

    public sealed record Tier(string Id, string Name, int MinSpeedBps, int MaxSpeedBps);

    public sealed record Entry(string ItemId, string Name, string Tier, int SpeedBps);

    public int Version { get; }
    public bool Pending { get; }
    public IReadOnlyList<Tier> Tiers { get; }
    public IReadOnlyList<Entry> Entries { get; }

    MountTable(int version, bool pending, IReadOnlyList<Tier> tiers, IReadOnlyList<Entry> entries)
    {
        Version = version;
        Pending = pending;
        Tiers = tiers;
        Entries = entries;
    }

    public Entry? Find(string itemId)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.ItemId, itemId, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>骑着的这只给多少加成；不认识的物品回 0（世界服据此当没有坐骑）。</summary>
    public int SpeedBpsFor(string itemId) => Find(itemId)?.SpeedBps ?? 0;

    public Tier? TierOf(string id)
    {
        foreach (var tier in Tiers)
        {
            if (string.Equals(tier.Id, id, StringComparison.Ordinal))
            {
                return tier;
            }
        }

        return null;
    }

    public static MountTable Load(string path, ItemTable items)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到坐骑表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("mount.json 是空的");
        }
        catch (JsonException ex)
        {
            throw new ConfigException("mount.json 不是合法 JSON：" + ex.Message);
        }

        if ((wire.Schema ?? "").Trim() != WrittenSchema)
        {
            throw new ConfigException("mount.json schema 必须是 " + WrittenSchema);
        }

        if (wire.Tiers is null || wire.Tiers.Count != TierIds.Length)
        {
            throw new ConfigException("坐骑表必须正好 " + TierIds.Length + " 个档位（普通 / 高级）");
        }

        var tiers = new List<Tier>(TierIds.Length);
        for (var i = 0; i < TierIds.Length; i++)
        {
            var row = wire.Tiers[i];
            var id = (row.Id ?? "").Trim();
            if (!string.Equals(id, TierIds[i], StringComparison.Ordinal))
            {
                throw new ConfigException(
                    "坐骑表第 " + (i + 1) + " 档必须写 " + TierIds[i] + "（顺序即高低）");
            }

            var name = (row.Name ?? "").Trim();
            if (name.Length == 0)
            {
                throw new ConfigException("坐骑档位 " + id + " 缺 name");
            }

            var min = row.MinSpeedBps ?? 0;
            var max = row.MaxSpeedBps ?? 0;
            if (min <= 0 || max > MaxSpeedBpsCap)
            {
                throw new ConfigException(
                    "坐骑档位 " + id + " 的区间须在 1.." + MaxSpeedBpsCap + "（现在是 " + min + ".." + max + "）");
            }

            if (min > max)
            {
                throw new ConfigException("坐骑档位 " + id + " 的 min_speed_bps 高于 max_speed_bps");
            }

            if (tiers.Count > 0 && min <= tiers[^1].MaxSpeedBps)
            {
                // 方案 9.4 只允许「高级比普通快」，两档区间不许交叉，
                // 否则同一只坐骑能落进两档，档位就没有意义了。
                throw new ConfigException(
                    "坐骑档位 " + id + " 的下限必须高于上一档的上限（" + tiers[^1].MaxSpeedBps + "）");
            }

            tiers.Add(new Tier(id, name, min, max));
        }

        if (wire.Entries is null || wire.Entries.Count == 0)
        {
            throw new ConfigException("坐骑表一只坐骑都没有");
        }

        var entries = new List<Entry>(wire.Entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in wire.Entries)
        {
            var itemId = (row.ItemId ?? "").Trim();
            if (itemId.Length == 0)
            {
                throw new ConfigException("坐骑表有一条缺 item_id");
            }

            if (!seen.Add(itemId))
            {
                throw new ConfigException("坐骑表 item_id 重复：" + itemId);
            }

            var goods = items.Find(itemId)
                ?? throw new ConfigException("坐骑 " + itemId + " 不在 items.json 里");
            if (!string.Equals(goods.Kind, "mount", StringComparison.Ordinal))
            {
                throw new ConfigException("坐骑 " + itemId + " 的 kind 必须是 mount（现在是 " + goods.Kind + "）");
            }

            if (goods.StackMax != 1)
            {
                // 一只坐骑就是一件，可叠会让「骑哪一行」变得没有唯一答案。
                throw new ConfigException("坐骑 " + itemId + " 的 stack_max 必须是 1");
            }

            var tierId = (row.Tier ?? "").Trim();
            var tier = tiers.Find(t => string.Equals(t.Id, tierId, StringComparison.Ordinal))
                ?? throw new ConfigException("坐骑 " + itemId + " 的 tier 不认识：" + tierId);

            var speed = row.SpeedBps ?? 0;
            if (speed < tier.MinSpeedBps || speed > tier.MaxSpeedBps)
            {
                throw new ConfigException(
                    "坐骑 " + itemId + " 的 speed_bps 必须在 " + tier.Id + " 档的 "
                    + tier.MinSpeedBps + ".." + tier.MaxSpeedBps + "（现在是 " + speed + "）");
            }

            entries.Add(new Entry(itemId, (row.Name ?? goods.Name).Trim(), tier.Id, speed));
        }

        return new MountTable(wire.Version, wire.Pending ?? false, tiers, entries);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("pending")]
        public bool? Pending { get; init; }

        [JsonPropertyName("tiers")]
        public List<TierWire>? Tiers { get; init; }

        [JsonPropertyName("entries")]
        public List<EntryWire>? Entries { get; init; }
    }

    sealed class TierWire
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("min_speed_bps")]
        public int? MinSpeedBps { get; init; }

        [JsonPropertyName("max_speed_bps")]
        public int? MaxSpeedBps { get; init; }
    }

    sealed class EntryWire
    {
        [JsonPropertyName("item_id")]
        public string? ItemId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("tier")]
        public string? Tier { get; init; }

        [JsonPropertyName("speed_bps")]
        public int? SpeedBps { get; init; }
    }
}
