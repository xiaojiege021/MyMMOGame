using System.Text.Json;
using System.Text.Json.Serialization;

namespace Social;

/// <summary>
/// 宗门五级人数上限 + 四个职位席位（config/sect.json）——唯一正本。方案 11.1 写定
/// 五级与每档名册上限，以及掌门 1 / 副掌门 1 / 长老 4 / 执事 12 的席位；这两组数字
/// 只从这里读，代码、proto、SQL 都不写第二套。人数是名册上限，不是进本人数
/// （技术架构第 49 行、开发架构 4.4）。
///
/// 方案 11.1 还写定「45 级之后可以建立宗门，创建消耗金币」：门槛与金币种类、
/// 金币数（待定）也只从这份配置读，SectFounderGrant 照它向角色服提交一次事务。
/// 改这份配置重启社交服生效，不在代码里给数值兜底。
///
/// P2-1a2：disciple 是无席位上限的默认职位（不在 positions 里、不占席位）；
/// 职位不加属性、不代替境界；1 级只有 10 个席位，任满为止，不要求把执事名额填满。
/// 职位席位之和不超过 5 级名册上限（按配置挡，不写死 200）。
///
/// P2-1a3：transfer 段钉传位规则。掌门本人可以不在名册，名册里的 master 行
/// 只是席位展示；当前掌门唯一权威是 sect.master_character_id。old_master_position
/// 是传位后旧掌门的去向（方案 11.1 没写定，默认 disciple，标待定，代码不写死）；
/// idempotency 是 sect 上幂等列名，只读入口自检，缺段/非法都起不来。
/// </summary>
public sealed class SectTable
{
    public const string WrittenSchema = "sect-table/1";

    // disciple 是默认职位，无席位上限；这四个才是有席位上限的职位。
    public const string DefaultPosition = "disciple";
    public static readonly string[] SeatedPositionIds =
        { "master", "vice_master", "elder", "steward" };

    public sealed record Level(int Number, int MemberCap, string Title);

    public sealed record Seat(string Id, int Cap);

    public sealed record CreateRule(
        int FounderLevelGate, string FounderGoldKind, long FounderGoldCost,
        bool FounderGoldCostPending);

    // P2-1a3：传位只给副掌门。old_master_position 是传位后旧掌门的去向
    // （方案 11.1 没写定，默认 disciple，代码不写死）；idempotency 钉 sect 上的
    // 幂等列名，只读入口自检这一项，缺了就起不来。
    public sealed record TransferRule(
        string Idempotency, string OldMasterPosition, bool OldMasterPositionPending);

    // P2-1a4：升级只耗建设。一档 = 从 from_level 升到 to_level 消耗多少宗门建设。
    // 方案 11.1 写定「升级消耗宗门建设」但没给数字：costs 是正整数占位（待定），
    // 只从 config/sect.json 读，代码 / proto / SQL 不写第二套数字、不硬编码。
    public sealed record UpgradeTier(int FromLevel, int ToLevel, long Cost);

    public sealed record UpgradeRule(
        string Idempotency, bool UpgradeCostPending, string Note,
        IReadOnlyList<UpgradeTier> Costs);

    public int Version { get; }
    public string Note { get; }
    public IReadOnlyList<Level> Levels { get; }
    public IReadOnlyList<Seat> Positions { get; }
    public CreateRule Create { get; }
    public TransferRule Transfer { get; }
    public UpgradeRule Upgrade { get; }

    SectTable(int version, string note, IReadOnlyList<Level> levels,
        IReadOnlyList<Seat> positions, CreateRule create, TransferRule transfer,
        UpgradeRule upgrade)
    {
        Version = version;
        Note = note;
        Levels = levels;
        Positions = positions;
        Create = create;
        Transfer = transfer;
        Upgrade = upgrade;
    }

    /// <summary>5 级封顶：最高档的等级号。升到这一档之后不再有 cost 档。</summary>
    public int MaxLevel => Levels[Levels.Count - 1].Number;

    /// <summary>
    /// 按当前等级取「升下一级」要扣的建设数；5 级封顶（没有下一档）返回 null。
    /// 消耗只从配置正本读，找不到就不给数，调用方据此回 at_max_level / bad_sect_level。
    /// </summary>
    public long? CostFor(int fromLevel)
    {
        foreach (var tier in Upgrade.Costs)
        {
            if (tier.FromLevel == fromLevel)
            {
                return tier.Cost;
            }
        }

        return null;
    }

