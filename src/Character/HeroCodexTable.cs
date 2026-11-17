using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// N12 英灵图鉴正本。九位开口的人与主线名单一致；解锁场次由 mainline 首场出现推算；
/// 被动只做 life_assist，不进伤害公式，不做卡组战斗。
/// </summary>
public sealed class HeroCodexTable
{
    public const string WrittenSchema = "hero-codex/1";
    public const string WrittenPassiveKind = "life_assist";
    public const int EntryCount = 9;
    public const int MaxEntryPassiveBps = 100;
    public const int MaxTotalPassiveBpsCap = 1000;

    public sealed record Entry(
        string Id,
        string Name,
        int UnlockMainStage,
        int PassivePctBps);

    public int Version { get; }
    public bool Pending { get; }
    public string PassiveKind { get; }
    public int MaxTotalPassivePctBps { get; }
    public IReadOnlyList<Entry> Entries { get; }

    HeroCodexTable(
        int version, bool pending, string passiveKind, int maxTotalPassivePctBps,
        IReadOnlyList<Entry> entries)
    {
        Version = version;
        Pending = pending;
        PassiveKind = passiveKind;
        MaxTotalPassivePctBps = maxTotalPassivePctBps;
        Entries = entries;
    }

    public Entry? Find(string id)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    public static HeroCodexTable Load(string path, MainlineTable mainline)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到英灵图鉴表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("hero_codex.json 是空的");
        }
        catch (JsonException ex)
        {
            throw new ConfigException("hero_codex.json 不是合法 JSON：" + ex.Message);
        }

        if ((wire.Schema ?? "").Trim() != WrittenSchema)
        {
            throw new ConfigException("hero_codex.json schema 必须是 " + WrittenSchema);
        }

        var kind = (wire.PassiveKind ?? WrittenPassiveKind).Trim();
        if (!string.Equals(kind, WrittenPassiveKind, StringComparison.Ordinal))
        {
            throw new ConfigException("英灵图鉴 passive_kind 只能是 " + WrittenPassiveKind);
        }

        var maxTotal = wire.MaxTotalPassivePctBps ?? 0;
        if (maxTotal <= 0 || maxTotal > MaxTotalPassiveBpsCap)
        {
            throw new ConfigException("max_total_passive_pct_bps 须在 1.." + MaxTotalPassiveBpsCap);
        }

        if (mainline.IsMissing || mainline.Stages.Count == 0)
        {
            throw new ConfigException("hero_codex.json 需要已加载的 mainline.json 推算解锁场次");
        }

        var firstStage = BuildFirstStageMap(mainline);
        var entries = new List<Entry>();
        var sumPassive = 0;
        if (wire.Entries is null || wire.Entries.Count != EntryCount)
        {
            throw new ConfigException("英灵图鉴必须正好 " + EntryCount + " 条");
        }

        for (var i = 0; i < EntryCount; i++)
        {
            var row = wire.Entries[i];
            var id = (row.Id ?? "").Trim();
            var name = (row.Name ?? "").Trim();
            if (id.Length == 0 || name.Length == 0)
            {
                throw new ConfigException("英灵图鉴第 " + (i + 1) + " 条 id 与 name 不能为空");
            }

            var written = mainline.Speakers[i];
            if (!string.Equals(name, written, StringComparison.Ordinal))
            {
                throw new ConfigException("英灵图鉴第 " + (i + 1) + " 条须是 " + written);
            }

            if (!firstStage.TryGetValue(name, out var unlock) || unlock <= 0)
            {
                throw new ConfigException("主线里没有开口的人：" + name);
            }

            var passive = row.PassivePctBps ?? 0;
            if (passive < 0 || passive > MaxEntryPassiveBps)
            {
                throw new ConfigException("条目 " + id + " passive_pct_bps 须在 0.." + MaxEntryPassiveBps);
            }

            sumPassive += passive;
            entries.Add(new Entry(id, name, unlock, passive));
        }

        if (sumPassive > maxTotal)
        {
            throw new ConfigException("九条 passive 之和不能超过 max_total_passive_pct_bps");
        }

        return new HeroCodexTable(
            wire.Version, wire.Pending ?? false, kind, maxTotal, entries);
    }

    static Dictionary<string, int> BuildFirstStageMap(MainlineTable mainline)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stage in mainline.Stages)
        {
            foreach (var speaker in stage.Speakers)
            {
                if (!map.ContainsKey(speaker))
                {
                    map[speaker] = stage.Stage;
                }
            }
        }

        return map;
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

        [JsonPropertyName("max_total_passive_pct_bps")]
        public int? MaxTotalPassivePctBps { get; init; }

        [JsonPropertyName("entries")]
        public List<EntryWire>? Entries { get; init; }
    }

    sealed class EntryWire
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("passive_pct_bps")]
        public int? PassivePctBps { get; init; }
    }
}