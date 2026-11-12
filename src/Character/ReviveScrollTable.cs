using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 复活卷轴的唯一正本（`config/revive_scroll.json`）。方案 12.1 写定：
/// 「绑定的自救消耗，只拉起自己，不能拉队友……拉起后按配置回到一小段生命，
/// 不满血，不回蓝，不加属性。只扣当前等级已有经验的一部分，不掉级。比例按
/// 一转基础，不吃生息二转减免。冷却记在当前玩家身上，跨场保留，时间长。」
///
/// 这里只写卷轴自己：编号、名字、冷却时长。冷却的**到期时刻**不写在配置里，
/// 也不写在战斗进程内存里——技术架构第 104 行点名放进角色 schema，由角色服
/// 用掉那一张时落库（`db/character/revive_scroll.sql`），换一场仍读同一行。
///
/// 拉起回到的一小段生命与经验扣除比例**不在这里重复写数**：两者取
/// `config/classes.json` 的 `resurrect` 段，那是生息与卷轴共用的「一转基础比例」。
/// 二转减免（P2-4）只会改生息那一条，卷轴永远读这一份基础值，天然吃不到减免。
/// 数值不在代码里写死，改配置重启生效；坏表在启动时就起不来。
/// </summary>
public sealed class ReviveScrollTable
{
    public const string WrittenSchema = "revive-scroll-table/1";

    /// <summary>卷轴那一段：编号、名字与全局冷却时长。</summary>
    public sealed record Scroll(string ItemId, string Name, int CooldownMs, string Note);

    public int Version { get; }
    public string Note { get; }
    public Scroll ReviveScroll { get; }

    /// <summary>拉起后回到的一小段生命，占生命上限的百分比。必须小于 100（不满血）。</summary>
    public int HpRestorePct { get; }

    /// <summary>只扣当前等级已有经验的这个百分比；等级与上一等级经验都不动。</summary>
    public int ExpCostPct { get; }

    ReviveScrollTable(
        int version, string note, Scroll scroll, int hpRestorePct, int expCostPct)
    {
        Version = version;
        Note = note;
        ReviveScroll = scroll;
        HpRestorePct = hpRestorePct;
        ExpCostPct = expCostPct;
    }

    /// <summary>这个物品编号是不是卷轴。不是就返回 null，调用方据此拒绝。</summary>
    public Scroll? Find(string itemId) =>
        string.Equals(ReviveScroll.ItemId, itemId, StringComparison.Ordinal)
            ? ReviveScroll
            : null;

    /// <summary>
    /// classTablePath 传的就是 `config/classes.json`（角色服建号时读的同一份）。
    /// talismans 用来挡「卷轴和符写成同一个编号」——符和卷轴是两种东西，
    /// 解绑道具对它们也解不开（P1-5c8），编号混了后面几张卡都会串。
    /// </summary>
    public static ReviveScrollTable Load(
        string path, string classTablePath, ItemTable items, TalismanTable talismans)
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
            throw new ConfigException("revive_scroll.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("revive_scroll.json 的 version 必须是正整数");
        }

        var scroll = LoadScroll(wire.Scroll, items, talismans);
        var (hpPct, expPct) = LoadBase(classTablePath);

        return new ReviveScrollTable(
            wire.Version, (wire.Note ?? "").Trim(), scroll, hpPct, expPct);
    }

    static Scroll LoadScroll(WireScroll? raw, ItemTable items, TalismanTable talismans)
    {
        if (raw is null)
        {
            throw new ConfigException("revive_scroll.json 缺 scroll 段");
        }

        var itemId = (raw.ItemId ?? "").Trim();
        var name = (raw.Name ?? "").Trim();
        if (itemId.Length == 0)
        {
            throw new ConfigException("revive_scroll.json 的 scroll.item_id 不能是空的");
        }

        if (name.Length == 0)
        {
            throw new ConfigException("revive_scroll.json 的 scroll.name 不能是空的");
        }

        var goods = items.Find(itemId);
        if (goods is null)
        {
            throw new ConfigException("revive_scroll.json 的卷轴不在 items.json 里：" + itemId);
        }

        // 方案 12.1：复活卷轴「购买后绑定」，且是消耗品，不是装备、材料或凭证。
        if (!string.Equals(goods.Kind, "consumable", StringComparison.Ordinal))
        {
            throw new ConfigException("revive_scroll.json 的卷轴必须是 consumable：" + goods.Kind);
        }

        if (goods.Bind != BindRule.Always)
        {
            throw new ConfigException("revive_scroll.json 的卷轴必须到账即绑："
                + goods.Id + " 的 bind 是 " + goods.Bind);
        }

        if (talismans.Find(itemId) is not null)
        {
            throw new ConfigException("revive_scroll.json 的卷轴不能和符是同一个编号：" + itemId);
        }

        if (raw.CooldownMs < 1)
        {
            throw new ConfigException("revive_scroll.json 的 scroll.cooldown_ms 必须是正整数");
        }

        return new Scroll(itemId, name, raw.CooldownMs, (raw.Note ?? "").Trim());
    }

    /// <summary>
    /// 只从职业表里读「一转基础」的两个比例，不在本文件里抄一份数字。
    /// hp_restore_pct 必须小于 100——方案写定「拉起后不满血」；
    /// exp_cost_pct 也是「一部分」，不许配成 0 或 100（配 100 等于把当前级清空）。
    /// 二转减免将来另配给生息，卷轴不读那一项，所以永远按基础比例扣。
    /// </summary>
    static (int HpPct, int ExpPct) LoadBase(string classTablePath)
    {
        if (!File.Exists(classTablePath))
        {
            throw new ConfigException("找不到职业表：" + classTablePath);
        }

        int hpPct;
        int expPct;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(classTablePath));
            var resurrect = doc.RootElement.GetProperty("resurrect");
            hpPct = resurrect.GetProperty("hp_restore_pct").GetInt32();
            expPct = resurrect.GetProperty("exp_cost_pct").GetInt32();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException
                                      or InvalidOperationException or FormatException)
        {
            throw new ConfigException("职业表读不出 resurrect 的两个基础比例：" + ex.Message);
        }

        if (hpPct < 1 || hpPct >= 100)
        {
            throw new ConfigException("classes.json 的 resurrect.hp_restore_pct 要在 [1,100)："
                + hpPct + "，方案写定拉起后不满血");
        }

        if (expPct < 1 || expPct >= 100)
        {
            throw new ConfigException("classes.json 的 resurrect.exp_cost_pct 要在 [1,100)："
                + expPct + "，方案写定只扣当前等级已有经验的一部分");
        }

        return (hpPct, expPct);
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
        [JsonPropertyName("scroll")] public WireScroll? Scroll { get; set; }
    }

    sealed class WireScroll
    {
        [JsonPropertyName("item_id")] public string? ItemId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("cooldown_ms")] public int CooldownMs { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
