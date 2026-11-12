using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 解绑道具的唯一正本（`config/unbind_item.json`，P1-5c8）。方案 12.1 写定：
/// 「把绑定装备变成可交易。购买后绑定。只对绑定装备使用，不对符、券、卷轴和经验道具
/// 使用。这件装备绑定越久，需要的张数越少，下限走配置。不加属性，不提高强化成功率。
/// 副本和任务不产出。」
///
/// 这里只写道具自己：编号、名字、几档「绑定多久 → 用几张」。绑定状态本身仍是
/// `"角色".inventory` 的 `bound` / `bound_at`（P1-1b3），解绑就是把这两列改回
/// 「未绑」——不新建表，也不另存一份「解绑过」的状态。年龄用数据库时钟算
/// （`now() - bound_at`），换进程、重启角色服都还是同一个答案。
///
/// 档位与张数方案没给数字，全部标待定，代码不写死；坏表在启动与只读入口就起不来。
/// 与符、卷轴、限时经验道具的编号不许撞（四种是四种东西）。
/// </summary>
public sealed class UnbindTable
{
    public const string WrittenSchema = "unbind-table/1";

    /// <summary>一档：绑定满 minBoundHours 小时，用 cost 张。cost 越小表示绑得越久。</summary>
    public sealed record Tier(int MinBoundHours, int Cost);

    /// <summary>道具那一段。Tiers 按小时从大到小排好，CostFor 顺序扫。</summary>
    public sealed record UnbindSpec(
        string ItemId, string Name, IReadOnlyList<Tier> Tiers, string Note);

    public int Version { get; }
    public string Note { get; }
    public UnbindSpec Unbind { get; }

    /// <summary>下限：张数的下限，也就是绑得最久那一档（Tiers 按小时从大到小排）的张数。</summary>
    public int FloorCost => Unbind.Tiers.Count == 0 ? 0 : Unbind.Tiers[0].Cost;

    UnbindTable(int version, string note, UnbindSpec unbind)
    {
        Version = version;
        Note = note;
        Unbind = unbind;
    }

    /// <summary>这个编号是不是解绑道具。不是就返回 null，调用方据此拒绝。</summary>
    public UnbindSpec? Find(string itemId) =>
        string.Equals(Unbind.ItemId, itemId, StringComparison.Ordinal) ? Unbind : null;

    /// <summary>
    /// 按绑定时长算需要几张。Tiers 从大到小排，第一个「满了」的就是答案；
    /// 年龄为负（时钟回拨 / 数据异常）按 0 处理，退到最低档。
    /// </summary>
    public int CostFor(long ageHours)
    {
        if (ageHours < 0)
        {
            ageHours = 0;
        }

        for (var i = 0; i < Unbind.Tiers.Count; i++)
        {
            if (ageHours >= Unbind.Tiers[i].MinBoundHours)
            {
                return Unbind.Tiers[i].Cost;
            }
        }

        return FloorCost;
    }

    public bool FloorHit(long ageHours) => CostFor(ageHours) == FloorCost;

