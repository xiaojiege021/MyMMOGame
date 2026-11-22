using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>一档套装效果的生效门槛与加成。只加基础属性（现在只有 attack）。</summary>
public sealed record SetBonusDef(int Pieces, long Attack);

/// <summary>一套套装：编号、名字、品阶，加上按件数递增的加成。</summary>
public sealed record SetDef(string Id, string Name, string Grade, IReadOnlyList<SetBonusDef> Bonuses)
{
    /// <summary>已穿几件时这一套给多少攻击：取该件数能达到的最高一档，达不到最低门槛就是 0。</summary>
    public long BonusFor(int wornPieces)
    {
        var best = 0L;
        foreach (var bonus in Bonuses)
        {
            if (wornPieces >= bonus.Pieces && bonus.Attack > best)
            {
                best = bonus.Attack;
            }
        }

        return best;
    }
}

/// <summary>
/// config/sets.json —— 套装的唯一正本（B2）。
/// 方案 `#s8` 写定品阶「换装不继承，自然换代」，核对表默认值表写定「每档 3 套，套装只加基础属性」。
/// 所以这里只记「穿到几件加多少攻击」，不记暴击 / 技能 / 冷却，也不记解锁历史：
/// 效果永远是「现在穿着的件数」算出来的，脱下立刻回落。
///
/// 加载期与物品表交叉对账：套内件数取自 items.json 里同 set_id 的装备，
/// 加成门槛不得超过实际件数，同套不得混品阶。代码里没有第二份件数或数值。
/// </summary>
public sealed class SetTable
{
    public const string WrittenSchema = "set-bonus/1";

    readonly Dictionary<string, SetDef> _byId = new(StringComparer.Ordinal);

    public int Version { get; }

    public IReadOnlyCollection<SetDef> All => _byId.Values;

    SetTable(Dictionary<string, SetDef> byId, int version)
    {
        _byId = byId;
        Version = version;
    }

    public SetDef? Find(string id) => string.IsNullOrEmpty(id) ? null : _byId.GetValueOrDefault(id);

    /// <summary>
    /// 身上这套装备给多少攻击。wornBySet 是「套装编号 → 已穿件数」，
    /// 由调用方按已穿装备现数，本表只查门槛做加法。
    /// </summary>
    public long BonusFor(IReadOnlyDictionary<string, int> wornBySet)
    {
        var total = 0L;
        foreach (var (setId, pieces) in wornBySet)
        {
            if (pieces > 0 && _byId.TryGetValue(setId, out var def))
            {
                total += def.BonusFor(pieces);
            }
        }

        return total;
    }

    public static SetTable Load(string path, ItemTable items, GradeTable grades)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到套装表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), Options)
                ?? throw new ConfigException("套装表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("套装表不是合法 JSON：" + ex.Message);
        }

        if (wire.Schema != WrittenSchema)
        {
            throw new ConfigException(path + "：schema 必须是 " + WrittenSchema);
        }

        if (wire.Sets is null || wire.Sets.Count == 0)
        {
            throw new ConfigException(path + "：sets 不能是空的");
        }

        // 套内件数从物品表现数，不在这份表里再记一遍。
        var piecesOf = new Dictionary<string, List<ItemDef>>(StringComparer.Ordinal);
        foreach (var item in items.All)
        {
            if (item.SetId.Length == 0)
            {
                continue;
            }

            if (!piecesOf.TryGetValue(item.SetId, out var list))
            {
                list = [];
                piecesOf[item.SetId] = list;
            }

            list.Add(item);
        }

        var byId = new Dictionary<string, SetDef>(StringComparer.Ordinal);
        foreach (var raw in wire.Sets)
        {
            var id = (raw.Id ?? "").Trim();
            if (!ItemTable.IsSetId(id))
            {
                throw new ConfigException(path + "：套装编号要写成 set_<小写编号>：" + id);
            }

            if (byId.ContainsKey(id))
            {
                throw new ConfigException(path + "：套装重复 " + id);
            }

            var name = (raw.Name ?? "").Trim();
            if (name.Length == 0)
            {
                throw new ConfigException(path + "：套装 " + id + " 缺 name");
            }

            var grade = (raw.Grade ?? "").Trim();
            if (grades.Find(grade) is null)
            {
                throw new ConfigException(path + "：套装 " + id + " 的品阶不认识：" + grade);
            }

            if (raw.Bonuses is null || raw.Bonuses.Count == 0)
            {
                throw new ConfigException(path + "：套装 " + id + " 没有 bonuses");
            }

            var bonuses = new List<SetBonusDef>();
            foreach (var rawBonus in raw.Bonuses)
            {
                var needPieces = rawBonus.Pieces ?? 0;
                var attack = rawBonus.Attack ?? 0;
                if (needPieces < 1)
                {
                    throw new ConfigException(path + "：套装 " + id + " 的 pieces 必须大于 0");
                }

                if (attack < 1)
                {
                    throw new ConfigException(path + "：套装 " + id + " 的 attack 必须大于 0");
                }

                if (bonuses.Count > 0 && needPieces <= bonuses[^1].Pieces)
                {
                    throw new ConfigException(path + "：套装 " + id + " 的 bonuses 必须按件数严格递增");
                }

                bonuses.Add(new SetBonusDef(needPieces, attack));
            }

            var pieces = piecesOf.GetValueOrDefault(id) ?? [];
            if (pieces.Count == 0)
            {
                throw new ConfigException(path + "：套装 " + id + " 在 items.json 里一件装备都没有");
            }

            var maxPieces = bonuses[^1].Pieces;
            if (maxPieces > pieces.Count)
            {
                throw new ConfigException(
                    path + "：套装 " + id + " 要求 " + maxPieces + " 件，但 items.json 里只有 " + pieces.Count + " 件");
            }

            foreach (var piece in pieces)
            {
                if (!string.Equals(piece.Grade, grade, StringComparison.Ordinal))
                {
                    throw new ConfigException(
                        path + "：套装 " + id + " 混了品阶（" + piece.Id + " 是 " + piece.Grade + "，套装是 " + grade + "）");
                }
            }

            byId[id] = new SetDef(id, name, grade, bonuses);
        }

        // 反向对账：物品写了 set_id，就必须在套表里——否则这件装备永远凑不齐、也不报错。
        foreach (var setId in piecesOf.Keys)
        {
            if (!byId.ContainsKey(setId))
            {
                throw new ConfigException(path + "：items.json 里的 " + setId + " 没有对应的套装定义");
            }
        }

        return new SetTable(byId, wire.Version ?? 0);
    }

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")] public string? Schema { get; set; }
        [JsonPropertyName("version")] public int? Version { get; set; }
        [JsonPropertyName("sets")] public List<WireSet>? Sets { get; set; }
    }

    sealed class WireSet
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("grade")] public string? Grade { get; set; }
        [JsonPropertyName("bonuses")] public List<WireBonus>? Bonuses { get; set; }
    }

    sealed class WireBonus
    {
        [JsonPropertyName("pieces")] public int? Pieces { get; set; }
        [JsonPropertyName("attack")] public long? Attack { get; set; }
    }
}
