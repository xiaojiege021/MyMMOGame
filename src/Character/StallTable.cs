using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// B5 摆摊的唯一配置正本。方案第 12 章只写定「拍卖行（金币）+ 摆摊」与
/// 「官方抽税 5%–8%」，更细的格数、等级门槛、税率取值都没写死，留在这里待定；
/// 方案写定的两条（税率区间、金币标价）在这里硬校验，越界就起不来。
///
/// 摆摊不做竞价、不做跨服：cross_server 只能是 false。成交不另立账本，
/// 仍走拍卖那一笔角色服事务（auction_txn / auction_settlement），税同样没有收款方。
/// </summary>
public sealed class StallTable
{
    // 方案第 12 章写定的区间，不是待定数值：写成常量，配置越界就起不来。
    public const int WrittenMinRatePct = 5;
    public const int WrittenMaxRatePct = 8;
    public const string WrittenSchema = "stall-table/1";
    // 金币标价是方案写定的；摆摊不许用元宝标价（元宝只经寄售换金币）。
    public const int WrittenSlotsMax = 8;
    public const int WrittenLevelMax = 100;

    public int Version { get; }
    public bool Pending { get; }
    public int TaxRatePct { get; }
    public int Slots { get; }
    public int MinLevel { get; }
    public bool CrossServer { get; }
    public int TitleMaxLength { get; }
    public string Note { get; }

    StallTable(int version, bool pending, int taxRatePct, int slots, int minLevel,
        bool crossServer, int titleMaxLength, string note)
    {
        Version = version;
        Pending = pending;
        TaxRatePct = taxRatePct;
        Slots = slots;
        MinLevel = minLevel;
        CrossServer = crossServer;
        TitleMaxLength = titleMaxLength;
        Note = note;
    }

    /// <summary>税额。与拍卖同一口径：向下取整，整数除法天然向下。</summary>
    public long TaxOf(long priceGold) =>
        priceGold < 1 ? 0 : priceGold * TaxRatePct / 100;

    public static StallTable Load(string path)
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

        using var document = JsonDocument.Parse(text);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            // 税不能有收款方，与拍卖同一条纪律：出现这类字段就是玩法被改了。
            if (property.Name.Contains("recipient", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("tax_account", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("system_account", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("tax_item", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigException("stall.json 不许出现税的收款方字段：" + property.Name);
            }
        }

        if (!string.Equals(wire.Schema, WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException("stall.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.TaxRatePct is null)
        {
            throw new ConfigException("stall.json 缺 tax_rate_pct");
        }

        var rate = wire.TaxRatePct.Value;
        if (rate < WrittenMinRatePct || rate > WrittenMaxRatePct)
        {
            throw new ConfigException("stall.json 的 tax_rate_pct 必须落在写定的 "
                + WrittenMinRatePct + "–" + WrittenMaxRatePct + " 之间，现在是 " + rate);
        }

        if (wire.Slots is null)
        {
            throw new ConfigException("stall.json 缺 slots");
        }

        var slots = wire.Slots.Value;
        if (slots < 1 || slots > WrittenSlotsMax)
        {
            throw new ConfigException("stall.json 的 slots 必须是 1–"
                + WrittenSlotsMax + "，现在是 " + slots);
        }

        if (wire.MinLevel is null)
        {
            throw new ConfigException("stall.json 缺 min_level");
        }

        var minLevel = wire.MinLevel.Value;
        if (minLevel < 1 || minLevel > WrittenLevelMax)
        {
            throw new ConfigException("stall.json 的 min_level 必须是 1–"
                + WrittenLevelMax + "，现在是 " + minLevel);
        }

        if (wire.CrossServer is null)
        {
            throw new ConfigException("stall.json 缺 cross_server");
        }

        // 方案没写跨服摆摊，本卡口径是「不跨服」：写成 true 就是改了玩法，起不来。
        if (wire.CrossServer.Value)
        {
            throw new ConfigException("stall.json 的 cross_server 只能是 false（摆摊不跨服）");
        }

        if (wire.TitleMaxLength is null)
        {
            throw new ConfigException("stall.json 缺 title_max_length");
        }

        var titleMax = wire.TitleMaxLength.Value;
        if (titleMax < 1 || titleMax > 40)
        {
            throw new ConfigException("stall.json 的 title_max_length 必须是 1–40，现在是 " + titleMax);
        }

        var note = (wire.Note ?? "").Trim();
        if (!note.Contains("待定", StringComparison.Ordinal))
        {
            throw new ConfigException("stall.json 的 note 要写明哪些数值待定");
        }

        return new StallTable(
            wire.Version ?? 1, wire.Pending ?? false, rate, slots, minLevel,
            wire.CrossServer.Value, titleMax, note);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed record Wire
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        [JsonPropertyName("version")]
        public int? Version { get; init; }

        [JsonPropertyName("pending")]
        public bool? Pending { get; init; }

        [JsonPropertyName("note")]
        public string? Note { get; init; }

        [JsonPropertyName("tax_rate_pct")]
        public int? TaxRatePct { get; init; }

        [JsonPropertyName("slots")]
        public int? Slots { get; init; }

        [JsonPropertyName("min_level")]
        public int? MinLevel { get; init; }

        [JsonPropertyName("cross_server")]
        public bool? CrossServer { get; init; }

        [JsonPropertyName("title_max_length")]
        public int? TitleMaxLength { get; init; }
    }
}
