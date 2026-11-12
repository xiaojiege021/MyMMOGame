using System.Text.Json;
using System.Text.Json.Serialization;
using Mmo.Proto.Character;

namespace Character;

/// <summary>
/// 符类消耗品的唯一正本（`config/talisman.json`）。方案 12.1 写定：
/// 红符是「绑定的生命回复消耗」——只回复自己，回复不超过同级回春丹 / 气血丹，
/// 和红药、牧师治疗共用冷却，不复活、不治疗队友，不和蓝符共用冷却；
/// 蓝符是「绑定的蓝量回复消耗」——只回复自己的蓝，回复不超过同级凝神丹 / 回灵丹，
/// 和蓝药共用冷却，不回血、不复活、不提高精神、不增加伤害。
///
/// 这张表只管符自己，丹药那边一个数都不写：`potion_hp_restore` /
/// `potion_mana_restore` 是「同级药的回复上限」，唯一用途是卡住符的回复不许超过它。
/// 丹药做出来以后对齐这两个字段，不在别处另写一套回复量。
///
/// 校验放在角色服（持久数据的唯一写入方）：配置不对就起不来，
/// 战斗服不自己解析这份文件，回复值与冷却都由扣符回包带过去。
/// 哪个编号是哪种符也只由这里说了算——战斗服先走一次只读查询，再决定加血还是加蓝。
/// </summary>
public sealed class TalismanTable
{
    public const string WrittenSchema = "talisman-table/1";

    // 方案 12.1 写定「和红药、牧师治疗共用冷却」；蓝符「和蓝药共用冷却」，
    // 且红符「不和蓝符共用冷却」。两组必须不同，开发架构第 14 节第 44 行
    // 才写得出「和红符可以各用一次」。
    public const string WrittenRedCooldownGroup = "heal";
    public const string WrittenBlueCooldownGroup = "mana";

    /// <summary>红符：回复值、同级红药上限、共用冷却组与时长。</summary>
    public sealed record Red(
        string ItemId,
        string Name,
        long HpRestore,
        long PotionHpRestore,
        string CooldownGroup,
        int CooldownMs,
        string Note);

    /// <summary>蓝符：回复值、同级蓝药上限、共用冷却组与时长。</summary>
    public sealed record Blue(
        string ItemId,
        string Name,
        long ManaRestore,
        long PotionManaRestore,
        string CooldownGroup,
        int CooldownMs,
        string Note);

    /// <summary>
    /// 给扣符回包用的统一形状。Pool 决定战斗服加到血还是蓝，另一个回复值恒 0——
    /// 「蓝符不回血」不是靠一句约定，是这里只给一个数。
    /// </summary>
    public sealed record TalismanSpec(
        string ItemId,
        TalismanPool Pool,
        long HpRestore,
        long ManaRestore,
        int CooldownMs,
        string CooldownGroup);

    public int Version { get; }
    public string Note { get; }
    public Red? RedTalisman { get; }
    public Blue? BlueTalisman { get; }

