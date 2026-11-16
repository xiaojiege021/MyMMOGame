using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>通行证的一阶：轨（free / paid）、阶号、这一阶给的奖励（一到多条）。</summary>
public sealed record SeasonPassStage(int Stage, IReadOnlyList<SeasonPassRewardDef> Rewards);

/// <summary>一阶里的一条奖励。物品编号与数量只从配置读，客户端带不进来。</summary>
public sealed record SeasonPassRewardDef(
    string Track, int Stage, string Kind, string ItemId, int Count);

/// <summary>一季的通行证：免费轨与付费轨各若干阶。</summary>
public sealed record SeasonPassSeason(
    string SeasonId,
    IReadOnlyList<SeasonPassStage> Free,
    IReadOnlyList<SeasonPassStage> Paid)
{
    public IReadOnlyList<SeasonPassStage> Track(string track) =>
        string.Equals(track, SeasonPassTable.PaidTrack, StringComparison.Ordinal) ? Paid : Free;
}

/// <summary>
/// 赛季通行证的唯一正本（`config/season_pass.json`）。方案 12.1 写定：
/// 免费轨只给称号和少量外观碎片，付费轨给当季秘境外观、洞府摆件和很少几张保护券，
/// 两轨都不给金币、强化石、转移石、能量结晶、灵石、装备与副本次数。
///
/// 这些都是加载期的硬约束，不是运行期再判一遍：
///   · 免费轨的奖励种类只能落在 {title, appearance_fragment}，付费轨只能落在
///     {season_outfit, home_decor, protect_ticket}；两轨各自三种 / 两种都必须出现。
///   · 保护券不许另立编号：必须就是 enhance.json 的 protect_item_id（商城卖的同一件），
///     且每阶张数不超过 protect_per_stage_max（方案只写「很少几张」）。
///   · 每条奖励都要在 items.json 里真实存在、种类对得上、到账即绑。
///   · 每一季都得有通行证，季编号必须真的在 season.json 的季表里——通行证不能长出一季。
/// 价格不在这份表里：通行证在商城按 shop.json 那条 season_pass 商品直接标价卖。
/// </summary>
public sealed class SeasonPassTable
{
    public const string WrittenSchema = "season-pass-table/1";

    public const string FreeTrack = "free";
    public const string PaidTrack = "paid";

    // 方案 12.1 写定的两轨奖励种类。多一种少一种都算玩法被改了。
    public static readonly string[] FreeKinds = ["title", "appearance_fragment"];
    public static readonly string[] PaidKinds = ["season_outfit", "home_decor", "protect_ticket"];

    // 奖励种类 → items.json 的物品种类。称号不是装备，外观碎片不是材料。
    static readonly Dictionary<string, string> ItemKindOf = new(StringComparer.Ordinal)
    {
        ["title"] = "title",
        ["appearance_fragment"] = "appearance_fragment",
        ["season_outfit"] = "outfit",
        ["home_decor"] = "home_decor",
        ["protect_ticket"] = "voucher",
    };

    static readonly string[] AllowedTopKeys =
        ["schema", "version", "note", "pending", "product_id", "protect_per_stage_max", "seasons"];
    static readonly string[] AllowedSeasonKeys = ["season", "free", "paid"];
    static readonly string[] AllowedStageKeys = ["stage", "rewards"];
    static readonly string[] AllowedRewardKeys = ["kind", "item_id", "count"];

    readonly List<SeasonPassSeason> _seasons;

    public int Version { get; }
    public bool Pending { get; }
    public string ProductId { get; }
    public int ProtectPerStageMax { get; }
    public long PriceYuanbao { get; }
    public string ProtectItemId { get; }
    public IReadOnlyList<SeasonPassSeason> Seasons => _seasons;

    SeasonPassTable(
        int version, bool pending, string productId, int protectPerStageMax,
        long priceYuanbao, string protectItemId, List<SeasonPassSeason> seasons)
    {
        Version = version;
        Pending = pending;
        ProductId = productId;
        ProtectPerStageMax = protectPerStageMax;
        PriceYuanbao = priceYuanbao;
        ProtectItemId = protectItemId;
        _seasons = seasons;
    }

    /// <summary>这一季的通行证。配置里没有这一季返回 null（调用方拒绝，不猜）。</summary>
    public SeasonPassSeason? Find(string seasonId)
    {
        for (var i = 0; i < _seasons.Count; i++)
        {
            if (string.Equals(_seasons[i].SeasonId, seasonId, StringComparison.Ordinal))
            {
                return _seasons[i];
            }
        }

        return null;
    }

