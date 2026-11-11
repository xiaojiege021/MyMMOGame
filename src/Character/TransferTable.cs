using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// config/transfer.json 是强化转移数值的唯一正本：转移石编号、每级金币费用。
/// 方案 9.3 写定的「一枚转移石」「费用随转出的强化等级上升」「转移必定成功」
/// 在这里逐条校验，改配置不能悄悄改掉玩法；没写定的费用只从表里读，
/// 代码里不写第二套数字。角色服是唯一读表方。
/// </summary>
public sealed class TransferTable
{
    readonly long[] goldCosts;

    public int Version { get; }

    public string GoldKind { get; }

    /// <summary>唯一一种转移石。方案 9.3 / 10 章：巢穴产出，商城不卖。</summary>
    public string StoneItemId { get; }

    /// <summary>方案 9.3 写定「消耗一枚转移石」，配置必须一致。</summary>
    public int StoneCost { get; }

    public int MaxLevel { get; }

    TransferTable(long[] goldCosts, int version, string goldKind, string stoneItemId,
        int stoneCost, int maxLevel)
    {
        this.goldCosts = goldCosts;
        Version = version;
        GoldKind = goldKind;
        StoneItemId = stoneItemId;
        StoneCost = stoneCost;
        MaxLevel = maxLevel;
    }

    /// <summary>转出这个强化等级要扣多少金币。只接受 1 到 MaxLevel，越界由调用方先挡。</summary>
    public long GoldCostFor(int sourceLevel) => goldCosts[sourceLevel - 1];

    /// <summary>只给只读入口与启动日志用：费用表原样输出，不在代码里另算一份。</summary>
    public IReadOnlyList<long> GoldCosts => goldCosts;

    public static TransferTable Load(string path, ItemTable items, CurrencyTable currencies,
        EnhanceTable enhance)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到转移表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("转移表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("转移表不是合法 JSON：" + ex.Message);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("transfer.json 的 version 必须大于 0");
        }

        // 转移与强化扣同一种金币：不另起一套币种，免得两处费用对不上。
        var goldKind = (wire.GoldKind ?? "").Trim();
        if (goldKind.Length == 0)
        {
            throw new ConfigException("transfer.json 缺 gold_kind");
        }

        if (currencies.Find(goldKind) is null)
        {
            throw new ConfigException("transfer.json 的 gold_kind 不在币种表里：" + goldKind);
        }

        if (!string.Equals(goldKind, enhance.GoldKind, StringComparison.Ordinal))
        {
            throw new ConfigException("transfer.json 的 gold_kind 必须和 enhance.json 一致");
        }

        var stoneItemId = (wire.StoneItemId ?? "").Trim();
        if (stoneItemId.Length == 0)
        {
            throw new ConfigException("transfer.json 缺 stone_item_id");
        }

        var stone = items.Find(stoneItemId);
        if (stone is null)
        {
            throw new ConfigException("transfer.json 的 stone_item_id 不在 items.json 里：" + stoneItemId);
        }

        if (!string.Equals(stone.Kind, "material", StringComparison.Ordinal))
        {
            throw new ConfigException("transfer.json 的 stone_item_id 必须是材料：" + stoneItemId);
        }

        if (enhance.Tiers.Any(t => string.Equals(t.StoneId, stoneItemId, StringComparison.Ordinal))
            || string.Equals(enhance.ProtectItemId, stoneItemId, StringComparison.Ordinal))
        {
            throw new ConfigException("转移石不能和强化石或保护券是同一种：" + stoneItemId);
        }

        // 方案 9.3 写定「消耗一枚转移石」：配置写别的就是改玩法，直接起不来。
        var stoneCost = wire.StoneCost
            ?? throw new ConfigException("transfer.json 缺 stone_cost");
        if (stoneCost != 1)
        {
            throw new ConfigException("transfer.json 的 stone_cost 必须是方案写定的 1，现在是 " + stoneCost);
        }

        var maxLevel = enhance.MaxLevel;
        var costs = wire.GoldCostByLevel
            ?? throw new ConfigException("transfer.json 缺 gold_cost_by_level");
        if (costs.Count != maxLevel)
        {
            throw new ConfigException("transfer.json 的 gold_cost_by_level 必须正好 "
                + maxLevel + " 项（1 到 +" + maxLevel + "），现在是 " + costs.Count);
        }

        var parsed = new long[costs.Count];
        for (var i = 0; i < costs.Count; i++)
        {
            var cost = costs[i]
                ?? throw new ConfigException("transfer.json 的 gold_cost_by_level 第 " + (i + 1) + " 项缺数值");
            if (cost < 1)
            {
                throw new ConfigException("transfer.json 的 +" + (i + 1) + " 金币费用必须大于 0");
            }

            // 方案 9.3 写定「费用随转出的强化等级上升」：严格递增是写定的相对关系。
            if (i > 0 && cost <= parsed[i - 1])
            {
                throw new ConfigException("transfer.json 的 +" + (i + 1)
                    + " 金币费用必须高于 +" + i + " 的费用");
            }

            parsed[i] = cost;
        }

        return new TransferTable(parsed, wire.Version, goldKind, stoneItemId, stoneCost, maxLevel);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("gold_kind")]
        public string? GoldKind { get; init; }

        [JsonPropertyName("stone_item_id")]
        public string? StoneItemId { get; init; }

        [JsonPropertyName("stone_cost")]
        public int? StoneCost { get; init; }

        [JsonPropertyName("gold_cost_by_level")]
        public List<long?>? GoldCostByLevel { get; init; }
    }
}
