namespace Character;

/// <summary>掷出来的一条附加属性。种类来自 config/craft_roll.json 的 extras。</summary>
public sealed record CraftExtraValue(string Attr, int Value);

/// <summary>一件装备的掷点结果：主属性 + 最多两条附加属性（方案 9.1）。</summary>
public sealed record CraftRollResult(int Attr, IReadOnlyList<CraftExtraValue> Extras)
{
    /// <summary>落库用的 JSON：[{"attr":"attack","value":3}]。字段名与 inventory.craft_extras 对齐。</summary>
    public string ExtrasJson()
    {
        if (Extras.Count == 0)
        {
            return "[]";
        }

        var parts = new string[Extras.Count];
        for (var i = 0; i < Extras.Count; i++)
        {
            parts[i] = "{\"attr\":\"" + Extras[i].Attr + "\",\"value\":" + Extras[i].Value + "}";
        }

        return "[" + string.Join(",", parts) + "]";
    }

    /// <summary>攻击合计：主属性 + 附加属性里 attr=attack 的那几条。其余种类（将来）不计在这里。</summary>
    public int AttackTotal()
    {
        var total = Attr;
        for (var i = 0; i < Extras.Count; i++)
        {
            if (string.Equals(Extras[i].Attr, "attack", StringComparison.Ordinal))
            {
                total += Extras[i].Value;
            }
        }

        return total;
    }
}

/// <summary>
/// 制作掷点：纯函数，只有服务端调用一次（方案 9.1）。
///
/// 区间与权重都从 CraftRollTable 来，代码里没有第二个数字：
///   - 主属性在配方的 attr_min..attr_max 之间取值，按三段（低 / 中 / 高）加权，
///     熟练度档只把中段与高段的权重乘上去——不抬上限、不改品阶；
///   - 附加属性按配方的 extras_max 条数掷，种类取该部位 × 该品阶的允许表；
///   - 同一件不再掷第二次（调用点只有 FinishCraft 一处，proto 里也没有重掷入口）。
/// </summary>
public static class CraftRoller
{
    public static CraftRollResult Roll(
        RecipeDef recipe, ItemDef output, CraftRollTable table, int craftCount, Random rng)
    {
        var roll = recipe.Roll
            ?? throw new ConfigException("配方 " + recipe.Id + " 没有 grade_roll，不能掷点");
        var tier = table.TierOf(craftCount);

        var attr = PickInBand(roll.AttrMin, roll.AttrMax, tier.HighWeight, rng);

        var pool = table.ExtrasFor(output.Slot, output.Grade);
        var count = roll.ExtrasMax;
        if (count > pool.Count)
        {
            count = pool.Count;
        }

        var extras = new List<CraftExtraValue>(count);
        for (var i = 0; i < count; i++)
        {
            var def = pool[i];
            extras.Add(new CraftExtraValue(def.Attr, PickInBand(def.Min, def.Max, tier.HighWeight, rng)));
        }

        return new CraftRollResult(attr, extras);
    }

    /// <summary>
    /// 在 [min, max] 里按三段加权取一个整数。highWeight 只作用在中段与高段上：
    /// 档位越高，掷到中高值的相对概率越大，但取值永远不越出区间。
    /// 区间不足三段时退化成均匀取值（权重无从施加）。
    /// </summary>
    public static int PickInBand(int min, int max, int highWeight, Random rng)
    {
        if (max <= min)
        {
            return min;
        }

        var span = max - min + 1;
        if (span < 3)
        {
            return rng.Next(min, max + 1);
        }

        var lowEnd = min + (span / 3);
        var midEnd = min + ((span * 2) / 3);
        var low = lowEnd - min;
        var mid = midEnd - lowEnd;
        var high = max - midEnd + 1;

        var weight = Math.Max(1, highWeight);
        var total = low + (mid + high) * weight;
        var pick = rng.Next(total);

        if (pick < low)
        {
            return min + pick;
        }

        pick -= low;
        var band = weight;
        if (pick < mid * band)
        {
            return lowEnd + (pick / band);
        }

        pick -= mid * band;
        return midEnd + (pick / band);
    }
}
