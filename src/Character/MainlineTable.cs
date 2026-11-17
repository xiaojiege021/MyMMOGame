using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>十二场里引用的一句定词：正文只在 config/dialogue/&lt;人名&gt;.json 存一份。</summary>
public sealed record MainlineLine(string Speaker, int Chapter);

/// <summary>主线十二场里的一场。这里只有内容与门槛，没有任何奖励 / 概率 / 掉落数字。</summary>
public sealed record MainlineStage(
    int Stage,
    string Title,
    string Happens,
    string Teaches,
    IReadOnlyList<string> Regions,
    string Faction,
    IReadOnlyList<string> Speakers,
    IReadOnlyList<MainlineLine> Dialogue,
    int LevelMin,
    string RequiresBreakthrough,
    bool AlignmentChoice,
    string Battle);

/// <summary>
/// C1 主线十二场的内容唯一正本（config/mainline.json）。开发架构「主顺序之外」第 627 行：
/// 十二场都是单人、不扣疲劳、战斗仍走已经有的进本规则；第 10 场等炼体成功、第 11 场
/// 等筑基成功、第 12 场等结丹成功；地名、势力名、开口的人和定词用方案正文 2.5 到 2.8，
/// 不另起名字；交任务使用 P1-1b5 的事务号。
///
/// 加载期硬校验（不是运行期再判一遍）：
///   · 正好十二场、序号连续 1..12；关卡名 / 发生的事 / 结束时玩家会什么都不许空。
///   · 地名 / 势力 / 开口的人只能从本表顶层那三份名单里取，名单逐字照方案 2.5–2.7。
///   · 定词只引用「人名 + 场次」，不复写正文；引用的人必须在名单里、场次必须等于本场。
///   · 十二场的 battle 全部指向 dungeons.json 里那条 main，而且那条必须是 1 人、
///     不扣疲劳、不占周份额——「只加内容和配置」的结构保证，不为每场新做一种进本。
///   · 等级门槛非递减、只在 1..100；requires_breakthrough 只许落在 10/11/12 场，
///     且依次是炼体 / 筑基 / 结丹（都在首发三段里，不改 realms.json 的版本开关）。
///   · alignment_choice 只许第 10 场为 true：立仙或立魔只在这一场写入。
///   · 顶层与条目一律不许出现奖励 / 掉落 / 概率 / 数值一类的键：掉落与发奖链路
///     没有承接卡，本卡不塞占位奖励，也不把数值写进代码。
/// </summary>
public sealed class MainlineTable
{
    public const string WrittenSchema = "mainline-table/1";
    public const int StageCount = 12;
    public const string MainDungeon = "main";

    // K6：地名 / 势力 / 开口的人不再写死在代码里。顶层三份名单只在 mainline.json 存一次，
    // 再用同目录的 world.json 逐项交叉校验（真实配置一定在同一目录；老卡的临时单文件
    // 副本没有 world.json 时只做名单内部一致性校验，不误报）。
    public static readonly string[] WrittenAlignments = ["立仙", "立魔"];

    // 第 10/11/12 场分别等这三段突破成功（开发架构第 627 行的原文）。
    public static readonly string[] RequiredBreakthroughs = ["炼体", "筑基", "结丹"];

    static readonly string[] AllowedTopKeys =
    [
        "schema", "version", "pending", "note", "battle_dungeon",
        "alignment_choices", "places", "factions", "speakers", "stages",
    ];
    static readonly string[] RequiredTopKeys =
    [
        "schema", "version", "note", "battle_dungeon", "alignment_choices",
        "places", "factions", "speakers", "stages",
    ];
    static readonly string[] AllowedStageKeys =
    [
        "stage", "title", "happens", "teaches", "regions", "faction",
        "speakers", "dialogue", "level_min", "requires_breakthrough",
        "alignment_choice", "battle",
    ];
    static readonly string[] AllowedLineKeys = ["speaker", "chapter"];
    // 奖励 / 掉落 / 数值一类的键长不出来。方案没写定的数值留在配置里，不进代码。
    static readonly string[] ForbiddenKeys =
    [
        "reward", "rewards", "drop", "loot", "gold", "stone", "spirit_stone", "yuanbao",
        "exp", "base_exp", "item", "items", "equip", "equipment", "fatigue", "dungeon_count",
        "weekly_share", "price", "cost", "probability", "success_pct", "rate", "attribute",
        "power", "tax", "vein", "owner_sect", "level", "realm", "passive",
    ];

