using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Character;

/// <summary>一个副职：编号与界面上的名字。四个副职的顺序由方案 12 章写定。</summary>
public sealed record ProfessionDef(string Id, string Name);

/// <summary>一条配方的材料：物品编号 + 件数。物品必须先在 items.json 里。</summary>
public sealed record RecipeMaterial(string ItemId, int Count);

/// <summary>一条配方的费用：币种 + 数量。币种必须在 currency.json 里。</summary>
public sealed record RecipeFee(string Kind, long Amount);

/// <summary>
/// B3：装备配方的那一次掷点区间。下限不低于该品阶基准、上限不超过下一档基准
/// （方案 9.1），对账在 CraftRollTable.CheckRecipes；这里只装配置里的三个数。
/// </summary>
public sealed record RecipeRoll(int AttrMin, int AttrMax, int ExtrasMax);

/// <summary>
/// 一条配方。材料 / 产出 / 费用 / 成功率 / 耗时 / 解锁等级都在这里，
/// 代码只按这些字段执行，不按配方编号分支，也不写第二套数值。
/// Grade 与 Roll 只对产装备的配方有值（B2 的品阶、B3 的掷点）。
/// </summary>
public sealed record RecipeDef(
    string Id,
    string Profession,
    int UnlockLevel,
    IReadOnlyList<RecipeMaterial> Inputs,
    IReadOnlyList<RecipeFee> Fee,
    string OutputItemId,
    int OutputCount,
    double SuccessPct,
    int CraftTimeS,
    string Note,
    string Grade = "",
    RecipeRoll? Roll = null);

/// <summary>
/// config/recipes.json 是生产 / 配方的唯一内容正本（配置设计第 7 节，14.7 的 K3 行）。
///
/// 四个副职按方案 12 章「炼丹 → 铁匠 → 裁缝 → 巧匠」写定，顺序在加载期校验；
/// 每条配方的 profession 必须落在四个里，每个副职至少要有一条配方，否则起不来。
/// 材料 / 产出必须指向 items.json 的真物品，费用币种必须在 currency.json；
/// success_pct 与 craft_time_s 只从这份表读，代码里不写第二套概率与时长。
/// grade_roll（B3 的「胚子做成装备时掷一次」）产装备必填、非装备不许写；
/// 区间与品阶基准的对账在 CraftRollTable.CheckRecipes，代码里不写第二套数字。
/// </summary>
public sealed class RecipeTable
{
    /// <summary>方案 12 章写定的四个副职与顺序。加副职是改玩法，走新卡，不在配置里悄悄加。</summary>
    public static readonly string[] WrittenProfessionIds =
        ["alchemy", "blacksmith", "tailor", "jeweler"];

    static readonly Regex IdPattern = new("^recipe_[a-z0-9_]+$", RegexOptions.Compiled);

    readonly Dictionary<string, RecipeDef> byId = new(StringComparer.Ordinal);
    readonly Dictionary<string, ProfessionDef> professionsById = new(StringComparer.Ordinal);

    public int Version { get; }

    /// <summary>整表是不是还没定数。与数值台账表 B 的集合一一对应。</summary>
    public bool Pending { get; }

    public IReadOnlyList<ProfessionDef> Professions { get; }

    public IReadOnlyList<RecipeDef> Recipes { get; }

    RecipeTable(
        int version,
        bool pending,
        IReadOnlyList<ProfessionDef> professions,
        IReadOnlyList<RecipeDef> recipes)
    {
        Version = version;
        Pending = pending;
        Professions = professions;
        Recipes = recipes;
        foreach (var profession in professions)
        {
            professionsById[profession.Id] = profession;
        }

        foreach (var recipe in recipes)
        {
            byId[recipe.Id] = recipe;
        }
    }

    public RecipeDef? Find(string id) => byId.GetValueOrDefault(id);

    public ProfessionDef? FindProfession(string id) => professionsById.GetValueOrDefault(id);

