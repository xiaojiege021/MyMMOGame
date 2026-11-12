using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 限时经验道具的唯一正本（`config/exp_booster.json`）。方案 12.1 写定：
/// 「绑定的一段时间经验提高……只用在经验结算上。不加伤害、掉落、疲劳和副本次数。
/// 时间走配置，到点消失。同时只生效一个，再用一张只刷新剩余时间，倍率不叠。
/// 不能把等级写出 100。」
///
/// 这里只写道具自己：编号、名字、一段时长、结算倍率。到期时刻不写在配置里，
/// 也不写在任何进程内存里——技术架构第 104 行点名放进角色 schema，由角色服
/// 用掉那一张时落库（`db/character/exp_booster.sql`），换进程 / 重启仍读同一行。
/// 那一行只有到期时刻、没有倍率，所以「同时只生效一个、倍率不叠」是结构上落死的。
///
/// 基础经验与组队加成不在这里抄第二套，仍是 `config/exp.json`（P1-2d）的正本；
/// 等级封顶也仍只有 `ExpTable.Apply` 一处。时长与倍率方案没给数字，全部标待定，
/// 代码不写死；坏表在启动与只读入口就起不来。
/// </summary>
public sealed class ExpBoosterTable
{
    public const string WrittenSchema = "exp-booster-table/1";

    /// <summary>道具那一段。gain_pct 是「基础 + 组队」这一笔的结算倍率百分比。</summary>
    public sealed record Booster(
        string ItemId, string Name, int DurationMs, int GainPct, string Note);

    public int Version { get; }
    public string Note { get; }
    public Booster ExpBooster { get; }

    ExpBoosterTable(int version, string note, Booster booster)
    {
        Version = version;
        Note = note;
        ExpBooster = booster;
    }

    /// <summary>这个物品编号是不是限时经验道具。不是就返回 null，调用方据此拒绝。</summary>
    public Booster? Find(string itemId) =>
        string.Equals(ExpBooster.ItemId, itemId, StringComparison.Ordinal)
            ? ExpBooster
            : null;

    /// <summary>
    /// talismans / scrolls 用来挡「道具和符、卷轴写成同一个编号」——三种是三种东西，
    /// 解绑道具对符和卷轴也解不开（P1-5c8），编号混了后面几张卡都会串。
    /// </summary>
    public static ExpBoosterTable Load(
        string path, ItemTable items, TalismanTable talismans, ReviveScrollTable scrolls)
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
            throw new ConfigException("exp_booster.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("exp_booster.json 的 version 必须是正整数");
        }

        var booster = LoadBooster(wire.Booster, items, talismans, scrolls);
        return new ExpBoosterTable(wire.Version, (wire.Note ?? "").Trim(), booster);
    }

    static Booster LoadBooster(
        WireBooster? raw, ItemTable items, TalismanTable talismans, ReviveScrollTable scrolls)
    {
        if (raw is null)
        {
            throw new ConfigException("exp_booster.json 缺 booster 段");
        }

        var itemId = (raw.ItemId ?? "").Trim();
        var name = (raw.Name ?? "").Trim();
        if (itemId.Length == 0)
        {
            throw new ConfigException("exp_booster.json 的 booster.item_id 不能是空的");
        }

        if (name.Length == 0)
        {
            throw new ConfigException("exp_booster.json 的 booster.name 不能是空的");
        }

        var goods = items.Find(itemId);
        if (goods is null)
        {
            throw new ConfigException("exp_booster.json 的道具不在 items.json 里：" + itemId);
        }

        // 方案 12.1：限时经验道具「购买后绑定」，且是消耗品，不是装备、材料或凭证。
        if (!string.Equals(goods.Kind, "consumable", StringComparison.Ordinal))
        {
            throw new ConfigException("exp_booster.json 的道具必须是 consumable：" + goods.Kind);
        }

        if (goods.Bind != BindRule.Always)
        {
            throw new ConfigException("exp_booster.json 的道具必须到账即绑："
                + goods.Id + " 的 bind 是 " + goods.Bind);
        }

        if (talismans.Find(itemId) is not null)
        {
            throw new ConfigException("exp_booster.json 的道具不能和符是同一个编号：" + itemId);
        }

        if (scrolls.Find(itemId) is not null)
        {
            throw new ConfigException("exp_booster.json 的道具不能和卷轴是同一个编号：" + itemId);
        }

        if (raw.DurationMs < 1)
        {
            throw new ConfigException("exp_booster.json 的 booster.duration_ms 必须是正整数");
        }

        // 100 就是原倍率，配成 100 或更小等于「用了没提高经验」，与方案写定的
        // 「一段时间提高经验」相反；不设上限，数值待定由配置决定，不在代码里写死。
        if (raw.GainPct <= 100)
        {
            throw new ConfigException("exp_booster.json 的 booster.gain_pct 必须大于 100："
                + raw.GainPct);
        }

        return new Booster(itemId, name, raw.DurationMs, raw.GainPct,
            (raw.Note ?? "").Trim());
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
        [JsonPropertyName("booster")] public WireBooster? Booster { get; set; }
    }

    sealed class WireBooster
    {
        [JsonPropertyName("item_id")] public string? ItemId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }
        [JsonPropertyName("gain_pct")] public int GainPct { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
