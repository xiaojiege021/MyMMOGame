using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 丹药的唯一正本（`config/pill.json`）。方案 12.1 写定六种丹：
///   · 回春丹、气血丹回复生命（红药）；
///   · 凝神丹、回灵丹回复蓝量（蓝药）；
///   · 筑基丹、破镜丹用于境界突破，不进战斗。
/// 都从副本、炼丹和特殊任务来，可以进拍卖；商城不卖这些丹，也不能代替复活。
///
/// 这张表只管丹自己：回复值、落到哪个池、哪一组冷却、哪一档。
/// 「符不超过同级丹」的另一半在 TalismanTable 里，两份文件靠加载期互校钉死——
/// 同级最强那一档丹的回复值必须等于 talisman.json 的 potion_hp_restore /
/// potion_mana_restore，谁也不许各写一套数。
///
/// 突破丹没有池也没有冷却：它们只在角色服的境界突破事务里扣，
/// 哪一段用哪一种、扣几枚写在 config/realms.json 的 advance.pill / advance.pill_count，
/// 这里只校验「配到突破上的编号真的是突破丹、每一种突破丹真的被某一段用到」。
///
/// 校验放在角色服（持久数据的唯一写入方）：配置不对就起不来。
/// 战斗服不解析这份文件，池、回复值与冷却都由 DescribePill / ConsumePill 的回包带过去。
/// </summary>
public sealed class PillTable
{
    public const string WrittenSchema = "pill-table/1";

    // 与 TalismanTable 的写法一致：红药和红符、牧师治疗共用 heal 组，
    // 蓝药和蓝符共用 mana 组。方案 12.1 写定两符「不和对方共用冷却」，
    // 丹顺着自己那一侧走，所以丹与符同组就是「和符共用冷却」。
    public const string WrittenHpGroup = "heal";
    public const string WrittenManaGroup = "mana";

    public const string KindHp = "hp";
    public const string KindMana = "mana";
    public const string KindBreakthrough = "breakthrough";

    /// <summary>
    /// 一种丹。HpRestore / ManaRestore 只有一侧非零（哪一侧由 Kind 决定），
    /// 突破丹两侧都是 0、冷却也是 0——它不进战斗，回包里不会出现它。
    /// </summary>
    public sealed record PillSpec(
        string ItemId,
        string Name,
        string Kind,
        int Tier,
        long HpRestore,
        long ManaRestore,
        int CooldownMs,
        string CooldownGroup,
        string Note)
    {
        public bool IsBreakthrough => Kind == KindBreakthrough;
    }

    readonly Dictionary<string, PillSpec> _byId;

    public int Version { get; }
    public string Note { get; }
    public IReadOnlyList<PillSpec> All { get; }

    PillTable(int version, string note, List<PillSpec> pills)
    {
        Version = version;
        Note = note;
        All = pills;
        _byId = new Dictionary<string, PillSpec>(StringComparer.Ordinal);
        foreach (var pill in pills)
        {
            _byId[pill.ItemId] = pill;
        }
    }

    /// <summary>这个编号是不是丹。不是就返回 null，调用方按 not_pill 拒绝。</summary>
    public PillSpec? Find(string itemId) => _byId.GetValueOrDefault(itemId ?? "");

    /// <summary>突破丹的编号集合（把编号配到 realms.json 的段上时用来校验）。</summary>
    public IEnumerable<PillSpec> BreakthroughPills =>
        All.Where(x => x.IsBreakthrough);

    /// <summary>同一种池里回复最高的那一档——符的上限就取它（只读入口也用它）。</summary>
    public long Strongest(string kind) =>
        All.Where(x => x.Kind == kind).Select(x =>
            kind == KindHp ? x.HpRestore : x.ManaRestore).DefaultIfEmpty(0).Max();