    /// <summary>按等级取名册上限。等级不在表里时抛 ConfigException，由调用方回拒绝。</summary>
    public int CapFor(int level)
    {
        foreach (var l in Levels)
        {
            if (l.Number == level)
            {
                return l.MemberCap;
            }
        }

        throw new ConfigException("宗门等级不在配置里：" + level);
    }

    /// <summary>五个职位名都认（含默认弟子）；用于校验请求里的 position。</summary>
    public static bool IsKnownPosition(string? position) =>
        position == DefaultPosition || IsSeatedPosition(position);

    /// <summary>只有这四个职位有席位上限；disciple 不占席位。</summary>
    public static bool IsSeatedPosition(string? position) =>
        position is not null && Array.IndexOf(SeatedPositionIds, position) >= 0;

    /// <summary>按职位名取席位上限。席位只从配置正本读，找不到就抛 ConfigException。</summary>
    public int SeatCapFor(string position)
    {
        foreach (var p in Positions)
        {
            if (p.Id == position)
            {
                return p.Cap;
            }
        }

        throw new ConfigException("职位不在配置里：" + position);
    }

    public static SectTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配置文件 " + path);
        }

        var rawText = File.ReadAllText(path);
        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(rawText, JsonOptions)
                ?? throw new ConfigException("配置是空的 " + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配置不是合法 JSON：" + ex.Message);
        }

        // P2-1a4 硬口径：建设和贡献只从宗门秘境和宗门任务来、不从商城买。
        // 配置里任何一处写「商城可买建设」这类反口径，整份配置起不来。
        foreach (var claim in ShopBuyConstructionClaims)
        {
            if (rawText.Contains(claim, StringComparison.Ordinal))
            {
                throw new ConfigException("sect.json 不允许出现「" + claim
                    + "」：建设和贡献只从宗门秘境和宗门任务来、不从商城买");
            }
        }

        if (!string.Equals(wire.Schema, WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException("sect.json 的 schema 必须是 " + WrittenSchema);
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("sect.json 的 version 必须是正整数");
        }

        var raw = wire.Levels;
        if (raw is null || raw.Count == 0)
        {
            throw new ConfigException("sect.json 的 levels 不能为空（缺段）");
        }

        // 方案写定等级仍是五级；少一档、多一档都起不来。
        if (raw.Count != 5)
        {
            throw new ConfigException("sect.json 必须正好五档，现在是 " + raw.Count);
        }

        var levels = new List<Level>();
        var seen = new HashSet<int>();
        long previousCap = 0;
        for (var i = 0; i < raw.Count; i++)
        {
            var l = raw[i];
            if (l is null)
            {
                throw new ConfigException("sect.json 的 levels 有空项");
            }

            // 等级必须正好 1..5 顺序：重复、跳号、缺档都在这里挡住。
            if (l.Level != i + 1 || !seen.Add(l.Level))
            {
                throw new ConfigException("sect.json 第 " + (i + 1) + " 档的 level 必须是 "
                    + (i + 1) + "（等级重复或缺档）");
            }

            if (l.MemberCap <= 0)
            {
                throw new ConfigException("sect.json 第 " + l.Level + " 档 member_cap 必须是正整数");
            }

            if (l.MemberCap <= previousCap)
            {
                throw new ConfigException("sect.json 的 member_cap 必须严格递增，第 "
                    + l.Level + " 档没有比上一档大");
            }

            previousCap = l.MemberCap;

            if ((l.Title ?? "").Trim().Length == 0)
            {
                throw new ConfigException("sect.json 第 " + l.Level + " 档没有 title");
            }

            levels.Add(new Level(l.Level, l.MemberCap, l.Title!.Trim()));
        }

        // ---- P2-1a2：四个有席位职位的硬校验 ----
        var rawPositions = wire.Positions;
        if (rawPositions is null || rawPositions.Count == 0)
        {
            throw new ConfigException("sect.json 缺 positions 段");
        }

        var positions = new List<Seat>();
        var seenPositions = new HashSet<string>();
        foreach (var p in rawPositions)
        {
            if (p is null)
            {
                throw new ConfigException("sect.json 的 positions 有空项");
            }

            var id = (p.Id ?? "").Trim();
            if (id.Length == 0 || !IsSeatedPosition(id))
            {
                throw new ConfigException("sect.json 的 positions 有未知职位：" + id);
            }

            if (!seenPositions.Add(id))
            {
                throw new ConfigException("sect.json 的 positions 职位编号重复：" + id);
            }

            if (p.SeatCap <= 0)
            {
                throw new ConfigException("sect.json 的 " + id + " seat_cap 必须是正整数");
            }

            positions.Add(new Seat(id, p.SeatCap));
        }

        foreach (var required in SeatedPositionIds)
        {
            if (!seenPositions.Contains(required))
            {
                throw new ConfigException("sect.json 的 positions 缺职位：" + required);
            }
        }

        // 方案写定掌门只有 1 席；不是 1 就起不来。
        foreach (var p in positions)
        {
            if (p.Id == "master" && p.Cap != 1)
            {
                throw new ConfigException("sect.json 的掌门席位必须是 1，现在是 " + p.Cap);
            }
        }

        // 职位席位之和不超过 5 级名册上限（按配置挡，不写死 200）。
        var maxCap = levels.Max(l => l.MemberCap);
        long seatSum = 0;
        foreach (var p in positions)
        {
            seatSum += p.Cap;
        }

        if (seatSum > maxCap)
        {
            throw new ConfigException("sect.json 的职位席位之和 " + seatSum
                + " 超过 5 级名册上限 " + maxCap);
        }

        var c = wire.Create;
        if (c is null)
        {
            throw new ConfigException("sect.json 缺 create 段");
        }

        if (c.FounderLevelGate < 1)
        {
            throw new ConfigException("sect.json 的 founder_level_gate 必须是正整数，现在是 "
                + c.FounderLevelGate);
        }

        var kind = (c.FounderGoldKind ?? "").Trim();
        if (kind.Length == 0)
        {
            throw new ConfigException("sect.json 的 founder_gold_kind 不能为空");
        }

        if (c.FounderGoldCost < 0)
        {
            throw new ConfigException("sect.json 的 founder_gold_cost 不能为负");
        }

        var create = new CreateRule(c.FounderLevelGate, kind, c.FounderGoldCost,
            c.FounderGoldCostPending);

        // ---- P2-1a3：传位段的硬校验 ----
        var t = wire.Transfer;
        if (t is null)
        {
            throw new ConfigException("sect.json 缺 transfer 段");
        }

        // 幂等列自检：缺 idempotency / 值不对都起不来（挡「幂等列缺失」）。
        var idempotency = (t.Idempotency ?? "").Trim();
        if (idempotency.Length == 0
            || !string.Equals(idempotency, "last_transfer_txn", StringComparison.Ordinal))
        {
            throw new ConfigException(
                "sect.json 的 transfer.idempotency 必须是 last_transfer_txn（幂等列缺失自检）");
        }

        // 旧掌门去向：方案 11.1 没写定，默认 disciple（配置里标待定），代码不写死。
        // 不能还是 master，否则提交后名册可能出现两条 master 行、破掌门 1 席。
        var oldMaster = (t.OldMasterPosition ?? "").Trim();
        if (!IsKnownPosition(oldMaster) || oldMaster == "master")
        {
            throw new ConfigException("sect.json 的 transfer.old_master_position 非法：" + oldMaster);
        }

        var transfer = new TransferRule(idempotency, oldMaster, t.OldMasterPositionPending);

        // ---- P2-1a4：升级段的硬校验 ----
        var up = wire.Upgrade;
        if (up is null)
        {
            throw new ConfigException("sect.json 缺 upgrade 段");
        }

        // 幂等列自检：idempotency 必须钉 sect 上的 last_upgrade_txn（挡「幂等列缺失」）。
        var upgradeIdempotency = (up.Idempotency ?? "").Trim();
        if (!string.Equals(upgradeIdempotency, "last_upgrade_txn", StringComparison.Ordinal))
        {
            throw new ConfigException(
                "sect.json 的 upgrade.idempotency 必须是 last_upgrade_txn（幂等列缺失自检）");
        }

        var rawCosts = up.Costs;
        if (rawCosts is null || rawCosts.Count == 0)
        {
            throw new ConfigException("sect.json 的 upgrade.costs 不能为空（缺档）");
        }

        // 五级 → 正好 levels.Count-1 档升级（1→2 / 2→3 / 3→4 / 4→5）。
        // 多一档（5 级还配 cost）/ 少一档（缺档）都起不来；档数不写死，从五档推。
        var expectedTiers = levels.Count - 1;
        if (rawCosts.Count != expectedTiers)
        {
            throw new ConfigException("sect.json 的 upgrade.costs 必须正好 " + expectedTiers
                + " 档（四档齐全、5 级不能再升），现在是 " + rawCosts.Count);
        }

        var costs = new List<UpgradeTier>();
        for (var i = 0; i < rawCosts.Count; i++)
        {
            var tier = rawCosts[i];
            if (tier is null)
            {
                throw new ConfigException("sect.json 的 upgrade.costs 有空项");
            }

            // 等级必须连续且从 1 起：不允许跳级（请求里也没有目标等级），
            // 也不允许把 5 级配成升级起点（5 级封顶）。
            if (tier.FromLevel != i + 1 || tier.ToLevel != tier.FromLevel + 1)
            {
                throw new ConfigException("sect.json 的 upgrade.costs 第 " + (i + 1)
                    + " 档等级不连续（必须 " + (i + 1) + "→" + (i + 2) + "）");
            }

            if (tier.Cost <= 0)
            {
                throw new ConfigException("sect.json 的 upgrade.costs 第 " + (i + 1)
                    + " 档 cost 必须是正整数（建设不足才能被拒）");
            }

            costs.Add(new UpgradeTier(tier.FromLevel, tier.ToLevel, tier.Cost));
        }

        var upgrade = new UpgradeRule(upgradeIdempotency, up.UpgradeCostPending,
            (up.Note ?? "").Trim(), costs);

        return new SectTable(wire.Version, (wire.Note ?? "").Trim(), levels, positions,
            create, transfer, upgrade);
    }

    // P2-1a4 硬口径：建设和贡献只从宗门秘境和宗门任务来、不从商城买。
    // 配置里出现这种「商城可买建设」的反口径就起不来（坏配置自检）。
    static readonly string[] ShopBuyConstructionClaims =
    {
        "商城可买建设", "商城可以买建设", "建设可从商城买", "建设能在商城买",
        "商城出售建设", "商城可买贡献",
    };

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
        [JsonPropertyName("levels")] public List<WireLevel?>? Levels { get; set; }
        [JsonPropertyName("positions")] public List<WirePosition?>? Positions { get; set; }
        [JsonPropertyName("create")] public WireCreate? Create { get; set; }
        [JsonPropertyName("transfer")] public WireTransfer? Transfer { get; set; }
        [JsonPropertyName("upgrade")] public WireUpgrade? Upgrade { get; set; }
    }

    sealed class WireLevel
    {
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("member_cap")] public int MemberCap { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }

    sealed class WirePosition
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("seat_cap")] public int SeatCap { get; set; }
    }

    sealed class WireCreate
    {
        [JsonPropertyName("founder_level_gate")] public int FounderLevelGate { get; set; }
        [JsonPropertyName("founder_gold_kind")] public string? FounderGoldKind { get; set; }
        [JsonPropertyName("founder_gold_cost")] public long FounderGoldCost { get; set; }
        [JsonPropertyName("founder_gold_cost_pending")] public bool FounderGoldCostPending { get; set; }
    }

    sealed class WireTransfer
    {
        [JsonPropertyName("idempotency")] public string? Idempotency { get; set; }
        [JsonPropertyName("old_master_position")] public string? OldMasterPosition { get; set; }
        [JsonPropertyName("old_master_position_pending")] public bool OldMasterPositionPending { get; set; }
    }

    sealed class WireUpgrade
    {
        [JsonPropertyName("idempotency")] public string? Idempotency { get; set; }
        [JsonPropertyName("upgrade_cost_pending")] public bool UpgradeCostPending { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("costs")] public List<WireUpgradeTier?>? Costs { get; set; }
    }

    sealed class WireUpgradeTier
    {
        [JsonPropertyName("from_level")] public int FromLevel { get; set; }
        [JsonPropertyName("to_level")] public int ToLevel { get; set; }
        [JsonPropertyName("cost")] public long Cost { get; set; }
    }
}