    public static UnbindTable Load(
        string path, ItemTable items, TalismanTable talismans,
        ReviveScrollTable scrolls, ExpBoosterTable boosters)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配置文件 " + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("配置是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配置不是合法 JSON：" + ex.Message);
        }

        if (!string.Equals(wire.Schema, WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException("unbind_item.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("unbind_item.json 的 version 必须是正整数");
        }

        var unbind = LoadUnbind(wire.Unbind, items, talismans, scrolls, boosters);
        return new UnbindTable(wire.Version, (wire.Note ?? "").Trim(), unbind);
    }

    static UnbindSpec LoadUnbind(
        WireUnbind? raw, ItemTable items, TalismanTable talismans,
        ReviveScrollTable scrolls, ExpBoosterTable boosters)
    {
        if (raw is null)
        {
            throw new ConfigException("unbind_item.json 缺 unbind 段");
        }

        var itemId = (raw.ItemId ?? "").Trim();
        var name = (raw.Name ?? "").Trim();
        if (itemId.Length == 0)
        {
            throw new ConfigException("unbind_item.json 的 unbind.item_id 不能是空的");
        }

        if (name.Length == 0)
        {
            throw new ConfigException("unbind_item.json 的 unbind.name 不能是空的");
        }

        var goods = items.Find(itemId);
        if (goods is null)
        {
            throw new ConfigException("unbind_item.json 的道具不在 items.json 里：" + itemId);
        }

        // 方案 12.1：解绑道具「购买后绑定」，是消耗品，不是装备、材料或凭证。
        if (!string.Equals(goods.Kind, "consumable", StringComparison.Ordinal))
        {
            throw new ConfigException("unbind_item.json 的道具必须是 consumable：" + goods.Kind);
        }

        if (goods.Bind != BindRule.Always)
        {
            throw new ConfigException("unbind_item.json 的道具必须到账即绑："
                + goods.Id + " 的 bind 是 " + goods.Bind);
        }

        if (talismans.Find(itemId) is not null)
        {
            throw new ConfigException("unbind_item.json 的道具不能和符是同一个编号：" + itemId);
        }

        if (scrolls.Find(itemId) is not null)
        {
            throw new ConfigException("unbind_item.json 的道具不能和卷轴是同一个编号：" + itemId);
        }

        if (boosters.Find(itemId) is not null)
        {
            throw new ConfigException("unbind_item.json 的道具不能和经验道具是同一个编号：" + itemId);
        }

        var rawTiers = raw.Tiers;
        if (rawTiers is null || rawTiers.Count == 0)
        {
            throw new ConfigException("unbind_item.json 的 unbind.tiers 不能为空");
        }

        var tiers = new List<Tier>();
        var seenHours = new HashSet<int>();
        foreach (var t in rawTiers)
        {
            if (t is null)
            {
                throw new ConfigException("unbind_item.json 的 unbind.tiers 有空档");
            }

            if (t.MinBoundHours < 0)
            {
                throw new ConfigException("unbind_item.json 的档位时长不能是负数："
                    + t.MinBoundHours);
            }

            if (t.Cost < 1)
            {
                throw new ConfigException("unbind_item.json 的档位张数必须是正整数："
                    + t.Cost);
            }

            if (!seenHours.Add(t.MinBoundHours))
            {
                throw new ConfigException("unbind_item.json 的档位时长重复："
                    + t.MinBoundHours);
            }

            tiers.Add(new Tier(t.MinBoundHours, t.Cost));
        }

        // 从大到小排，CostFor 顺序扫；同时校验「绑得越久张数越少」：
        // 排好之后张数只能随时长变短而不减，长得更久的档不许比更短的档还贵。
        tiers.Sort((a, b) => b.MinBoundHours.CompareTo(a.MinBoundHours));
        for (var i = 1; i < tiers.Count; i++)
        {
            if (tiers[i].Cost < tiers[i - 1].Cost)
            {
                throw new ConfigException("unbind_item.json 的档位张数必须绑得越久越少："
                    + tiers[i - 1].MinBoundHours + "h 用 " + tiers[i - 1].Cost
                    + " 张，但更短的 " + tiers[i].MinBoundHours + "h 却要 " + tiers[i].Cost + " 张");
            }
        }

        // 最低档必须是 0 小时：否则新绑的装备没有可用的档，等于「刚绑上就解不了」。
        if (tiers[^1].MinBoundHours != 0)
        {
            throw new ConfigException("unbind_item.json 的档位必须覆盖 0 小时（最低一档 min_bound_hours=0）");
        }

        return new UnbindSpec(itemId, name, tiers, (raw.Note ?? "").Trim());
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("unbind")] public WireUnbind? Unbind { get; set; }
    }

    sealed class WireUnbind
    {
        [JsonPropertyName("item_id")] public string? ItemId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("tiers")] public List<WireTier?>? Tiers { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }

    sealed class WireTier
    {
        [JsonPropertyName("min_bound_hours")] public int MinBoundHours { get; set; }
        [JsonPropertyName("cost")] public int Cost { get; set; }
    }
}
