using System.Text.Json;

namespace Battle;

/// <summary>竞技场的一种模式：1v1 或 3v3。开打人数 = 每队人数 × 队数。</summary>
public sealed class ArenaModeDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int TeamSize { get; init; }
    public int Teams { get; init; }
    public int Players => TeamSize * Teams;
}

/// <summary>
/// 一套模板属性。竞技场里一个人的攻击、生命、蓝、治疗和仇恨稳定值全取自这里，
/// 不再走职业表的属性与系数——装备、强化等级、能量结晶、角色等级都进不来，
/// 所以换装不改变模板属性（方案 12 章三层战力；开发架构 P2-3a）。
/// </summary>
public sealed class ArenaTemplate
{
    public string ClassId { get; init; } = "";
    public string BranchId { get; init; } = "";
    public long Attack { get; init; }
    public long HpMax { get; init; }
    public long ManaMax { get; init; }
    public long HealPower { get; init; }
    public long ThreatStability { get; init; }
}

/// <summary>
/// 竞技场模式与统一属性模板：config/arena_match.json 是唯一正本，代码里不写第二套数值。
/// 模板按「职业 + 分支」取：先找同职业同分支，找不到退到该职业的空分支默认模板。
/// </summary>
public sealed class ArenaMatchTable
{
    readonly Dictionary<string, ArenaModeDef> _modes;
    readonly Dictionary<(string Class, string Branch), ArenaTemplate> _byKey;
    readonly Dictionary<string, ArenaTemplate> _defaults;

    ArenaMatchTable(
        int version,
        Dictionary<string, ArenaModeDef> modes,
        Dictionary<(string, string), ArenaTemplate> byKey,
        Dictionary<string, ArenaTemplate> defaults,
        bool missing = false)
    {
        Version = version;
        _modes = modes;
        _byKey = byKey;
        _defaults = defaults;
        IsMissing = missing;
    }

    /// <summary>
    /// 这张表没配。竞技场是可选的玩法：没有它，副本与练习场照常跑，
    /// 只是带 arena_mode 的连接会被拒。核心里那份表在，这里就恒为 false。
    /// </summary>
    public bool IsMissing { get; }

    /// <summary>没配竞技场表时的空表。任何 arena_mode 都查不到，连接会被拒。</summary>
    public static ArenaMatchTable Missing() =>
        new(0, [], [], [], missing: true);

    public int Version { get; }

    public IReadOnlyCollection<ArenaModeDef> Modes => _modes.Values;

    public IReadOnlyCollection<ArenaTemplate> Templates => _byKey.Values;

    public bool TryGetMode(string id, out ArenaModeDef mode) => _modes.TryGetValue(id, out mode!);

    /// <summary>先按职业与分支找，找不到退到该职业的空分支默认模板。两条都没有就是没配。</summary>
    public bool TryGetTemplate(string classId, string branchId, out ArenaTemplate template)
    {
        if (!string.IsNullOrEmpty(branchId) && _byKey.TryGetValue((classId, branchId), out template!))
        {
            return true;
        }

        return _defaults.TryGetValue(classId, out template!);
    }