    public int Version { get; }
    public bool Pending { get; }
    public string BattleDungeon { get; }
    public IReadOnlyList<string> Places { get; }
    public IReadOnlyList<string> Factions { get; }
    public IReadOnlyList<string> Speakers { get; }
    public IReadOnlyList<string> AlignmentChoices { get; }
    public IReadOnlyList<MainlineStage> Stages { get; }
    public bool IsMissing { get; }

    MainlineTable(
        int version, bool pending, string battleDungeon,
        IReadOnlyList<string> places, IReadOnlyList<string> factions,
        IReadOnlyList<string> speakers, IReadOnlyList<string> alignmentChoices,
        IReadOnlyList<MainlineStage> stages, bool missing)
    {
        Version = version;
        Pending = pending;
        BattleDungeon = battleDungeon;
        Places = places;
        Factions = factions;
        Speakers = speakers;
        AlignmentChoices = alignmentChoices;
        Stages = stages;
        IsMissing = missing;
    }

    /// <summary>老卡的临时配置目录没有这份表：降级成「没有主线内容」，不挡启动。</summary>
    public static MainlineTable Missing() => new(
        0, false, "", [], [], [], [], [], missing: true);

    public MainlineStage? Find(int stage) =>
        Stages.FirstOrDefault(s => s.Stage == stage);

    public static MainlineTable Load(string path, DungeonTable dungeons, RealmTable realms)
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
            throw new ConfigException("mainline.json 不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            foreach (var key in RequiredTopKeys)
            {
                if (!root.TryGetProperty(key, out _))
                {
                    throw new ConfigException("mainline.json 缺字段 " + key);
                }
            }

            RejectUnknownKeys(root, AllowedTopKeys, "顶层");
            RejectForbiddenKeys(root, "顶层");

            Wire wire;
            try
            {
                wire = JsonSerializer.Deserialize<Wire>(text, JsonOptions)
                    ?? throw new ConfigException("mainline.json 是空的");
            }
            catch (JsonException ex)
            {
                throw new ConfigException("mainline.json 字段类型不对：" + ex.Message);
            }

            if (!string.Equals((wire.Schema ?? "").Trim(), WrittenSchema, StringComparison.Ordinal))
            {
                throw new ConfigException("mainline.json 的 schema 必须是 " + WrittenSchema);
            }

            if (wire.Version is null || wire.Version.Value != 1)
            {
                throw new ConfigException("mainline.json 的 version 必须是 1");
            }

            var battleDungeon = (wire.BattleDungeon ?? "").Trim();
            if (!string.Equals(battleDungeon, MainDungeon, StringComparison.Ordinal))
            {
                throw new ConfigException("mainline.json 的 battle_dungeon 只能是 " + MainDungeon);
            }

            // 十二场都挂已有那条单人主线：C1 不新做一种进本，也不改既有规则。
            var main = dungeons.Find(MainDungeon)
                ?? throw new ConfigException("dungeons.json 里没有 " + MainDungeon + " 这条主线");
            if (main.MaxPlayers != 1 || main.ChargesFatigue || main.TakesWeeklyShare)
            {
                throw new ConfigException("mainline.json 的 " + MainDungeon
                    + " 必须是 1 人、不扣疲劳、不占周份额");
            }

            var places = ReadNames(wire.Places, "places");
            var factions = ReadNames(wire.Factions, "factions");
            var speakers = ReadNames(wire.Speakers, "speakers");
            var alignments = ReadNames(wire.AlignmentChoices, "alignment_choices");
            CheckAgainstWorld(path, places, factions, speakers);

            var array = root.GetProperty("stages");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != StageCount)
            {
                throw new ConfigException("mainline.json 的 stages 必须正好 " + StageCount + " 条");
            }

            if (wire.Stages is null || wire.Stages.Count != StageCount)
            {
                throw new ConfigException("mainline.json 的 stages 必须正好 " + StageCount + " 条");
            }

