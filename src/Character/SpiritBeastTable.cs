using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// B4 灵兽正本（方案 9.4：普通、高级、神兽只做收集、外观和生活协助，
/// 不进入伤害公式）。本表只写三档、每只的 item_id 与生活协助万分比：
///   · 没有攻击 / 技能 / 出战 / 伤害字段，battle.json 里一条都不引用；
///   · 收集口径与外观收藏一致——背包里现在持有就算收集，所以不建第二张收集表；
///   · 生活协助沿用英灵图鉴的 passive_kind=life_assist，并与图鉴共用同一处上限：
///     hero_codex.json 的 max_total_passive_pct_bps。加载期硬校验
///     「灵兽合计 + 图鉴合计 ≤ 该上限」，本表不写第二份上限数字。
/// 数值方案没写定（占位、pending=true），改这份文件重启角色服生效。
/// </summary>
public sealed class SpiritBeastTable
{
    public const string WrittenSchema = "spirit-beast/1";
    public const string WrittenPassiveKind = "life_assist";
    /// <summary>三档，顺序就是高低（普通 → 高级 → 神兽）。</summary>
    public static readonly string[] TierIds = ["normal", "advanced", "divine"];
    public const int MaxEntryPassiveBps = 100;

    public sealed record Tier(string Id, string Name);

    public sealed record Entry(string ItemId, string Name, string Tier, int LifeAssistPctBps);

    public int Version { get; }
    public bool Pending { get; }
    public string PassiveKind { get; }
    public IReadOnlyList<Tier> Tiers { get; }
    public IReadOnlyList<Entry> Entries { get; }
    /// <summary>与图鉴共用的那处上限，只作只读展示与核对，判定在加载期做过。</summary>
    public int MaxTotalLifeAssistPctBps { get; }
    /// <summary>图鉴那九条合计，加载期算一次，回来给核对脚本与屏用。</summary>
    public int HeroCodexPassiveBps { get; }
    /// <summary>六只灵兽的合计。</summary>
    public int TotalLifeAssistPctBps { get; }

    SpiritBeastTable(
        int version, bool pending, string passiveKind,
        IReadOnlyList<Tier> tiers, IReadOnlyList<Entry> entries,
        int maxTotal, int heroCodexBps, int totalBps)
    {
        Version = version;
        Pending = pending;
        PassiveKind = passiveKind;
        Tiers = tiers;
        Entries = entries;
        MaxTotalLifeAssistPctBps = maxTotal;
        HeroCodexPassiveBps = heroCodexBps;
        TotalLifeAssistPctBps = totalBps;
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

    public static SpiritBeastTable Load(string path, ItemTable items, HeroCodexTable heroCodex)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到灵兽表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("spirit_beast.json 是空的");
        }
        catch (JsonException ex)
        {
            throw new ConfigException("spirit_beast.json 不是合法 JSON：" + ex.Message);
        }

        if ((wire.Schema ?? "").Trim() != WrittenSchema)
        {
            throw new ConfigException("spirit_beast.json schema 必须是 " + WrittenSchema);
        }

        var kind = (wire.PassiveKind ?? WrittenPassiveKind).Trim();
        if (!string.Equals(kind, WrittenPassiveKind, StringComparison.Ordinal))
        {
            throw new ConfigException("灵兽 passive_kind 只能是 " + WrittenPassiveKind);
        }

        if (wire.Tiers is null || wire.Tiers.Count != TierIds.Length)
        {
            throw new ConfigException("灵兽表必须正好 " + TierIds.Length + " 个档位（普通 / 高级 / 神兽）");
        }

        var tiers = new List<Tier>(TierIds.Length);
        for (var i = 0; i < TierIds.Length; i++)
        {
            var row = wire.Tiers[i];
            var id = (row.Id ?? "").Trim();
            if (!string.Equals(id, TierIds[i], StringComparison.Ordinal))
            {
                throw new ConfigException(
                    "灵兽表第 " + (i + 1) + " 档必须写 " + TierIds[i] + "（顺序即高低）");
            }

            var name = (row.Name ?? "").Trim();
            if (name.Length == 0)
            {
                throw new ConfigException("灵兽档位 " + id + " 缺 name");
            }

            tiers.Add(new Tier(id, name));
        }

        if (wire.Entries is null || wire.Entries.Count == 0)
        {
            throw new ConfigException("灵兽表一只灵兽都没有");
        }

        var entries = new List<Entry>(wire.Entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var row in wire.Entries)
        {
            var itemId = (row.ItemId ?? "").Trim();
            if (itemId.Length == 0)
            {
                throw new ConfigException("灵兽表有一条缺 item_id");
            }

            if (!seen.Add(itemId))
            {
                throw new ConfigException("灵兽表 item_id 重复：" + itemId);
            }

            var goods = items.Find(itemId)
                ?? throw new ConfigException("灵兽 " + itemId + " 不在 items.json 里");
            if (!string.Equals(goods.Kind, "spirit_beast", StringComparison.Ordinal))
            {
                throw new ConfigException(
                    "灵兽 " + itemId + " 的 kind 必须是 spirit_beast（现在是 " + goods.Kind + "）");
            }

            if (goods.StackMax != 1)
            {
                throw new ConfigException("灵兽 " + itemId + " 的 stack_max 必须是 1");
            }

            var tierId = (row.Tier ?? "").Trim();
            if (tiers.Find(t => string.Equals(t.Id, tierId, StringComparison.Ordinal)) is null)
            {
                throw new ConfigException("灵兽 " + itemId + " 的 tier 不认识：" + tierId);
            }

            var passive = row.LifeAssistPctBps ?? 0;
            if (passive < 0 || passive > MaxEntryPassiveBps)
            {
                throw new ConfigException(
                    "灵兽 " + itemId + " 的 life_assist_pct_bps 须在 0.." + MaxEntryPassiveBps);
            }

            total += passive;
            entries.Add(new Entry(itemId, (row.Name ?? goods.Name).Trim(), tierId, passive));
        }

        // 一处上限：图鉴 + 灵兽 合计不能超过 hero_codex.json 那一个数。
        var codexTotal = 0;
        foreach (var entry in heroCodex.Entries)
        {
            codexTotal += entry.PassivePctBps;
        }

        if (codexTotal + total > heroCodex.MaxTotalPassivePctBps)
        {
            throw new ConfigException(
                "生活协助合计超上限：图鉴 " + codexTotal + " + 灵兽 " + total
                + " > hero_codex.json 的 " + heroCodex.MaxTotalPassivePctBps);
        }

        return new SpiritBeastTable(
            wire.Version, wire.Pending ?? false, kind, tiers, entries,
            heroCodex.MaxTotalPassivePctBps, codexTotal, total);
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

        [JsonPropertyName("passive_kind")]
        public string? PassiveKind { get; init; }

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
    }

    sealed class EntryWire
    {
        [JsonPropertyName("item_id")]
        public string? ItemId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("tier")]
        public string? Tier { get; init; }

        [JsonPropertyName("life_assist_pct_bps")]
        public int? LifeAssistPctBps { get; init; }
    }
}
