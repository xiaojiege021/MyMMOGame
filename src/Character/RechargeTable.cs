using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 内部充值商品表（`config/recharge.json`，P1-5d）。方案只写定「官方现金只来自
/// 充值元宝」，没给档位与价格，所以元宝数与金额全部标待定，代码不写死。
///
/// P0 不接真实支付渠道：订单由本服务建，回调也由本服务结算。客户端只能带
/// product_id，元宝数由这份表决定；订单号由服务端生成，一笔订单只发一次。
/// 一张订单的状态落在 `"角色".recharge_order`（见 `db/character/recharge.sql`），
/// 不放在进程内存里——换进程、重启都还读得到同一行。
/// </summary>
public sealed class RechargeTable
{
    public const string WrittenSchema = "recharge-table/1";

    /// <summary>一档内部商品：编号、名字、到账元宝、金额（分）。</summary>
    public sealed record Product(string Id, string Name, long Yuanbao, long RmbCents);

    public int Version { get; }
    public string Note { get; }
    public IReadOnlyList<Product> Products { get; }

    RechargeTable(int version, string note, IReadOnlyList<Product> products)
    {
        Version = version;
        Note = note;
        Products = products;
    }

    public Product? Find(string id)
    {
        for (var i = 0; i < Products.Count; i++)
        {
            if (string.Equals(Products[i].Id, id, StringComparison.Ordinal))
            {
                return Products[i];
            }
        }

        return null;
    }

    public static RechargeTable Load(string path)
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
            throw new ConfigException("recharge.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("recharge.json 的 version 必须是正整数");
        }

        var raw = wire.Products;
        if (raw is null || raw.Count == 0)
        {
            throw new ConfigException("recharge.json 的 products 不能为空");
        }

        var products = new List<Product>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in raw)
        {
            if (p is null)
            {
                throw new ConfigException("recharge.json 的 products 有空项");
            }

            var id = (p.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("recharge.json 的商品编号不能是空的");
            }

            if (!seen.Add(id))
            {
                throw new ConfigException("recharge.json 的商品编号重复：" + id);
            }

            if ((p.Name ?? "").Trim().Length == 0)
            {
                throw new ConfigException("recharge.json 的商品 " + id + " 没有 name");
            }

            // 0 或负数的元宝会让「充值」变成「扣钱不发东西」的反面，直接拒。
            if (p.Yuanbao < 1)
            {
                throw new ConfigException("recharge.json 的商品 " + id + " 元宝数必须是正整数");
            }

            // P0 不接真实渠道，金额只是记录用的待定值；仍然要求为正，免得对账时出现 0 元单。
            if (p.RmbCents < 1)
            {
                throw new ConfigException("recharge.json 的商品 " + id + " 金额必须是正整数（分）");
            }

            products.Add(new Product(id, p.Name!.Trim(), p.Yuanbao, p.RmbCents));
        }

        return new RechargeTable(wire.Version, (wire.Note ?? "").Trim(), products);
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
        [JsonPropertyName("products")] public List<WireProduct?>? Products { get; set; }
    }

    sealed class WireProduct
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("yuanbao")] public long Yuanbao { get; set; }
        [JsonPropertyName("rmb_cents")] public long RmbCents { get; set; }
    }
}
