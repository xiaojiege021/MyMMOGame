using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// B6 觉醒正本（方案 8.2「满级后开放觉醒」；14.5「不抬等级上限、不另开一套战斗规则」）。
/// 本表只回答两件事：门槛是多少级、哪条分支解锁哪一条技能。
///   · min_level 必须等于 exp.json 的 level_cap —— 满级后开放，不是满级前；
///   · branch_skills 必须正好覆盖 branches.json 的每一条分支，一条分支一条技能；
///   · 技能本体与全部数值只在 config/skills.json 的 awaken=true 那一组，本表不抄第二份；
///   · gold_cost 方案没给数字，本版只允许 0（觉醒免费）。要收费得另开卡把扣款
///     接进觉醒事务（走 GrantRepository 的唯一写法），不许在这里填个正数就完事。
/// 角色服不读技能表（第 7 节：技能表由战斗服读），所以这里只校验编号形状与唯一性；
/// 「这条编号真的是本分支的觉醒技」由战斗服启动期按 skills.json 交叉校验。
/// </summary>
public sealed class AwakenTable
{
    public const string WrittenSchema = "awaken-table/1";

    /// <summary>一条分支的觉醒技。class_id 从分支表现取，本表不另存职业。</summary>
    public sealed record BranchSkill(string BranchId, string ClassId, uint SkillId);

    public int Version { get; }
    public bool Pending { get; }
    public int MinLevel { get; }
    public long GoldCost { get; }
    public IReadOnlyList<BranchSkill> BranchSkills { get; }

    AwakenTable(int version, bool pending, int minLevel, long goldCost,
        IReadOnlyList<BranchSkill> branchSkills)
    {
        Version = version;
        Pending = pending;
        MinLevel = minLevel;
        GoldCost = goldCost;
        BranchSkills = branchSkills;
    }

    /// <summary>这条分支的觉醒技；没觉醒过或分支不在表里回 null。</summary>
    public BranchSkill? ForBranch(string branchId)
    {
        foreach (var row in BranchSkills)
        {
            if (string.Equals(row.BranchId, branchId, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>这条技能编号是不是某条分支的觉醒技（请求里带进来的编号只按本表验）。</summary>
    public bool HasSkill(uint skillId)
    {
        foreach (var row in BranchSkills)
        {
            if (row.SkillId == skillId)
            {
                return true;
            }
        }

        return false;
    }

    public static AwakenTable Load(string path, BranchTable branches, ExpTable exp)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到觉醒表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("awaken.json 是空的");
        }
        catch (JsonException ex)
        {
            throw new ConfigException("awaken.json 不是合法 JSON：" + ex.Message);
        }

        if ((wire.Schema ?? "").Trim() != WrittenSchema)
        {
            throw new ConfigException("awaken.json schema 必须是 " + WrittenSchema);
        }

        if (wire.Version <= 0)
        {
            throw new ConfigException("awaken.json 的 version 必须大于 0");
        }

        var pending = wire.Pending ?? false;
        var note = (wire.Note ?? "").Trim();
        if (pending && !note.Contains("待定", StringComparison.Ordinal))
        {
            // 与 stall.json / mount.json 同一条口径：标了待定就必须在 note 里写清哪一项待定，
            // 不许用 pending 掩盖已经写死的数值。
            throw new ConfigException("awaken.json 标了 pending，note 必须写明哪一项待定");
        }

        var minLevel = wire.MinLevel ?? 0;
        if (minLevel != exp.LevelCap)
        {
            // 方案写定「满级后开放」：低于满级就是提前开，高于满级永远达不到。
            throw new ConfigException(
                "awaken.json 的 min_level 必须等于 exp.json 的 level_cap（"
                + exp.LevelCap + "，现在是 " + minLevel + "）");
        }

        var goldCost = wire.GoldCost ?? -1;
        if (goldCost != 0)
        {
            // 方案没写觉醒要花钱，本版觉醒事务里也没有扣款路径。填个正数只会让
            // 玩家白觉醒一次，所以加载期就挡住：要收费得先把扣款写进事务并另开卡。
            throw new ConfigException(
                "awaken.json 的 gold_cost 只能是 0（本版觉醒免费；要收费另开卡接扣款）");
        }

        if (wire.BranchSkills is null || wire.BranchSkills.Count == 0)
        {
            throw new ConfigException("awaken.json 一条分支都没写");
        }

        if (wire.BranchSkills.Count != branches.All.Count)
        {
            throw new ConfigException(
                "awaken.json 必须覆盖全部 " + branches.All.Count + " 条分支（现在是 "
                + wire.BranchSkills.Count + " 条）");
        }

        var rows = new List<BranchSkill>(wire.BranchSkills.Count);
        var seenBranches = new HashSet<string>(StringComparer.Ordinal);
        var seenSkills = new HashSet<uint>();
        foreach (var row in wire.BranchSkills)
        {
            var branchId = (row.BranchId ?? "").Trim();
            if (branchId.Length == 0)
            {
                throw new ConfigException("awaken.json 有一条缺 branch_id");
            }

            if (!seenBranches.Add(branchId))
            {
                throw new ConfigException("awaken.json 的 branch_id 重复：" + branchId);
            }

            var branch = branches.Find(branchId)
                ?? throw new ConfigException("awaken.json 的分支「" + branchId + "」不在 branches.json 里");

            var skillId = row.SkillId ?? 0;
            if (skillId == 0)
            {
                throw new ConfigException("awaken.json 的分支 " + branchId + " 缺 skill_id");
            }

            if (!seenSkills.Add(skillId))
            {
                throw new ConfigException("awaken.json 的 skill_id 重复：" + skillId);
            }

            rows.Add(new BranchSkill(branchId, branch.ClassId, skillId));
        }

        // 反过来查一遍：某条分支没写就是漏，不能只靠条数相等。
        foreach (var branch in branches.All)
        {
            if (!seenBranches.Contains(branch.Id))
            {
                throw new ConfigException("awaken.json 漏了分支：" + branch.Id);
            }
        }

        return new AwakenTable(wire.Version, pending, minLevel, goldCost, rows);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("pending")]
        public bool? Pending { get; init; }

        [JsonPropertyName("note")]
        public string? Note { get; init; }

        [JsonPropertyName("min_level")]
        public int? MinLevel { get; init; }

        [JsonPropertyName("gold_cost")]
        public long? GoldCost { get; init; }

        [JsonPropertyName("branch_skills")]
        public List<BranchSkillWire>? BranchSkills { get; init; }
    }

    sealed class BranchSkillWire
    {
        [JsonPropertyName("branch_id")]
        public string? BranchId { get; init; }

        [JsonPropertyName("skill_id")]
        public uint? SkillId { get; init; }
    }
}