    public IReadOnlyList<RecipeDef> ForProfession(string profession) =>
        Recipes.Where(r => string.Equals(r.Profession, profession, StringComparison.Ordinal)).ToList();

    public static RecipeTable Load(string path, ItemTable items, CurrencyTable currencies)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到配方表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("配方表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配方表不是合法 JSON：" + ex.Message);
        }

        if (!string.Equals((wire.Schema ?? "").Trim(), "recipe-table/1", StringComparison.Ordinal))
        {
            throw new ConfigException("recipes.json 的 schema 必须是 recipe-table/1");
        }

        if (wire.Version < 1)
        {
            throw new ConfigException("recipes.json 的 version 必须大于 0");
        }

        // 四个副职：编号与顺序必须是方案 12 章写定的那四个；名字只查非空与不重复，
        // 中文名是内容，不进代码。
        var rawProfessions = wire.Professions ?? [];
        if (rawProfessions.Count != WrittenProfessionIds.Length)
        {
            throw new ConfigException("recipes.json 的 professions 必须正好是方案写定的四个副职");
        }

        var professions = new List<ProfessionDef>(rawProfessions.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rawProfessions.Count; i++)
        {
            var raw = rawProfessions[i];
            var id = (raw.Id ?? "").Trim();
            if (!string.Equals(id, WrittenProfessionIds[i], StringComparison.Ordinal))
            {
                throw new ConfigException(
                    "recipes.json 第 " + (i + 1) + " 个副职必须是 " + WrittenProfessionIds[i]);
            }

            var name = (raw.Name ?? "").Trim();
            if (name.Length == 0)
            {
                throw new ConfigException("recipes.json 的副职 " + id + " 缺 name");
            }

            if (!names.Add(name))
            {
                throw new ConfigException("recipes.json 的副职名重复：" + name);
            }

            professions.Add(new ProfessionDef(id, name));
        }

        var rawRecipes = wire.Recipes ?? [];
        if (rawRecipes.Count == 0)
        {
            throw new ConfigException("recipes.json 一条配方都没有");
        }

        var parsed = new List<RecipeDef>(rawRecipes.Count);
        var recipeIds = new HashSet<string>(StringComparer.Ordinal);
        var usedProfessions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in rawRecipes)
        {
            var id = (raw.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException("recipes.json 里有配方缺 id");
            }

            if (!IdPattern.IsMatch(id))
            {
                throw new ConfigException("recipes.json 的配方编号不合形状（recipe_<小写编号>）：" + id);
            }

            if (!recipeIds.Add(id))
            {
                throw new ConfigException("recipes.json 的配方编号重复：" + id);
            }

            var profession = (raw.Profession ?? "").Trim();
            if (!ContainsProfession(professions, profession))
            {
                throw new ConfigException("配方 " + id + " 的 profession 不在四个副职里：" + profession);
            }

            var unlockLevel = raw.UnlockLevel
                ?? throw new ConfigException("配方 " + id + " 缺 unlock_level");
            if (unlockLevel < 1 || unlockLevel > 100)
            {
                throw new ConfigException("配方 " + id + " 的 unlock_level 必须在 1 到 100 之间");
            }

            var rawInputs = raw.Inputs ?? [];
            if (rawInputs.Count == 0)
            {
                throw new ConfigException("配方 " + id + " 没有任何材料");
            }

            var inputs = new List<RecipeMaterial>(rawInputs.Count);
            var inputIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in rawInputs)
            {
                var itemId = (input.ItemId ?? "").Trim();
                if (itemId.Length == 0)
                {
                    throw new ConfigException("配方 " + id + " 有材料缺 item_id");
                }

                if (items.Find(itemId) is null)
                {
                    throw new ConfigException("配方 " + id + " 的材料不在 items.json 里：" + itemId);
                }

                var count = input.Count ?? 0;
                if (count < 1)
                {
                    throw new ConfigException("配方 " + id + " 的材料 " + itemId + " 件数必须大于 0");
                }

                if (!inputIds.Add(itemId))
                {
                    throw new ConfigException("配方 " + id + " 同一种材料写了两遍：" + itemId);
                }

                inputs.Add(new RecipeMaterial(itemId, count));
            }

            // 费用可以整段不写；写了就每个币种都要在币种表里，数量不能为负。
            var fee = new List<RecipeFee>();
            foreach (var pair in (raw.Fee ?? new Dictionary<string, long>())
                         .OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var kind = (pair.Key ?? "").Trim();
                if (kind.Length == 0)
                {
                    throw new ConfigException("配方 " + id + " 的费用币种是空串");
                }

                if (currencies.Find(kind) is null)
                {
                    throw new ConfigException("配方 " + id + " 的费用币种不在 currency.json 里：" + kind);
                }

                if (pair.Value < 0)
                {
                    throw new ConfigException("配方 " + id + " 的费用 " + kind + " 不能为负");
                }

                fee.Add(new RecipeFee(kind, pair.Value));
            }

            var output = raw.Output ?? throw new ConfigException("配方 " + id + " 缺 output");
            var outputId = (output.ItemId ?? "").Trim();
            if (outputId.Length == 0)
            {
                throw new ConfigException("配方 " + id + " 的 output 缺 item_id");
            }

            var outputItem = items.Find(outputId)
                ?? throw new ConfigException("配方 " + id + " 的产出不在 items.json 里：" + outputId);
            var outputCount = output.Count ?? 0;
            if (outputCount < 1)
            {
                throw new ConfigException("配方 " + id + " 的产出件数必须大于 0");
            }

            // 产出是一笔入包，单次不能超过堆叠上限；要更多件就多写一条配方或多次制作。
            if (outputCount > outputItem.StackMax)
            {
                throw new ConfigException("配方 " + id + " 的产出 " + outputId
                    + " 一件次超过堆叠上限 " + outputItem.StackMax);
            }

            // B2：制作按品阶。产出是装备时必须写 grade 且与成品一致（数值仍只在 items.json，
            // 这里只挡「配方与成品对不上」）；产出不是装备的不许写 grade，避免多一份品阶口径。
            var recipeGrade = (raw.Grade ?? "").Trim();
            if (outputItem.IsEquipment)
            {
                if (recipeGrade.Length == 0)
                {
                    throw new ConfigException("配方 " + id + " 产出的是装备，必须写 grade");
                }

                if (!string.Equals(recipeGrade, outputItem.Grade, StringComparison.Ordinal))
                {
                    throw new ConfigException("配方 " + id + " 的 grade 与成品不一致：配方 " + recipeGrade
                        + "，成品 " + outputId + " 是 " + outputItem.Grade);
                }
            }
            else if (recipeGrade.Length > 0)
            {
                throw new ConfigException("配方 " + id + " 的产出不是装备，不许写 grade");
            }

            var success = raw.SuccessPct
                ?? throw new ConfigException("配方 " + id + " 缺 success_pct");
            if (double.IsNaN(success) || double.IsInfinity(success) || success < 0.0 || success > 100.0)
            {
                throw new ConfigException("配方 " + id + " 的 success_pct 必须在 0 到 100 之间");
            }

            var craftTime = raw.CraftTimeS
                ?? throw new ConfigException("配方 " + id + " 缺 craft_time_s");
            if (craftTime < 0 || craftTime > 86400)
            {
                throw new ConfigException("配方 " + id + " 的 craft_time_s 必须在 0 到 86400 之间");
            }

            // B3：产出装备的配方必须写 grade_roll（方案 9.1 的那一次掷点），
            // 非装备一条都不许写。区间上限与本档 / 下一档基准的对账在 CraftRollTable.CheckRecipes。
            var roll = ParseRoll(id, outputItem, raw.GradeRoll);

            parsed.Add(new RecipeDef(
                id, profession, unlockLevel, inputs, fee, outputId, outputCount,
                success, craftTime, (raw.Note ?? "").Trim(), recipeGrade, roll));
            usedProfessions.Add(profession);
        }

        foreach (var profession in professions)
        {
            if (!usedProfessions.Contains(profession.Id))
            {
                throw new ConfigException("副职 " + profession.Id + " 一条配方都没有");
            }
        }

        return new RecipeTable(wire.Version, wire.Pending, professions, parsed);
    }

    /// <summary>
    /// B3：解析 grade_roll。产出装备的配方必填（方案 9.1 的「做成装备那一次掷点」），
    /// 产出不是装备的一条都不许写；三个数缺一个就起不来，不兜默认值。
    /// </summary>
    static RecipeRoll? ParseRoll(string id, ItemDef outputItem, JsonElement? raw)
    {
        var has = raw is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };
        if (!outputItem.IsEquipment)
        {
            if (has)
            {
                throw new ConfigException("配方 " + id + " 的产出不是装备，不许写 grade_roll");
            }

            return null;
        }

        if (!has)
        {
            throw new ConfigException(
                "配方 " + id + " 产出的是装备，必须写 grade_roll（方案 9.1 的掷点）");
        }

        RollWire wire;
        try
        {
            wire = raw!.Value.Deserialize<RollWire>()
                ?? throw new ConfigException("配方 " + id + " 的 grade_roll 解析为空");
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配方 " + id + " 的 grade_roll 不是合法 JSON：" + ex.Message);
        }

        var min = wire.AttrMin ?? throw new ConfigException("配方 " + id + " 的 grade_roll 缺 attr_min");
        var max = wire.AttrMax ?? throw new ConfigException("配方 " + id + " 的 grade_roll 缺 attr_max");
        var extras = wire.ExtrasMax
            ?? throw new ConfigException("配方 " + id + " 的 grade_roll 缺 extras_max");
        return new RecipeRoll(min, max, extras);
    }

    static bool ContainsProfession(IReadOnlyList<ProfessionDef> professions, string id)
    {
        for (var i = 0; i < professions.Count; i++)
        {
            if (string.Equals(professions[i].Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("pending")]
        public bool Pending { get; init; }

        [JsonPropertyName("professions")]
        public List<ProfessionWire>? Professions { get; init; }

        [JsonPropertyName("recipes")]
        public List<RecipeWire>? Recipes { get; init; }
    }

    sealed record ProfessionWire
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    sealed record RecipeWire
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("profession")]
        public string? Profession { get; init; }

        [JsonPropertyName("unlock_level")]
        public int? UnlockLevel { get; init; }

        [JsonPropertyName("inputs")]
        public List<InputWire>? Inputs { get; init; }

        [JsonPropertyName("fee")]
        public Dictionary<string, long>? Fee { get; init; }

        [JsonPropertyName("output")]
        public OutputWire? Output { get; init; }

        [JsonPropertyName("success_pct")]
        public double? SuccessPct { get; init; }

        [JsonPropertyName("craft_time_s")]
        public int? CraftTimeS { get; init; }

        // B2：产出装备的配方要声明品阶，与成品一致；产出不是装备的不许写。
        [JsonPropertyName("grade")]
        public string? Grade { get; init; }

        [JsonPropertyName("grade_roll")]
        public JsonElement? GradeRoll { get; init; }

        [JsonPropertyName("note")]
        public string? Note { get; init; }
    }

    sealed record RollWire
    {
        [JsonPropertyName("attr_min")]
        public int? AttrMin { get; init; }

        [JsonPropertyName("attr_max")]
        public int? AttrMax { get; init; }

        [JsonPropertyName("extras_max")]
        public int? ExtrasMax { get; init; }
    }

    sealed record InputWire
    {
        [JsonPropertyName("item_id")]
        public string? ItemId { get; init; }

        [JsonPropertyName("count")]
        public int? Count { get; init; }
    }

    sealed record OutputWire
    {
        [JsonPropertyName("item_id")]
        public string? ItemId { get; init; }

        [JsonPropertyName("count")]
        public int? Count { get; init; }
    }
}
