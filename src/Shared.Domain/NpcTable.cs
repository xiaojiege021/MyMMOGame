using System.Text.RegularExpressions;

namespace Mmo.Shared.Domain;

/// <summary>
/// 一位 NPC：静态定义只放编号、名字、所在地表、站位、势力、职能与两条引用
/// （对话、商店）。对话正文在 config/dialogue/，商品在 config/npc_shop.json，
/// 本表不抄第二份。
/// </summary>
public sealed record NpcDef(
    string Id,
    string Name,
    string Region,
    double X,
    double Y,
    double Z,
    double Facing,
    string Faction,
    string Role,
    string DialogueId,
    string ShopId,
    string Note);

/// <summary>
/// NPC 表（K6）。世界服启动时读它做内容校验：所在地表、势力、对话编号都必须存在；
/// 九位开口的人每一位都要有一条 NPC。站位坐标是占位，B15 接场景后再替换。
/// </summary>
public sealed class NpcTable
{
    public const string WrittenSchema = "npc-table/1";

    static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    {
        "quest", "innkeeper", "merchant", "trainer", "flavor",
    };

    static readonly Regex IdPattern = new("^npc_[a-z0-9_]+$", RegexOptions.CultureInvariant);

    readonly Dictionary<string, NpcDef> _byId = new(StringComparer.Ordinal);

    public int Version { get; }

    public IReadOnlyCollection<NpcDef> All => _byId.Values;

    public NpcDef? Find(string id) => _byId.GetValueOrDefault(id);

    NpcTable(Dictionary<string, NpcDef> byId, int version)
    {
        _byId = byId;
        Version = version;
    }

    public static NpcTable Load(
        string path,
        IReadOnlyCollection<string> regionIds,
        IReadOnlyCollection<string> factionIds,
        IReadOnlyCollection<string> dialogueIds,
        IReadOnlyCollection<string>? shopIds = null)
    {
        var wire = ConfigJson.Read<Wire>(path);
        if (!string.Equals((wire.Schema ?? "").Trim(), WrittenSchema, StringComparison.Ordinal))
        {
            throw new ConfigException(path + "：schema 必须是 " + WrittenSchema);
        }
        if (wire.Version is not > 0)
        {
            throw new ConfigException(path + "：version 必须大于 0");
        }
        if (wire.Npcs is null || wire.Npcs.Count == 0)
        {
            throw new ConfigException(path + "：一件 NPC 都没有");
        }

        var regions = new HashSet<string>(regionIds, StringComparer.Ordinal);
        var factions = new HashSet<string>(factionIds, StringComparer.Ordinal);
        var dialogues = new HashSet<string>(dialogueIds, StringComparer.Ordinal);
        var shops = shopIds is null ? null : new HashSet<string>(shopIds, StringComparer.Ordinal);

        var map = new Dictionary<string, NpcDef>(StringComparer.Ordinal);
        foreach (var npc in wire.Npcs)
        {
            var id = (npc.Id ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ConfigException(path + "：有 NPC 缺 id");
            }
            if (!IdPattern.IsMatch(id))
            {
                throw new ConfigException(path + "：NPC 编号要写成 npc_<小写编号>：" + id);
            }
            if (map.ContainsKey(id))
            {
                throw new ConfigException(path + "：NPC 编号重复：" + id);
            }

            var name = (npc.Name ?? "").Trim();
            if (name.Length == 0)
            {
                throw new ConfigException(path + "：NPC " + id + " 缺 name");
            }

            var region = (npc.Region ?? "").Trim();
            if (!regions.Contains(region))
            {
                throw new ConfigException(path + "：NPC " + id + " 的 region 不是已配置地表：" + region);
            }

            var pos = npc.Pos ?? throw new ConfigException(path + "：NPC " + id + " 缺 pos");
            if (!double.IsFinite(pos.X) || !double.IsFinite(pos.Y) || !double.IsFinite(pos.Z))
            {
                throw new ConfigException(path + "：NPC " + id + " 的 pos 必须是有限数");
            }

            var facing = npc.Facing ?? 0;
            if (!double.IsFinite(facing) || facing < 0 || facing > 360)
            {
                throw new ConfigException(path + "：NPC " + id + " 的 facing 要在 0..360 之间");
            }

            var faction = (npc.Faction ?? "").Trim();
            if (faction.Length > 0 && !factions.Contains(faction))
            {
                throw new ConfigException(path + "：NPC " + id + " 的 faction 不在 world.json： " + faction);
            }

            var role = (npc.Role ?? "").Trim();
            if (!Roles.Contains(role))
            {
                throw new ConfigException(path + "：NPC " + id + " 的 role 不认识：" + role);
            }

            var dialogue = (npc.DialogueId ?? "").Trim();
            if (dialogue.Length > 0 && !dialogues.Contains(dialogue))
            {
                throw new ConfigException(path + "：NPC " + id + " 的 dialogue_id 没有对应对话：" + dialogue);
            }

            var shop = (npc.ShopId ?? "").Trim();
            if (shop.Length > 0 && shops is not null && !shops.Contains(shop))
            {
                throw new ConfigException(path + "：NPC " + id + " 的 shop_id 不在 npc_shop.json：" + shop);
            }

            map[id] = new NpcDef(
                id, name, region, pos.X, pos.Y, pos.Z, facing, faction, role, dialogue, shop,
                (npc.Note ?? "").Trim());
        }

        return new NpcTable(map, wire.Version.Value);
    }

    sealed record Wire
    {
        public string? Schema { get; init; }
        public int? Version { get; init; }
        public List<NpcItem>? Npcs { get; init; }
    }

    sealed record NpcItem
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public string? Region { get; init; }
        public Position? Pos { get; init; }
        public double? Facing { get; init; }
        public string? Faction { get; init; }
        public string? Role { get; init; }
        public string? DialogueId { get; init; }
        public string? ShopId { get; init; }
        public string? Note { get; init; }
    }

    sealed record Position
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double Z { get; init; }
    }
}