    /// <summary>
    /// 加载并交叉校验。三份文件必须对得上：
    ///   1. 每条丹的物品必须在 items.json 里，是消耗品，到账不绑（方案 12.1：可以上拍卖）；
    ///   2. 红药 / 蓝药必须有正整数回复值，冷却组必须是 heal / mana，冷却时长必须为正；
    ///   3. 符的上限必须就是同级最强那一档丹的回复值（talisman.json 与 pill.json 互校），
    ///      并且符自己的回复不许超过它；
    ///   4. 突破丹必须被 realms.json 的某一段用到，realms.json 上配的丹必须是突破丹。
    /// </summary>
    public static PillTable Load(string path, ItemTable items, TalismanTable talismans, RealmTable realms)
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
            throw new ConfigException("pill.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("pill.json 的 version 必须是正整数");
        }

        var rows = wire.Pills ?? new List<WirePill>();
        if (rows.Count == 0)
        {
            throw new ConfigException("pill.json 一颗丹都没有");
        }

        var pills = new List<PillSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rows)
        {
            var itemId = (raw.ItemId ?? "").Trim();
            if (itemId.Length == 0)
            {
                throw new ConfigException("pill.json 有丹药缺 item_id");
            }

            if (!seen.Add(itemId))
            {
                throw new ConfigException("pill.json 的丹药编号重复：" + itemId);
            }

            var goods = items.Find(itemId)
                ?? throw new ConfigException("pill.json 的丹药不在 items.json 里：" + itemId);

            if (!string.Equals(goods.Kind, "consumable", StringComparison.Ordinal))
            {
                throw new ConfigException("pill.json 的丹药必须是 consumable："
                    + itemId + " 的 kind 是 " + goods.Kind);
            }

            // 方案 12.1：这些丹不在商城和 NPC 商店出售，从副本 / 炼丹 / 特殊任务来，可以进拍卖。
            // 到账即绑的丹上不了拍卖，所以绑定口径也必须对上。
            if (goods.Bind != BindRule.None)
            {
                throw new ConfigException("pill.json 的丹药必须到账不绑（可以上拍卖）："
                    + itemId + " 的 bind 是 " + goods.Bind);
            }

            var kind = (raw.Kind ?? "").Trim();
            var cooldownGroup = (raw.CooldownGroup ?? "").Trim();
            var cooldownMs = raw.CooldownMs ?? 0;

            if (kind == KindBreakthrough)
            {
                // 突破丹不进战斗：带回复值或冷却就是配错了，不静默忽略。
                if ((raw.Restore ?? 0) != 0 || cooldownGroup.Length != 0 || cooldownMs != 0)
                {
                    throw new ConfigException("pill.json 的突破丹不该有回复值或冷却：" + itemId);
                }
            }
            else if (kind == KindHp || kind == KindMana)
            {
                if ((raw.Restore ?? 0) < 1)
                {
                    throw new ConfigException("pill.json 的 " + itemId + " 回复值必须是正整数");
                }

                var expected = kind == KindHp ? WrittenHpGroup : WrittenManaGroup;
                if (!string.Equals(cooldownGroup, expected, StringComparison.Ordinal))
                {
                    throw new ConfigException("pill.json 的 " + itemId
                        + " 的 cooldown_group 必须是 " + expected
                        + "——方案写定红药和红符、牧师治疗共用，蓝药和蓝符共用：" + cooldownGroup);
                }

                if (cooldownMs < 1)
                {
                    throw new ConfigException("pill.json 的 " + itemId + " cooldown_ms 必须是正整数");
                }

                if ((raw.Tier ?? 0) < 1)
                {
                    throw new ConfigException("pill.json 的 " + itemId + " tier 必须是正整数");
                }
            }
            else
            {
                throw new ConfigException("pill.json 的 " + itemId
                    + " kind 只认 hp / mana / breakthrough：" + kind);
            }

            pills.Add(new PillSpec(
                itemId,
                goods.Name,
                kind,
                raw.Tier ?? 0,
                kind == KindHp ? raw.Restore ?? 0 : 0,
                kind == KindMana ? raw.Restore ?? 0 : 0,
                kind == KindBreakthrough ? 0 : cooldownMs,
                kind == KindBreakthrough ? "" : cooldownGroup,
                (raw.Note ?? "").Trim()));
        }

        var table = new PillTable(wire.Version, (wire.Note ?? "").Trim(), pills);
        table.CheckAgainstTalismans(talismans);
        table.CheckAgainstRealms(realms);
        return table;
    }

    /// <summary>
    /// 符的上限就是同级最强那一档丹：两个数必须相等。
    /// 相等才谈得上「符不超过同级丹」——不等就说明有一份文件在写自己的数。
    /// </summary>
    void CheckAgainstTalismans(TalismanTable talismans)
    {
        var hp = Strongest(KindHp);
        var mana = Strongest(KindMana);

        if (hp < 1 || mana < 1)
        {
            throw new ConfigException("pill.json 必须至少有一颗红药和一颗蓝药");
        }

        var red = talismans.RedTalisman
            ?? throw new ConfigException("talisman.json 缺红符，没法对同级丹的上限");
        var blue = talismans.BlueTalisman
            ?? throw new ConfigException("talisman.json 缺蓝符，没法对同级丹的上限");

        if (red.PotionHpRestore != hp)
        {
            throw new ConfigException("talisman.json 的红符上限 potion_hp_restore="
                + red.PotionHpRestore + " 与同级最强红药 " + hp
                + " 不一致——方案 12.1 写定符不超过同级丹，两个数只能有一份");
        }

        if (blue.PotionManaRestore != mana)
        {
            throw new ConfigException("talisman.json 的蓝符上限 potion_mana_restore="
                + blue.PotionManaRestore + " 与同级最强蓝药 " + mana + " 不一致");
        }

        if (red.HpRestore > hp)
        {
            throw new ConfigException("红符回复 " + red.HpRestore + " 超过了同级红药 " + hp);
        }

        if (blue.ManaRestore > mana)
        {
            throw new ConfigException("蓝符回复 " + blue.ManaRestore + " 超过了同级蓝药 " + mana);
        }
    }

    /// <summary>
    /// 突破丹与 realms.json 的 advance 必须对得上：
    /// 每一段配的丹得是突破丹，每一种突破丹至少被一段用到。
    /// </summary>
    void CheckAgainstRealms(RealmTable realms)
    {
        var breakthrough = new HashSet<string>(
            BreakthroughPills.Select(x => x.ItemId), StringComparer.Ordinal);

        foreach (var requirement in realms.Requirements)
        {
            var pill = (requirement.Pill ?? "").Trim();
            if (pill.Length == 0)
            {
                // 老配置（B1 之前的临时表）没有这一项：不算错，按「不需要丹」处理。
                continue;
            }

            if (!breakthrough.Contains(pill))
            {
                throw new ConfigException("realms.json 的 " + requirement.From
                    + " 突破配的丹不是 pill.json 的突破丹：" + pill);
            }

            if (requirement.PillCount < 1)
            {
                throw new ConfigException("realms.json 的 " + requirement.From
                    + " 突破丹枚数必须是正整数");
            }
        }

        foreach (var pill in breakthrough)
        {
            if (!realms.Requirements.Any(x => string.Equals(
                    (x.Pill ?? "").Trim(), pill, StringComparison.Ordinal)))
            {
                throw new ConfigException("pill.json 的突破丹没有被任何一段突破用到：" + pill);
            }
        }
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
        [JsonPropertyName("pills")] public List<WirePill>? Pills { get; set; }
    }

    sealed class WirePill
    {
        [JsonPropertyName("item_id")] public string? ItemId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("tier")] public int? Tier { get; set; }
        [JsonPropertyName("restore")] public long? Restore { get; set; }
        [JsonPropertyName("cooldown_group")] public string? CooldownGroup { get; set; }
        [JsonPropertyName("cooldown_ms")] public int? CooldownMs { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