    /// <summary>这一轨在这份表里合不合法。表外的轨一律 false。</summary>
    public static bool IsKnownTrack(string track) =>
        string.Equals(track, FreeTrack, StringComparison.Ordinal)
        || string.Equals(track, PaidTrack, StringComparison.Ordinal);

    /// <summary>
    /// 与角色服真实启动共用同一条校验链。seasonText 是同一份 config/season.json 的正文：
    /// 只用来核对「季编号真的存在」和「每一季都有通行证」，当前是哪一季仍归全局服务。
    /// </summary>
    public static SeasonPassTable Load(
        string path, string seasonText, ShopTable shop, ItemTable items, EnhanceTable enhance)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配置文件 " + path);
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("通行证表不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            foreach (var key in RequiredTopKeys)
            {
                if (!root.TryGetProperty(key, out _))
                {
                    throw new ConfigException("season_pass.json 缺字段 " + key);
                }
            }

            RejectUnknownKeys(root, AllowedTopKeys, "顶层");

            Wire wire;
            try
            {
                wire = JsonSerializer.Deserialize<Wire>(text, JsonOptions)
                    ?? throw new ConfigException("通行证表是空的");
            }
            catch (JsonException ex)
            {
                throw new ConfigException("通行证表字段类型不对：" + ex.Message);
            }

            if (!string.Equals((wire.Schema ?? "").Trim(), WrittenSchema, StringComparison.Ordinal))
            {
                throw new ConfigException("season_pass.json 的 schema 必须是 " + WrittenSchema);
            }

            if (wire.Version is null || wire.Version.Value <= 0)
            {
                throw new ConfigException("season_pass.json 的 version 必须大于 0");
            }

            var productId = (wire.ProductId ?? "").Trim();
            if (productId.Length == 0)
            {
                throw new ConfigException("season_pass.json 的 product_id 不能为空");
            }

            // 通行证是商城里的那条商品：标价只从 shop.json 读，本表不写第二套价格。
            var product = shop.Find(productId)
                ?? throw new ConfigException("season_pass.json 的 product_id 不在 shop.json 里：" + productId);
            if (!string.Equals(product.Kind, "season_pass", StringComparison.Ordinal))
            {
                throw new ConfigException("season_pass.json 的 product_id 指到的不是通行证商品："
                    + productId + " kind=" + product.Kind);
            }

            // K1：通行证在清单里必须是 season_pass 这种发货方式（不看种类字符串）。
            if (product.Mode != DeliveryMode.SeasonPass)
            {
                throw new ConfigException("season_pass.json 的通行证商品 delivery.mode 必须是 "
                    + "season_pass：" + productId + " mode=" + ShopTable.ModeText(product.Mode));
            }

            if (product.DeliveryItemId.Length > 0)
            {
                // 通行证是状态发货：买下即开付费轨，不往背包放东西，不许填一个假发货物。
                throw new ConfigException("season_pass.json 的通行证商品不许配发货物："
                    + productId);
            }

            var perStageMax = wire.ProtectPerStageMax ?? 0;
            if (perStageMax < 1 || perStageMax > 10)
            {
                throw new ConfigException("season_pass.json 的 protect_per_stage_max 要在 1..10 之间");
            }

            var seasonIds = ReadSeasonIds(seasonText);

            var seasonsElement = root.GetProperty("seasons");
            if (seasonsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("season_pass.json 的 seasons 必须是数组");
            }

            if (wire.Seasons is null || wire.Seasons.Count != seasonIds.Count)
            {
                throw new ConfigException("通行证表要给 season.json 里的每一季都配一份（"
                    + seasonIds.Count + " 季）");
            }

            var result = new List<SeasonPassSeason>(wire.Seasons.Count);
            var seenSeasons = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < wire.Seasons.Count; i++)
            {
                var wireSeason = wire.Seasons[i];
                var element = seasonsElement[i];
                RejectUnknownKeys(element, AllowedSeasonKeys, "第 " + (i + 1) + " 季");

                var seasonId = (wireSeason.Season ?? "").Trim();
                if (seasonId.Length == 0)
                {
                    throw new ConfigException("通行证表第 " + (i + 1) + " 季没有 season");
                }

                if (!seasonIds.Contains(seasonId))
                {
                    throw new ConfigException("通行证表里的季不在 season.json 里：" + seasonId);
                }

                if (!seenSeasons.Add(seasonId))
                {
                    throw new ConfigException("通行证表的季重复：" + seasonId);
                }

                var free = ReadTrack(element, "free", seasonId, FreeKinds,
                    items, enhance, perStageMax);
                var paid = ReadTrack(element, "paid", seasonId, PaidKinds,
                    items, enhance, perStageMax);

                // 方案 12.1 写定的两轨内容：免费轨两样都得有，付费轨三样都得有。
                RequireKind(free, "title", seasonId, "free");
                RequireKind(free, "appearance_fragment", seasonId, "free");
                RequireKind(paid, "season_outfit", seasonId, "paid");
                RequireKind(paid, "home_decor", seasonId, "paid");
                RequireKind(paid, "protect_ticket", seasonId, "paid");

                result.Add(new SeasonPassSeason(seasonId, free, paid));
            }

