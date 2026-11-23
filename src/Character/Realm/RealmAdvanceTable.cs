using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 一段的突破要求（方案 8.1）。修为就是当前境界，这里不另记一池点数；
/// Drill 是这一笔要扣掉的历练值，Stone / StoneCost 是灵石档位与数量，
/// MainStage 是炼心要查到的已经打开的主线场次，SideQuests 是这一段列出的必要支线。
/// Pill / PillCount 是 B1 的境界丹与枚数（方案 12.1「筑基丹、破镜丹用于境界突破」）：
/// 丹与灵石、历练同一笔事务，掷失败也不退。没配丹的段（B1 之前的临时表）是空串 / 0。
/// 数值都没在方案里写定，全部来自 config/realms.json，代码里不写第二套。
/// </summary>
public sealed record RealmAdvanceRequirement(
    string From,
    string Next,
    long Drill,
    string Stone,
    long StoneCost,
    int SuccessPct,
    int MainStage,
    IReadOnlyList<string> SideQuests,
    string Pill,
    long PillCount);

public sealed partial class RealmTable
{

    /// <summary>每一段（极境除外）的突破要求，顺序与 Realms 一致。</summary>
    public IReadOnlyCollection<RealmAdvanceRequirement> Requirements => _requirements.Values;

    /// <summary>这一段的突破要求；极境没有下一段，返回 null。</summary>
    public RealmAdvanceRequirement? Requirement(string? realm)
        => _requirements.GetValueOrDefault(Normalize(realm));

    /// <summary>修为到下一段这一句：下一段没开就 realm_locked，已到极境就 realm_max。</summary>
    public RealmAdvance CheckAdvance(string? current)
    {
        var from = Normalize(current);
        var rank = Rank(from);
        if (rank < 1)
        {
            return new RealmAdvance(false, "realm_unknown", from, "", false);
        }

        if (rank >= _order.Count)
        {
            return new RealmAdvance(false, "realm_max", from, "", true);
        }

        var next = _order[rank];
        var open = IsOpen(next);
        return new RealmAdvance(open, open ? "" : "realm_locked", from, next, open);
    }
    /// <summary>读 realms.json 的 advance 段，校验后返回「从这一段到下一段」的要求表。</summary>
    static Dictionary<string, RealmAdvanceRequirement> Build(
        Wire wire, List<string> order, CurrencyTable currencies)
    {
        var requirements = new Dictionary<string, RealmAdvanceRequirement>(StringComparer.Ordinal);
        foreach (var item in wire.Advance ?? new List<AdvanceItem>())
        {
            var from = (item.From ?? "").Trim();
            if (from.Length == 0)
            {
                throw new ConfigException("realms.json 的 advance 里有条目缺 from");
            }

            if (!order.Contains(from))
            {
                throw new ConfigException("realms.json 的突破要求不在顺序表里：" + from);
            }

            if (from == order[^1])
            {
                throw new ConfigException("realms.json 的 " + from + " 是最后一段，没有下一段，不该配突破要求");
            }

            if (requirements.ContainsKey(from))
            {
                throw new ConfigException("realms.json 的突破要求重复：" + from);
            }

            if (item.Drill is null || item.Drill < 0)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破要求缺 drill 或 drill 是负数");
            }

            if (item.StoneCost is null || item.StoneCost <= 0)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破要求缺 stone_cost 或不是正数");
            }

            var stone = (item.Stone ?? "").Trim();
            if (stone.Length == 0)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破要求缺 stone");
            }

            if (!stone.StartsWith("stone_", StringComparison.Ordinal))
            {
                throw new ConfigException(
                    "realms.json 的 " + from + " 突破灵石必须是灵石五档（stone_1..stone_5）：" + stone);
            }

            if (currencies.Find(stone) is null)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破灵石不在币种表里：" + stone);
            }

            if (item.SuccessPct is null || item.SuccessPct < 0 || item.SuccessPct > 100)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破成功率 success_pct 必须在 0–100");
            }

            if (item.MainStage is null || item.MainStage < 0)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破的主线场次 main_stage 必须是非负数");
            }

            var sides = new List<string>();
            foreach (var raw in item.SideQuests ?? new List<string?>())
            {
                var quest = (raw ?? "").Trim();
                if (quest.Length == 0)
                {
                    throw new ConfigException("realms.json 的 " + from + " 必要支线里有空名字");
                }

                if (sides.Contains(quest))
                {
                    throw new ConfigException("realms.json 的 " + from + " 必要支线重复：" + quest);
                }

                sides.Add(quest);
            }

            // B1：境界丹。配了丹就必须是正整数枚数；没配丹就不许留一个枚数，
            // 「有数没丹」会让日志里看着像扣了丹而实际没扣。
            var pill = (item.Pill ?? "").Trim();
            var pillCount = item.PillCount ?? 0;
            if (pill.Length == 0 && pillCount != 0)
            {
                throw new ConfigException("realms.json 的 " + from
                    + " 配了 pill_count 却没有 pill");
            }

            if (pill.Length > 0 && pillCount < 1)
            {
                throw new ConfigException("realms.json 的 " + from + " 突破丹枚数必须是正整数");
            }

            if (pill.StartsWith("stone_", StringComparison.Ordinal))
            {
                throw new ConfigException("realms.json 的 " + from + " 突破丹配成了灵石：" + pill);
            }

            var index = order.IndexOf(from);
            requirements[from] = new RealmAdvanceRequirement(
                from, order[index + 1], item.Drill.Value, stone, item.StoneCost.Value,
                item.SuccessPct.Value, item.MainStage.Value, sides, pill, pillCount);
        }

        for (var i = 0; i < order.Count - 1; i++)
        {
            if (!requirements.ContainsKey(order[i]))
            {
                throw new ConfigException("realms.json 缺 " + order[i] + " 的突破要求");
            }
        }
        return requirements;
    }
}


    sealed record AdvanceItem
    {
        [JsonPropertyName("from")]
        public string? From { get; init; }

        [JsonPropertyName("drill")]
        public long? Drill { get; init; }

        [JsonPropertyName("stone")]
        public string? Stone { get; init; }

        [JsonPropertyName("stone_cost")]
        public long? StoneCost { get; init; }

        [JsonPropertyName("success_pct")]
        public int? SuccessPct { get; init; }

        [JsonPropertyName("main_stage")]
        public int? MainStage { get; init; }

        [JsonPropertyName("side_quests")]
        public List<string?>? SideQuests { get; init; }

        // B1：境界丹与枚数。编号必须是 config/pill.json 的突破丹（由 PillTable 互校），
        // 这里只管形状与范围。
        [JsonPropertyName("pill")]
        public string? Pill { get; init; }

        [JsonPropertyName("pill_count")]
        public long? PillCount { get; init; }
    }


    sealed record SwitchItem
    {
        [JsonPropertyName("realm")]
        public string? Realm { get; init; }

        [JsonPropertyName("open")]
        public bool? Open { get; init; }
    }