            var stages = new List<MainlineStage>(StageCount);
            var lastLevel = 0;
            for (var i = 0; i < StageCount; i++)
            {
                var element = array[i];
                RejectUnknownKeys(element, AllowedStageKeys, "第 " + (i + 1) + " 场");
                RejectForbiddenKeys(element, "第 " + (i + 1) + " 场");
                var row = wire.Stages[i];
                var stage = row.Stage ?? 0;
                if (stage != i + 1)
                {
                    throw new ConfigException("mainline.json 第 " + (i + 1) + " 条的 stage 必须是 " + (i + 1));
                }

                var title = (row.Title ?? "").Trim();
                var happens = (row.Happens ?? "").Trim();
                var teaches = (row.Teaches ?? "").Trim();
                if (title.Length == 0 || happens.Length == 0 || teaches.Length == 0)
                {
                    throw new ConfigException("mainline.json 第 " + stage + " 场的标题 / 经过 / 收获不许空");
                }

                var regions = (row.Regions ?? []).Select(r => (r ?? "").Trim())
                    .Where(r => r.Length > 0).ToList();
                foreach (var region in regions)
                {
                    if (!places.Contains(region, StringComparer.Ordinal))
                    {
                        throw new ConfigException("mainline.json 第 " + stage + " 场的地表 " + region
                            + " 不在 places 名单里（方案 2.5 不另起地名）");
                    }
                }

                var faction = (row.Faction ?? "").Trim();
                if (faction.Length == 0 || !factions.Contains(faction, StringComparer.Ordinal))
                {
                    throw new ConfigException("mainline.json 第 " + stage + " 场的势力 " + faction
                        + " 不在 factions 名单里（方案 2.6 不另起势力名）");
                }

                var stageSpeakers = (row.Speakers ?? []).Select(s => (s ?? "").Trim())
                    .Where(s => s.Length > 0).ToList();
                if (stageSpeakers.Count == 0 || stageSpeakers.Distinct(StringComparer.Ordinal).Count() != stageSpeakers.Count)
                {
                    throw new ConfigException("mainline.json 第 " + stage + " 场的开口的人不许空或重复");
                }

                var lines = new List<MainlineLine>();
                var lineArray = element.GetProperty("dialogue");
                if (lineArray.ValueKind != JsonValueKind.Array)
                {
                    throw new ConfigException("mainline.json 第 " + stage + " 场的 dialogue 必须是数组");
                }

                for (var j = 0; j < lineArray.GetArrayLength(); j++)
                {
                    var lineElement = lineArray[j];
                    RejectUnknownKeys(lineElement, AllowedLineKeys, "第 " + stage + " 场第 " + (j + 1) + " 句");
                    RejectForbiddenKeys(lineElement, "第 " + stage + " 场第 " + (j + 1) + " 句");
                    var line = row.Dialogue is null || j >= row.Dialogue.Count ? null : row.Dialogue[j];
                    var speaker = (line?.Speaker ?? "").Trim();
                    var chapter = line?.Chapter ?? 0;
                    if (!speakers.Contains(speaker, StringComparer.Ordinal)
                        || !stageSpeakers.Contains(speaker, StringComparer.Ordinal)
                        || chapter != stage)
                    {
                        throw new ConfigException("mainline.json 第 " + stage + " 场的定词引用必须是本场开口的人");
                    }

                    lines.Add(new MainlineLine(speaker, chapter));
                }

                if (lines.Count == 0 || lines.Count != stageSpeakers.Count)
                {
                    throw new ConfigException("mainline.json 第 " + stage + " 场每个开口的人都要有一句定词引用");
                }

                var levelMin = row.LevelMin ?? 0;
                if (levelMin < 1 || levelMin > 100 || levelMin < lastLevel)
                {
                    throw new ConfigException("mainline.json 第 " + stage + " 场的 level_min 要在 1..100 且不低于前一场");
                }

                lastLevel = levelMin;
                var required = (row.RequiresBreakthrough ?? "").Trim();
                var expected = stage >= 10 && stage <= 12 ? RequiredBreakthroughs[stage - 10] : "";
                if (!string.Equals(required, expected, StringComparison.Ordinal))
                {
                    throw new ConfigException("mainline.json 第 " + stage + " 场的 requires_breakthrough 必须是 "
                        + (expected.Length == 0 ? "空" : expected));
                }

                if (required.Length > 0)
                {
                    var rank = realms.Rank(required);
                    if (rank < 1 || rank > realms.Rank(realms.LaunchUntil))
                    {
                        throw new ConfigException("mainline.json 第 " + stage + " 场等的是首发三段的突破：" + required);
                    }
                }

                var choice = row.AlignmentChoice ?? false;
                if (choice != (stage == 10))
                {
                    throw new ConfigException("mainline.json 只有第 10 场能写 alignment_choice");
                }

                var battle = (row.Battle ?? "").Trim();
                if (!string.Equals(battle, MainDungeon, StringComparison.Ordinal))
                {
                    throw new ConfigException("mainline.json 第 " + stage + " 场的 battle 只能是 " + MainDungeon);
                }

                stages.Add(new MainlineStage(
                    stage, title, happens, teaches, regions, faction, stageSpeakers,
                    lines, levelMin, required, choice, battle));
            }

