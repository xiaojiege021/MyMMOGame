using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 符类消耗品的唯一正本（`config/talisman.json`）。方案 12.1 写定：
/// 红符是「绑定的生命回复消耗」——只回复自己，回复不超过同级回春丹 / 气血丹，
/// 和红药、牧师治疗共用冷却，不复活、不治疗队友，不和蓝符共用冷却。
///
/// 这张表只管红符自己，丹药那边一个数都不写：`potion_hp_restore` 是
/// 「同级红药的回复上限」，唯一用途是卡住 `hp_restore` 不许超过它。
/// 丹药做出来以后对齐这一个字段，不在别处另写一套回复量。
///
/// 校验放在角色服（持久数据的唯一写入方）：配置不对就起不来，
/// 战斗服不自己解析这份文件，回复值与冷却都由扣符回包带过去。
/// </summary>
public sealed class TalismanTable
{
    public const string WrittenSchema = "talisman-table/1";

    // 方案 12.1 写定「和红药、牧师治疗共用冷却」。蓝符是另一组（mana），
    // 两段互不占用；那一段等 P1-5c5 再接。
    public const string WrittenRedCooldownGroup = "heal";

    /// <summary>红符：回复值、同级红药上限、共用冷却组与时长。</summary>
    public sealed record Red(
        string ItemId,
        string Name,
        long HpRestore,
        long PotionHpRestore,
        string CooldownGroup,
        int CooldownMs,
        string Note);

    public int Version { get; }
    public string Note { get; }
    public Red? RedTalisman { get; }

    TalismanTable(int version, string note, Red? red)
    {
        Version = version;
        Note = note;
        RedTalisman = red;
    }

    /// <summary>这个物品编号是不是红符。不是就返回 null，调用方据此拒绝。</summary>
    public Red? FindRed(string itemId)
    {
        if (RedTalisman is null)
        {
            return null;
        }

        return string.Equals(RedTalisman.ItemId, itemId, StringComparison.Ordinal)
            ? RedTalisman
            : null;
    }

    public static TalismanTable Load(string path, ItemTable items)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配置文件 " + path);
        }

        var text = File.ReadAllText(path);
        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(text, JsonOptions)
                ?? throw new ConfigException("配置是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配置不是合法 JSON：" + ex.Message);
        }

        if (!string.Equals(wire.Schema, WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException("talisman.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("talisman.json 的 version 必须是正整数");
        }

        var raw = wire.Red;
        if (raw is null)
        {
            // 红符是这张卡唯一的一种，缺了就等于这张卡什么都没做。
            throw new ConfigException("talisman.json 缺 red 段");
        }

        var itemId = (raw.ItemId ?? "").Trim();
        var name = (raw.Name ?? "").Trim();

        if (itemId.Length == 0)
        {
            throw new ConfigException("talisman.json 的 red.item_id 不能是空的");
        }

        if (name.Length == 0)
        {
            throw new ConfigException("talisman.json 的 red.name 不能是空的");
        }

        var goods = items.Find(itemId);
        if (goods is null)
        {
            throw new ConfigException("talisman.json 的红符不在 items.json 里：" + itemId);
        }

        // 红符是消耗品，不是装备、材料或凭证。
        if (!string.Equals(goods.Kind, "consumable", StringComparison.Ordinal))
        {
            throw new ConfigException("talisman.json 的红符必须是 consumable：" + goods.Kind);
        }

        // 方案 12.1：红符购买后绑定。到账不绑的话买来的符能直接上拍卖行，
        // 和「红符、蓝符和复活卷轴副本和任务都不产出」这条边界一起就漏了。
        if (goods.Bind != BindRule.Always)
        {
            throw new ConfigException("talisman.json 的红符必须到账即绑："
                + goods.Id + " 的 bind 是 " + goods.Bind);
        }

        if (raw.HpRestore < 1)
        {
            throw new ConfigException("talisman.json 的 red.hp_restore 必须是正整数");
        }

        if (raw.PotionHpRestore < 1)
        {
            throw new ConfigException("talisman.json 的 red.potion_hp_restore 必须是正整数");
        }

        // 方案 12.1 写定「回复不超过同级回春丹、气血丹」。超了就起不来——
        // 这条是本卡的完成标准，不能在运行时才拦。
        if (raw.HpRestore > raw.PotionHpRestore)
        {
            throw new ConfigException("talisman.json 的红符回复 " + raw.HpRestore
                + " 超过了同级红药的 " + raw.PotionHpRestore
                + "，方案 12.1 写定回复不超过同级红药");
        }

        var group = (raw.CooldownGroup ?? "").Trim();
        if (!string.Equals(group, WrittenRedCooldownGroup, StringComparison.Ordinal))
        {
            throw new ConfigException("talisman.json 的 red.cooldown_group 必须是 "
                + WrittenRedCooldownGroup + "——方案写定红符和红药、牧师治疗共用冷却；"
                + "蓝符用另一组，不许并进来：" + group);
        }

        if (raw.CooldownMs < 1)
        {
            throw new ConfigException("talisman.json 的 red.cooldown_ms 必须是正整数");
        }

        return new TalismanTable(
            wire.Version,
            (wire.Note ?? "").Trim(),
            new Red(itemId, name, raw.HpRestore, raw.PotionHpRestore, group,
                raw.CooldownMs, (raw.Note ?? "").Trim()));
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
        [JsonPropertyName("red")] public WireRed? Red { get; set; }
    }

    sealed class WireRed
    {
        [JsonPropertyName("item_id")] public string? ItemId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("hp_restore")] public long HpRestore { get; set; }
        [JsonPropertyName("potion_hp_restore")] public long PotionHpRestore { get; set; }
        [JsonPropertyName("cooldown_group")] public string? CooldownGroup { get; set; }
        [JsonPropertyName("cooldown_ms")] public int CooldownMs { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
