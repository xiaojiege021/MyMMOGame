using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>一条分支：属于哪个职业，带本命兵器。分支本身不加属性。</summary>
public sealed record BranchDef(string Id, string Name, string ClassId, IReadOnlyList<string> WeaponKinds);

public sealed record BranchTable(IReadOnlyList<BranchDef> All, int Version, int WeaponLockLevel)
{
    /// <summary>分支必须属于这个职业；分支号不存在也返回 false。</summary>
    public bool BelongsTo(string branchId, string classId)
    {
        var branch = All.FirstOrDefault(b => b.Id == branchId);
        return branch != null && string.Equals(branch.ClassId, classId, StringComparison.Ordinal);
    }

    public BranchDef? Find(string id) => All.FirstOrDefault(b => b.Id == id);

    /// <summary>
    /// 15 级起只能用本职业兵器：职业可用兵器 = 该职业所有分支的本命兵器并集。
    /// 方案写的是「按职业」而不是「按分支」——兵器不改分支，所以并集才是这条规则。
    /// </summary>
    public bool ClassCanUseWeapon(string classId, string weaponKind) =>
        All.Any(b => string.Equals(b.ClassId, classId, StringComparison.Ordinal)
                     && b.WeaponKinds.Contains(weaponKind, StringComparer.Ordinal));

    /// <summary>一个职业所有分支的本命兵器，按出现顺序去重，给只读入口与日志用。</summary>
    public IReadOnlyList<string> WeaponKindsOf(string classId) =>
        All.Where(b => string.Equals(b.ClassId, classId, StringComparison.Ordinal))
            .SelectMany(b => b.WeaponKinds).Distinct(StringComparer.Ordinal).ToList();

    public static BranchTable Load(string path)
    {
        var wire = RaceTable.Read<BranchWire>(path, "branches");
        var branches = new List<BranchDef>();
        foreach (var item in wire.Branches)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.ClassId))
            {
                throw new ConfigException("branches.json 里有条目缺 id 或 class_id");
            }

            branches.Add(new BranchDef(
                item.Id.Trim(), (item.Name ?? "").Trim(), item.ClassId.Trim(),
                (item.WeaponKinds ?? []).Select(k => (k ?? "").Trim())
                    .Where(k => k.Length > 0).ToList()));
        }

        if (branches.Count == 0)
        {
            throw new ConfigException("branches.json 里一条分支都没有");
        }

        // 武器锁等级是规则，不是可选数值：没写或越界都起不来，免得静默落到某个默认值上。
        if (wire.WeaponLockLevel is not (> 0 and <= 100))
        {
            throw new ConfigException("branches.json 的 weapon_lock_level 必须是 1..100 的整数");
        }

        return new BranchTable(branches, wire.Version, wire.WeaponLockLevel.Value);
    }

    sealed record BranchWire
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("weapon_lock_level")]
        public int? WeaponLockLevel { get; init; }

        [JsonPropertyName("branches")]
        public List<BranchItem> Branches { get; init; } = [];
    }

    sealed record BranchItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("class_id")]
        public string? ClassId { get; init; }

        [JsonPropertyName("weapon_kinds")]
        public List<string>? WeaponKinds { get; init; }
    }
}
