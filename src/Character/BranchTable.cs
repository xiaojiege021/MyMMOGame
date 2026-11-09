using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>一条分支：属于哪个职业，带本命兵器。分支本身不加属性。</summary>
public sealed record BranchDef(string Id, string Name, string ClassId);

public sealed record BranchTable(IReadOnlyList<BranchDef> All, int Version)
{
    /// <summary>分支必须属于这个职业；分支号不存在也返回 false。</summary>
    public bool BelongsTo(string branchId, string classId)
    {
        var branch = All.FirstOrDefault(b => b.Id == branchId);
        return branch != null && string.Equals(branch.ClassId, classId, StringComparison.Ordinal);
    }

    public BranchDef? Find(string id) => All.FirstOrDefault(b => b.Id == id);

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

            branches.Add(new BranchDef(item.Id.Trim(), (item.Name ?? "").Trim(), item.ClassId.Trim()));
        }

        if (branches.Count == 0)
        {
            throw new ConfigException("branches.json 里一条分支都没有");
        }

        return new BranchTable(branches, wire.Version);
    }

    sealed record BranchWire
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

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
    }
}