            return new MainlineTable(
                wire.Version.Value, wire.Pending ?? false, battleDungeon,
                places, factions, speakers, alignments, stages, missing: false);
        }
    }

    static IReadOnlyList<string> ReadNames(List<string?>? raw, string field)
    {
        var names = (raw ?? []).Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).ToList();
        if (names.Count == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            throw new ConfigException("mainline.json 的 " + field + " 不能为空、也不能重复");
        }

        return names;
    }

    /// <summary>
    /// K6：mainline.json 的三份名单与同目录 world.json 逐项一致。world.json 是地名 / 势力 /
    /// 开口人的正本；临时目录里没有 world.json 时跳过（只做名单内部一致性校验）。
    /// </summary>
    static void CheckAgainstWorld(
        string path, IReadOnlyList<string> places, IReadOnlyList<string> factions,
        IReadOnlyList<string> speakers)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var worldPath = Path.Combine(dir, "world.json");
        if (!File.Exists(worldPath))
        {
            return;
        }

        WorldNames world;
        try
        {
            world = JsonSerializer.Deserialize<WorldNames>(File.ReadAllText(worldPath), JsonOptions)
                ?? throw new ConfigException("world.json 是空的");
        }
        catch (JsonException ex)
        {
            throw new ConfigException("world.json 字段类型不对：" + ex.Message);
        }

        Same("places", places, world.Regions, "world.json 的 regions");
        Same("factions", factions, world.Factions, "world.json 的 factions");
        Same("speakers", speakers, world.Speakers, "world.json 的 speakers");

        static void Same(string field, IReadOnlyList<string> actual,
            List<string>? expected, string source)
        {
            var want = (expected ?? []).Select(n => (n ?? "").Trim())
                .Where(n => n.Length > 0).ToList();
            if (want.Count != actual.Count || want.Any(n => !actual.Contains(n, StringComparer.Ordinal)))
            {
                throw new ConfigException("mainline.json 的 " + field + " 与 " + source + " 对不上");
            }
        }
    }

    sealed class WorldNames
    {
        public List<string>? Regions { get; init; }
        public List<string>? Factions { get; init; }
        public List<string>? Speakers { get; init; }
    }

    static void RejectUnknownKeys(JsonElement element, IReadOnlyList<string> allowed, string where)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new ConfigException("mainline.json " + where + " 不认这个键：" + property.Name);
            }
        }
    }

    static void RejectForbiddenKeys(JsonElement element, string where)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (ForbiddenKeys.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new ConfigException("mainline.json " + where + " 不许出现奖励 / 数值键："
                    + property.Name);
            }
        }
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

        [JsonPropertyName("battle_dungeon")]
        public string? BattleDungeon { get; init; }

        [JsonPropertyName("alignment_choices")]
        public List<string?>? AlignmentChoices { get; init; }

        [JsonPropertyName("places")]
        public List<string?>? Places { get; init; }

        [JsonPropertyName("factions")]
        public List<string?>? Factions { get; init; }

        [JsonPropertyName("speakers")]
        public List<string?>? Speakers { get; init; }

        [JsonPropertyName("stages")]
        public List<StageWire>? Stages { get; init; }
    }

    sealed record StageWire
    {
        [JsonPropertyName("stage")]
        public int? Stage { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("happens")]
        public string? Happens { get; init; }

        [JsonPropertyName("teaches")]
        public string? Teaches { get; init; }

        [JsonPropertyName("regions")]
        public List<string?>? Regions { get; init; }

        [JsonPropertyName("faction")]
        public string? Faction { get; init; }

        [JsonPropertyName("speakers")]
        public List<string?>? Speakers { get; init; }

        [JsonPropertyName("dialogue")]
        public List<LineWire>? Dialogue { get; init; }

        [JsonPropertyName("level_min")]
        public int? LevelMin { get; init; }

        [JsonPropertyName("requires_breakthrough")]
        public string? RequiresBreakthrough { get; init; }

        [JsonPropertyName("alignment_choice")]
        public bool? AlignmentChoice { get; init; }

        [JsonPropertyName("battle")]
        public string? Battle { get; init; }
    }

    sealed record LineWire
    {
        [JsonPropertyName("speaker")]
        public string? Speaker { get; init; }

        [JsonPropertyName("chapter")]
        public int? Chapter { get; init; }
    }
}
