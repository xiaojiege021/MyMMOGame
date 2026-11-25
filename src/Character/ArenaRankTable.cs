using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// B11 竞技场赛季段位正本（方案 12 章「1v1 / 3v3，属性模板统一，赛季段位与专属外观」；
/// 开发架构 14.5 第 14 行「段位分与专属外观；竞技场仍模板统一属性；奖励偏外观与荣誉」）。
///
/// 本表只回答三件事：一场胜仗加多少分、分数落在哪一档、这一档发哪一件专属外观。
///   · 档数与阈值全在配置里：首档必须 0 分、阈值严格递增、编号不重复；
///   · 每档的 appearance_item_id 必须在物品表里、且是外观类（outfit 一类），
///     不在代码里写第二份外观表；
///   · win_points 与 top_n（排行取前几名）也是配置，方案没给数字，出厂全标 pending。
///
/// 本表不参与竞技场属性：P2-3a 的「换装不改变模板属性」一字不变，段位再高也不加
/// 攻击、生命、蓝、治疗与仇恨——模板只在 config/arena_match.json。
/// 表里不许长出货栈字段（金币 / 灵石 / 强化石 / 装备 / 副本次数 / 属性加成），
/// 未知键在加载期直接拒绝。
/// </summary>
public sealed class ArenaRankTable
{
    public const string WrittenSchema = "arena-rank-table/1";

    /// <summary>一档：编号、名字、门槛分、这一档的专属外观。</summary>
    public sealed record Tier(string Key, string Name, long MinScore, string AppearanceItemId);

    static readonly string[] AllowedTopKeys =
        ["schema", "version", "note", "pending", "win_points", "top_n", "tiers"];
    static readonly string[] AllowedTierKeys = ["key", "name", "min_score", "appearance_item_id", "note"];

    // 档位是荣誉与外观，不是货栈：这些键一律起不来（note 里写说明不受影响）。
    static readonly string[] ForbiddenKeys =
    [
        "gold", "stone", "crystal", "enhance", "equip", "item_count", "drop", "exp",
        "attack", "hp", "mana", "heal", "threat", "dungeon_count", "price",
    ];

    readonly List<Tier> _tiers;

    public int Version { get; }
    public bool Pending { get; }
    public long WinPoints { get; }
    public int TopN { get; }
    public bool IsMissing { get; }
    public IReadOnlyList<Tier> Tiers => _tiers;

    ArenaRankTable(int version, bool pending, long winPoints, int topN,
        List<Tier> tiers, bool missing = false)
    {
        Version = version;
        Pending = pending;
        WinPoints = winPoints;
        TopN = topN;
        _tiers = tiers;
        IsMissing = missing;
    }

    /// <summary>
    /// 没配这张表：老卡的临时配置目录里没有 arena_rank.json，服务照常起，
    /// 竞技场仍能打，只是不记段位分（ReportArenaResult 回 rank_table_missing）。
    /// </summary>
    public static ArenaRankTable Missing() => new(0, true, 0, 0, [], missing: true);

    public Tier? Find(string key)
    {
        foreach (var tier in _tiers)
        {
            if (string.Equals(tier.Key, key, StringComparison.Ordinal))
            {
                return tier;
            }
        }

        return null;
    }

    /// <summary>这个分数落在哪一档：取 min_score 不超过它的最高一档。</summary>
    public Tier? TierFor(long score)
    {
        Tier? found = null;
        foreach (var tier in _tiers)
        {
            if (tier.MinScore <= score)
            {
                found = tier;
            }
        }

        return found;
    }

    /// <summary>下一档；已经到顶回 null。</summary>
    public Tier? NextAfter(string key)
    {
        for (var i = 0; i < _tiers.Count; i++)
        {
            if (!string.Equals(_tiers[i].Key, key, StringComparison.Ordinal))
            {
                continue;
            }

            return i + 1 < _tiers.Count ? _tiers[i + 1] : null;
        }

        return null;
    }

