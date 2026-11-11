using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 拍卖税的唯一正本。方案第 12 章写定「官方抽税 5%–8%」、拍卖用金币标价、
/// 「拍卖行抽走的金币直接销毁，不做成商品」；开发架构第 11 节 P1-5 与
/// 第 13 节 P1-5a 同口径。写定的区间与币种在这里硬校验，具体税率与取整
/// 方式待定、进配置。配置里没有收款方：这笔税没有落点。
/// </summary>
public sealed class AuctionTable
{
    // 方案写定的区间，不是待定数值：写成常量，配置越界就起不来。
    public const int WrittenMinRatePct = 5;
    public const int WrittenMaxRatePct = 8;
    public const string WrittenGoldKind = "gold";
    // 开发架构第 13 节 P1-5b 写定：寄售的是元宝，换的是金币，商城不直接卖金币。
    // 币种编号在 currency.json 里是 yuanbao，这里写成常量，配置改了起不来。
    public const string WrittenIngotKind = "yuanbao";
    public const string WrittenSchema = "auction-table/1";

    public int Version { get; }
    public int TaxRatePct { get; }
    public string Rounding { get; }
    public string GoldKind { get; }
    public string IngotKind { get; }
    public string Note { get; }

    AuctionTable(int version, int taxRatePct, string rounding, string goldKind,
        string ingotKind, string note)
    {
        Version = version;
        TaxRatePct = taxRatePct;
        Rounding = rounding;
        GoldKind = goldKind;
        IngotKind = ingotKind;
        Note = note;
    }

    /// <summary>税额。目前只实现配置里的 down（向下取整），整数除法天然向下。</summary>
    public long TaxOf(long priceGold) =>
        priceGold < 1 ? 0 : priceGold * TaxRatePct / 100;

    public static AuctionTable Load(string path, CurrencyTable currencies)
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
            // 税不能有收款方。出现这类字段就是玩法被改了，直接起不来。
            if (property.Name.Contains("recipient", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("tax_account", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("system_account", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("tax_item", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigException("auction.json 不许出现税的收款方字段：" + property.Name);
            }
        }

        if (!string.Equals(wire.Schema, WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException("auction.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.TaxRatePct is null)
        {
            throw new ConfigException("auction.json 缺 tax_rate_pct");
        }

        var rate = wire.TaxRatePct.Value;
        if (rate < WrittenMinRatePct || rate > WrittenMaxRatePct)
        {
            throw new ConfigException("auction.json 的 tax_rate_pct 必须落在写定的 "
                + WrittenMinRatePct + "–" + WrittenMaxRatePct + " 之间，现在是 " + rate);
        }

        var rounding = (wire.Rounding ?? "").Trim();
        if (rounding != "down")
        {
            throw new ConfigException("auction.json 的 rounding 目前只认 down，现在是 " + rounding);
        }

        var gold = (wire.GoldKind ?? "").Trim();
        if (!string.Equals(gold, WrittenGoldKind, StringComparison.Ordinal))
        {
            throw new ConfigException("auction.json 的 gold_kind 必须是 " + WrittenGoldKind);
        }

        if (currencies.Find(gold) is null)
        {
            throw new ConfigException("auction.json 的 gold_kind 不在 currency.json 里：" + gold);
        }

        // P1-5b：寄售的是元宝、换的是金币。两种币种不能是同一个，
        // 否则就成了「用金币买金币」，违背「元宝只经寄售换成金币」的写定。
        var ingot = (wire.IngotKind ?? "").Trim();
        if (!string.Equals(ingot, WrittenIngotKind, StringComparison.Ordinal))
        {
            throw new ConfigException("auction.json 的 ingot_kind 必须是 " + WrittenIngotKind);
        }

        if (currencies.Find(ingot) is null)
        {
            throw new ConfigException("auction.json 的 ingot_kind 不在 currency.json 里：" + ingot);
        }

        if (string.Equals(ingot, gold, StringComparison.Ordinal))
        {
            throw new ConfigException("auction.json 的 ingot_kind 不能和 gold_kind 相同");
        }

        var note = (wire.Note ?? "").Trim();
        if (!note.Contains("待定", StringComparison.Ordinal))
        {
            throw new ConfigException("auction.json 的 note 要写明哪些数值待定");
        }

        return new AuctionTable(wire.Version ?? 1, rate, rounding, gold, ingot, note);
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

        [JsonPropertyName("note")]
        public string? Note { get; init; }

        [JsonPropertyName("tax_rate_pct")]
        public int? TaxRatePct { get; init; }

        [JsonPropertyName("rounding")]
        public string? Rounding { get; init; }

        [JsonPropertyName("gold_kind")]
        public string? GoldKind { get; init; }

        [JsonPropertyName("ingot_kind")]
        public string? IngotKind { get; init; }
    }
}