            return new SeasonPassTable(
                wire.Version!.Value, wire.Pending ?? false, productId, perStageMax,
                product.PriceYuanbao, enhance.ProtectItemId, result);
        }
    }

    static readonly string[] RequiredTopKeys =
        ["schema", "version", "product_id", "protect_per_stage_max", "seasons"];

    /// <summary>一轨的所有阶：阶号从 1 连续、不重复，奖励一到多条、同阶不许重复物品。</summary>
    static List<SeasonPassStage> ReadTrack(
        JsonElement seasonElement, string track, string seasonId, string[] allowedKinds,
        ItemTable items, EnhanceTable enhance, int perStageMax)
    {
        if (!seasonElement.TryGetProperty(track, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigException("通行证表 " + seasonId + " 的 " + track + " 轨必须是数组");
        }

        if (array.GetArrayLength() == 0)
        {
            throw new ConfigException("通行证表 " + seasonId + " 的 " + track + " 轨一阶都没有");
        }

        var stages = new List<SeasonPassStage>(array.GetArrayLength());
        var seenStages = new HashSet<int>();
        foreach (var stageElement in array.EnumerateArray())
        {
            RejectUnknownKeys(stageElement, AllowedStageKeys, seasonId + " 的 " + track + " 轨");

            var stage = stageElement.TryGetProperty("stage", out var stageValue)
                ? stageValue.GetInt32() : 0;
            if (stage != stages.Count + 1)
            {
                // 阶就是奖励分组的序号：从 1 起、不许跳号、不许重复，领哪一阶就是哪一条事实。
                throw new ConfigException("通行证表 " + seasonId + " 的 " + track
                    + " 轨阶号必须从 1 起连续，第 " + (stages.Count + 1) + " 阶写的是 " + stage);
            }

            if (!seenStages.Add(stage))
            {
                throw new ConfigException("通行证表 " + seasonId + " 的 " + track + " 轨阶号重复：" + stage);
            }

            if (!stageElement.TryGetProperty("rewards", out var rewards)
                || rewards.ValueKind != JsonValueKind.Array || rewards.GetArrayLength() == 0)
            {
                throw new ConfigException("通行证表 " + seasonId + " 的 " + track
                    + " 轨第 " + stage + " 阶没有奖励");
            }

            if (rewards.GetArrayLength() != 1)
            {
                // 一阶一条奖励：领取回一条事实值（物品 + 数量 + 格子）。一阶要发两样就多开一阶，
                // 免得回包只能报一半，界面与服务端各说一套。
                throw new ConfigException("通行证表 " + seasonId + " 的 " + track
                    + " 轨第 " + stage + " 阶只能有一条奖励，现在是 " + rewards.GetArrayLength());
            }

            var list = new List<SeasonPassRewardDef>(rewards.GetArrayLength());
            var seenItems = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rewardElement in rewards.EnumerateArray())
            {
                RejectUnknownKeys(rewardElement, AllowedRewardKeys,
                    seasonId + " 的 " + track + " 轨第 " + stage + " 阶");

                var kind = (rewardElement.TryGetProperty("kind", out var k)
                    ? (k.GetString() ?? "") : "").Trim();
                var itemId = (rewardElement.TryGetProperty("item_id", out var id)
                    ? (id.GetString() ?? "") : "").Trim();
                var count = rewardElement.TryGetProperty("count", out var c) ? c.GetInt32() : 0;

                if (Array.IndexOf(allowedKinds, kind) < 0)
                {
                    throw new ConfigException("通行证表 " + seasonId + " 的 " + track
                        + " 轨不许发 " + kind + "（方案 12.1 写定的两轨内容）");
                }

                if (itemId.Length == 0)
                {
                    throw new ConfigException("通行证表 " + seasonId + " 的 " + track
                        + " 轨第 " + stage + " 阶有一条奖励没写 item_id");
                }

                if (!seenItems.Add(itemId))
                {
                    throw new ConfigException("通行证表 " + seasonId + " 的 " + track
                        + " 轨第 " + stage + " 阶重复发同一件物品：" + itemId);
                }

                var goods = items.Find(itemId)
                    ?? throw new ConfigException("通行证奖励物品不在 items.json 里：" + itemId);

                if (!string.Equals(goods.Kind, ItemKindOf[kind], StringComparison.Ordinal))
                {
                    throw new ConfigException("通行证奖励 " + itemId + " 的物品种类是 "
                        + goods.Kind + "，与奖励种类 " + kind + " 对不上");
                }

                if (goods.Bind != BindRule.Always)
                {
                    // 通行证的东西到账即绑：不然一条奖励就能换成金币或灵石。
                    throw new ConfigException("通行证奖励 " + itemId + " 必须到账即绑（bind=always）");
                }

                if (count < 1 || count > goods.StackMax)
                {
                    throw new ConfigException("通行证奖励 " + itemId + " 的数量要在 1.."
                        + goods.StackMax + " 之间，现在是 " + count);
                }

                if (string.Equals(kind, "protect_ticket", StringComparison.Ordinal))
                {
                    // 方案 9.2 / 12.1：商城与通行证发的是同一种券，不许另立第二种。
                    if (!string.Equals(itemId, enhance.ProtectItemId, StringComparison.Ordinal))
                    {
                        throw new ConfigException("通行证的保护券必须是 enhance.json 的 protect_item_id（"
                            + enhance.ProtectItemId + "），不许另立：" + itemId);
                    }

                    if (count > perStageMax)
                    {
                        throw new ConfigException("通行证每阶的保护券不能超过 "
                            + perStageMax + " 张（方案写「很少几张」）：" + count);
                    }
                }

                list.Add(new SeasonPassRewardDef(track, stage, kind, itemId, count));
            }

            stages.Add(new SeasonPassStage(stage, list));
        }

        return stages;
    }

    static void RequireKind(
        IReadOnlyList<SeasonPassStage> stages, string kind, string seasonId, string track)
    {
        foreach (var stage in stages)
        {
            foreach (var reward in stage.Rewards)
            {
                if (string.Equals(reward.Kind, kind, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        throw new ConfigException("通行证表 " + seasonId + " 的 " + track
            + " 轨一整个季都没发 " + kind + "（方案 12.1 写定了这一轨给什么）");
    }

    /// <summary>season.json 里的季编号。只作交叉校验，当前季仍由全局服务回答。</summary>
    static HashSet<string> ReadSeasonIds(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("season.json 不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("seasons", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("season.json 缺 seasons 数组，无法核对通行证的季");
            }

            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in list.EnumerateArray())
            {
                var id = row.TryGetProperty("id", out var idValue)
                    ? (idValue.GetString() ?? "").Trim() : "";
                if (id.Length == 0)
                {
                    throw new ConfigException("season.json 里有季没写 id");
                }

                result.Add(id);
            }

            if (result.Count == 0)
            {
                throw new ConfigException("season.json 的 seasons 不能为空");
            }

            return result;
        }
    }

    static void RejectUnknownKeys(JsonElement row, string[] allowed, string where)
    {
        foreach (var property in row.EnumerateObject())
        {
            var ok = false;
            foreach (var candidate in allowed)
            {
                if (string.Equals(property.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    ok = true;
                    break;
                }
            }

            if (!ok)
            {
                throw new ConfigException("season_pass.json " + where + " 不许出现 " + property.Name);
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
        [JsonPropertyName("schema")] public string? Schema { get; init; }
        [JsonPropertyName("version")] public int? Version { get; init; }
        [JsonPropertyName("pending")] public bool? Pending { get; init; }
        [JsonPropertyName("product_id")] public string? ProductId { get; init; }
        [JsonPropertyName("protect_per_stage_max")] public int? ProtectPerStageMax { get; init; }
        [JsonPropertyName("seasons")] public List<SeasonWire>? Seasons { get; init; }
    }

    sealed class SeasonWire
    {
        [JsonPropertyName("season")] public string? Season { get; init; }
    }
}