    /// <summary>
    /// 校验链与真实启动共用。items 用来钉「这一档的外观真的在物品表里、而且是外观类」：
    /// 配置里写了一件装备或一个不存在的编号，进程直接起不来。
    /// </summary>
    public static ArenaRankTable Load(string path, ItemTable items)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"段位表不存在：{path}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{path}：不是合法 JSON（{ex.Message}）");
        }

        using (document)
        {
            var root = document.RootElement;
            foreach (var key in RequiredTopKeys)
            {
                if (!root.TryGetProperty(key, out _))
                {
                    throw new ConfigException($"arena_rank.json 缺字段 {key}");
                }
            }

            RejectUnknownKeys(root, AllowedTopKeys, "顶层");
            ScanForbiddenKeys(root, "顶层");

            ArenaRankFile? file;
            try
            {
                file = JsonSerializer.Deserialize<ArenaRankFile>(root.GetRawText(), JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new ConfigException($"arena_rank.json 字段类型不对：{ex.Message}");
            }

            if (file is null)
            {
                throw new ConfigException("arena_rank.json 是空的");
            }

            var schema = (file.Schema ?? "").Trim();
            if (!string.Equals(schema, WrittenSchema, StringComparison.Ordinal))
            {
                throw new ConfigException("arena_rank.json 的 schema 必须是 " + WrittenSchema);
            }

            if (file.Version <= 0)
            {
                throw new ConfigException("arena_rank.json 的 version 必须大于 0");
            }

            // 方案只写了「赛季段位与专属外观」，档位、分数与外观都是本卡按默认填的占位值：
            // 没写 pending=true 就是在假装数值已经写定，直接起不来。
            if (file.Pending != true)
            {
                throw new ConfigException(
                    "arena_rank.json 的 pending 必须是 true：档位与分数是方案未写定的占位值");
            }

            if (file.WinPoints <= 0)
            {
                throw new ConfigException("arena_rank.json 的 win_points 必须大于 0");
            }

            if (file.TopN < 1 || file.TopN > 100)
            {
                throw new ConfigException("arena_rank.json 的 top_n 必须在 1..100");
            }

            if (file.Tiers is null || file.Tiers.Count < 2)
            {
                throw new ConfigException("arena_rank.json 至少要两档，段位才分得出来");
            }

            var rowsElement = root.GetProperty("tiers");
            if (rowsElement.ValueKind != JsonValueKind.Array
                || rowsElement.GetArrayLength() != file.Tiers.Count)
            {
                throw new ConfigException("arena_rank.json 的 tiers 必须是条目齐全的数组");
            }

            var tiers = new List<Tier>(file.Tiers.Count);
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var seenItems = new HashSet<string>(StringComparer.Ordinal);
            long last = long.MinValue;
            for (var i = 0; i < file.Tiers.Count; i++)
            {
                var wire = file.Tiers[i];
                RejectUnknownKeys(rowsElement[i], AllowedTierKeys, "第 " + (i + 1) + " 档");
                ScanForbiddenKeys(rowsElement[i], "第 " + (i + 1) + " 档");

                var key = (wire.Key ?? "").Trim();
                var name = (wire.Name ?? "").Trim();
                var itemId = (wire.AppearanceItemId ?? "").Trim();
                if (key.Length == 0 || name.Length == 0 || itemId.Length == 0)
                {
                    throw new ConfigException(
                        "arena_rank.json 第 " + (i + 1) + " 档的 key / name / appearance_item_id 不能为空");
                }

                if (!seenKeys.Add(key))
                {
                    throw new ConfigException("段位编号重复：" + key);
                }

                if (!seenItems.Add(itemId))
                {
                    throw new ConfigException("两个段位共用了同一件专属外观：" + itemId);
                }

                // 首档必须 0 分：新角色落在最低一档，不能一进来就没有档位。
                if (i == 0 && wire.MinScore != 0)
                {
                    throw new ConfigException("arena_rank.json 的首档 min_score 必须是 0");
                }

                if (wire.MinScore <= last)
                {
                    throw new ConfigException("段位门槛必须严格递增，第 " + (i + 1) + " 档没涨");
                }

                last = wire.MinScore;

                var goods = items.Find(itemId)
                    ?? throw new ConfigException("段位外观不在物品表里：" + itemId);
                if (!WardrobeRepository.IsAppearanceKind(goods.Kind))
                {
                    throw new ConfigException(
                        "段位外观必须是外观类物品（outfit / weapon_skin / realm_skin / flight_trail / "
                        + "mount_skin / appearance_fragment），" + itemId + " 是 " + goods.Kind);
                }

                tiers.Add(new Tier(key, name, wire.MinScore, itemId));
            }

            return new ArenaRankTable(
                file.Version, file.Pending == true, file.WinPoints, file.TopN, tiers);
        }
    }

    static readonly string[] RequiredTopKeys = ["schema", "version", "tiers"];

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
                throw new ConfigException("arena_rank.json " + where + " 不许出现 " + property.Name);
            }
        }
    }

    static void ScanForbiddenKeys(JsonElement row, string where)
    {
        foreach (var property in row.EnumerateObject())
        {
            foreach (var word in ForbiddenKeys)
            {
                if (property.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConfigException(
                        "arena_rank.json " + where + " 不许出现 " + property.Name
                        + "：段位只给分数、荣誉与外观，不发货栈");
                }
            }
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed class ArenaRankFile
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("pending")]
        public bool? Pending { get; init; }

        [JsonPropertyName("win_points")]
        public long WinPoints { get; init; }

        [JsonPropertyName("top_n")]
        public int TopN { get; init; }

        [JsonPropertyName("tiers")]
        public List<TierWire>? Tiers { get; init; }
    }

    sealed class TierWire
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("min_score")]
        public long MinScore { get; init; }

        [JsonPropertyName("appearance_item_id")]
        public string? AppearanceItemId { get; init; }
    }
}