    public static ArenaMatchTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"竞技场表不存在：{path}");
        }

        RejectForbiddenKeys(path);

        var file = ConfigJson.Read<ArenaFile>(path);
        return Build(path, file);
    }

    /// <summary>
    /// 模板只保留职业与分支。要是有人往模板里塞装备、等级、强化、结晶，
    /// 反序列化会静默忽略这个字段、场上照样按模板算——那样「换装不改变模板属性」
    /// 就成了看着成立、实际没人知道配置里混进了什么。所以这些键一律让进程起不来。
    /// </summary>
    static void RejectForbiddenKeys(string path)
    {
        var text = File.ReadAllText(path);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{path}：不是合法 JSON（{ex.Message}）");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("templates", out var templates) ||
                templates.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            string[] banned = ["level", "equip", "equipment", "equipment_attack", "enhance", "crystal"];
            foreach (var item in templates.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var prop in item.EnumerateObject())
                {
                    var name = prop.Name.ToLowerInvariant();
                    if (Array.Exists(banned, word => name.Contains(word, StringComparison.Ordinal)))
                    {
                        throw new ConfigException(
                            $"{path}：模板里不能出现「{prop.Name}」——模板只按职业与分支给属性，"
                            + "装备、强化、结晶和等级不参与竞技场");
                    }
                }
            }
        }
    }

    static ArenaMatchTable Build(string path, ArenaFile file)
    {
        if (file.Version <= 0)
        {
            throw new ConfigException($"{path}：version 必须是正整数");
        }

        if (file.Modes.Count == 0)
        {
            throw new ConfigException($"{path}：一个模式都没有");
        }

        var modes = new Dictionary<string, ArenaModeDef>(StringComparer.Ordinal);
        foreach (var mode in file.Modes)
        {
            if (string.IsNullOrWhiteSpace(mode.Id))
            {
                throw new ConfigException($"{path}：有模式没写 id");
            }
            if (string.IsNullOrWhiteSpace(mode.Name))
            {
                throw new ConfigException($"{path}：模式 {mode.Id} 没有名字");
            }
            if (mode.TeamSize < 1)
            {
                throw new ConfigException($"{path}：模式 {mode.Id} 的 team_size 必须至少 1");
            }
            if (mode.Teams < 2)
            {
                throw new ConfigException($"{path}：模式 {mode.Id} 的 teams 必须至少 2");
            }
            if (mode.Players < 2 || mode.Players > 100)
            {
                throw new ConfigException(
                    $"{path}：模式 {mode.Id} 的开打人数是 {mode.Players}，超出 2..100");
            }
            if (!modes.TryAdd(mode.Id, mode))
            {
                throw new ConfigException($"{path}：模式 id「{mode.Id}」重复");
            }
        }

        // 方案 12.3 写定竞技场只有 1v1 和 3v3。按人数组合验，不按 id 字符串验，
        // 免得把 id 写死成第二套数字。
        var shapes = file.Modes.Select(m => (m.TeamSize, m.Teams)).ToList();
        if (shapes.Count != 2 || !shapes.Contains((1, 2)) || !shapes.Contains((3, 2)))
        {
            throw new ConfigException(
                $"{path}：竞技场只有 1v1 和 3v3 两种模式（每队 1 人或 3 人、两队），现在配的是 "
                + string.Join("、", shapes.Select(s => $"{s.TeamSize}v{s.TeamSize}×{s.Teams}")));
        }

        if (file.Templates.Count == 0)
        {
            throw new ConfigException($"{path}：一套模板都没有");
        }

        var byKey = new Dictionary<(string, string), ArenaTemplate>();
        var defaults = new Dictionary<string, ArenaTemplate>(StringComparer.Ordinal);
        foreach (var template in file.Templates)
        {
            if (string.IsNullOrWhiteSpace(template.ClassId))
            {
                throw new ConfigException($"{path}：有模板没写 class_id");
            }
            if (template.Attack < 0 || template.HpMax <= 0 || template.ManaMax < 0 ||
                template.HealPower < 0 || template.ThreatStability < 0)
            {
                throw new ConfigException(
                    $"{path}：模板 {template.ClassId}/{template.BranchId} 的数值不合法——"
                    + "攻击、蓝、治疗、仇恨不能是负数，生命必须大于 0");
            }

            var key = (template.ClassId, template.BranchId);
            if (!byKey.TryAdd(key, template))
            {
                throw new ConfigException(
                    $"{path}：模板 {template.ClassId}/{template.BranchId} 重复");
            }

            if (template.BranchId.Length == 0 &&
                !defaults.TryAdd(template.ClassId, template))
            {
                throw new ConfigException($"{path}：职业 {template.ClassId} 的默认模板重复");
            }
        }

        // 每个有分支模板的职业，必须有一条空分支默认模板兜底；只有分支、没有默认
        // 意味着「没转职的人进竞技场就没有模板」，这是缺项，不是可选项。
        foreach (var (classId, branchId) in byKey.Keys)
        {
            if (branchId.Length == 0)
            {
                continue;
            }
            if (!defaults.ContainsKey(classId))
            {
                throw new ConfigException(
                    $"{path}：职业 {classId} 有分支模板 {branchId}，但没有空分支的默认模板");
            }
        }

        return new ArenaMatchTable(file.Version, modes, byKey, defaults);
    }

    /// <summary>
    /// 与职业表对齐：每个职业都要有一套默认模板，模板里也不许出现职业表没有的职业。
    /// 职业表加了新职业而模板没跟上，进程起不来——不在场上给一个兜底属性。
    /// </summary>
    public void ValidateAgainst(ClassTable classes)
    {
        if (IsMissing)
        {
            return;
        }

        foreach (var cls in classes.All)
        {
            if (!_defaults.ContainsKey(cls.Id))
            {
                throw new ConfigException($"竞技场表：职业 {cls.Id} 没有默认模板");
            }
        }

        foreach (var template in _byKey.Values)
        {
            if (!classes.TryGet(template.ClassId, out _))
            {
                throw new ConfigException(
                    $"竞技场表：模板里的职业 {template.ClassId} 不在职业表里");
            }
        }
    }

    sealed class ArenaFile
    {
        public int Version { get; init; }
        public List<ArenaModeDef> Modes { get; init; } = [];
        public List<ArenaTemplate> Templates { get; init; } = [];
    }
}