    TalismanTable(int version, string note, Red? red, Blue? blue)
    {
        Version = version;
        Note = note;
        RedTalisman = red;
        BlueTalisman = blue;
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

    /// <summary>这个物品编号是不是蓝符。不是就返回 null，调用方据此拒绝。</summary>
    public Blue? FindBlue(string itemId)
    {
        if (BlueTalisman is null)
        {
            return null;
        }

        return string.Equals(BlueTalisman.ItemId, itemId, StringComparison.Ordinal)
            ? BlueTalisman
            : null;
    }

    /// <summary>
    /// 按物品编号找符。找得到就带着「哪个池、回多少、冷却多久」一起回来；
    /// 认不出的编号一律 null，调用方按 not_talisman 拒绝，不扣也不回。
    /// </summary>
    public TalismanSpec? Find(string itemId)
    {
        var red = FindRed(itemId);
        if (red is not null)
        {
            return new TalismanSpec(red.ItemId, TalismanPool.Hp, red.HpRestore, 0,
                red.CooldownMs, red.CooldownGroup);
        }

        var blue = FindBlue(itemId);
        if (blue is not null)
        {
            return new TalismanSpec(blue.ItemId, TalismanPool.Mana, 0, blue.ManaRestore,
                blue.CooldownMs, blue.CooldownGroup);
        }

        return null;
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

        var red = LoadRed(wire.Red, items);
        var blue = LoadBlue(wire.Blue, items);

        if (string.Equals(red.ItemId, blue.ItemId, StringComparison.Ordinal))
        {
            throw new ConfigException("talisman.json 的红符与蓝符不能是同一个物品编号："
                + red.ItemId);
        }

        if (string.Equals(red.CooldownGroup, blue.CooldownGroup, StringComparison.Ordinal))
        {
            throw new ConfigException("talisman.json 的红符与蓝符共用了一个冷却组："
                + red.CooldownGroup + "——方案 12.1 写定不和蓝符共用冷却");
        }

        return new TalismanTable(
            wire.Version,
            (wire.Note ?? "").Trim(),
            red,
            blue);
    }

    static Red LoadRed(WireRed? raw, ItemTable items)
    {
        if (raw is null)
        {
            // 红符是这张表的基本一种，缺了就等于这张卡什么都没做。
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

        // 方案 12.1：红符购买后绑定。
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
        // 这条是 P1-5c3 的完成标准，不能在运行时才拦。
        if (raw.HpRestore > raw.PotionHpRestore)
        {
            throw new ConfigException("talisman.json 的红符回复 " + raw.HpRestore
                + " 超过了同级红药的 " + raw.PotionHpRestore
                + "，方案 12.1 写定回复不超过同级红药");
        }

        var group = CheckGroup(raw.CooldownGroup, WrittenRedCooldownGroup, "red");

        if (raw.CooldownMs < 1)
        {
            throw new ConfigException("talisman.json 的 red.cooldown_ms 必须是正整数");
        }

        return new Red(itemId, name, raw.HpRestore, raw.PotionHpRestore, group,
            raw.CooldownMs, (raw.Note ?? "").Trim());
    }

    static Blue LoadBlue(WireBlue? raw, ItemTable items)
    {
        if (raw is null)
        {
            throw new ConfigException("talisman.json 缺 blue 段");
        }

        var itemId = (raw.ItemId ?? "").Trim();
        var name = (raw.Name ?? "").Trim();

        if (itemId.Length == 0)
        {
            throw new ConfigException("talisman.json 的 blue.item_id 不能是空的");
        }

        if (name.Length == 0)
        {
            throw new ConfigException("talisman.json 的 blue.name 不能是空的");
        }

        var goods = items.Find(itemId);
        if (goods is null)
        {
            throw new ConfigException("talisman.json 的蓝符不在 items.json 里：" + itemId);
        }

        if (!string.Equals(goods.Kind, "consumable", StringComparison.Ordinal))
        {
            throw new ConfigException("talisman.json 的蓝符必须是 consumable：" + goods.Kind);
        }

        // 方案 12.1：蓝符购买后绑定，副本、任务、月叠、通行证都不发。
        if (goods.Bind != BindRule.Always)
        {
            throw new ConfigException("talisman.json 的蓝符必须到账即绑："
                + goods.Id + " 的 bind 是 " + goods.Bind);
        }

        if (raw.ManaRestore < 1)
        {
            throw new ConfigException("talisman.json 的 blue.mana_restore 必须是正整数");
        }

        if (raw.PotionManaRestore < 1)
        {
            throw new ConfigException("talisman.json 的 blue.potion_mana_restore 必须是正整数");
        }

        // 方案 12.1 写定「回复不超过同级凝神丹、回灵丹」——开发架构第 14 节
        // 第 44 行的完成标准之一。超上限的配置不许起来。
        if (raw.ManaRestore > raw.PotionManaRestore)
        {
            throw new ConfigException("talisman.json 的蓝符回复 " + raw.ManaRestore
                + " 超过了同级蓝药的 " + raw.PotionManaRestore
                + "，方案 12.1 写定回复不超过同级蓝药");
        }

        var group = CheckGroup(raw.CooldownGroup, WrittenBlueCooldownGroup, "blue");

        if (raw.CooldownMs < 1)
        {
            throw new ConfigException("talisman.json 的 blue.cooldown_ms 必须是正整数");
        }

        return new Blue(itemId, name, raw.ManaRestore, raw.PotionManaRestore, group,
            raw.CooldownMs, (raw.Note ?? "").Trim());
    }

    static string CheckGroup(string? raw, string written, string which)
    {
        var group = (raw ?? "").Trim();
        if (!string.Equals(group, written, StringComparison.Ordinal))
        {
            throw new ConfigException("talisman.json 的 " + which
                + ".cooldown_group 必须是 " + written + "——方案写定红符和红药、牧师治疗共用，"
                + "蓝符和蓝药共用，两组不许并起来：" + group);
        }

        return group;
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
        [JsonPropertyName("blue")] public WireBlue? Blue { get; set; }
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

    sealed class WireBlue
    {
        [JsonPropertyName("item_id")] public string? ItemId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("mana_restore")] public long ManaRestore { get; set; }
        [JsonPropertyName("potion_mana_restore")] public long PotionManaRestore { get; set; }
        [JsonPropertyName("cooldown_group")] public string? CooldownGroup { get; set; }
        [JsonPropertyName("cooldown_ms")] public int CooldownMs { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
