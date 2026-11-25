namespace Character;

/// <summary>
/// 穿着规则的唯一判定出口。三条按顺序判：
/// ① 所有装备都看等级门槛（不只武器）：require_level 到了才能穿；
/// ② A9：写了属性门槛（require_attr）的装备，看「职业基础 + 已加点」够不够；
/// ③ 武器再看职业锁：不到 weapon_lock_level 的武器五个职业都能装备，
///    到了就只能用本职业兵器（职业可用兵器 = 该职业所有分支的本命兵器并集）。
/// 衣服、戒指这些没有武器种类的装备不受职业约束。
/// RPC（CanEquip / EquipItem）与核对入口都走这里，不留第二套判定；
/// 「同部位只能一件、脱下落在哪一格」属于落点规则，在 EquipRepository。
/// 属性门槛读的是加点之后的值（A9 口径）：基础值来自 config/classes.json，
/// 加点是 character_attr_points 里的事实值，两截由调用方一次读齐再传进来，
/// 这里不查库、也不读第二份属性数值。
/// </summary>
public static class EquipRules
{
    public static string Check(
        ItemDef item, int level, string classId, BranchTable branches, ClassAttributes attributes)
    {
        if (!item.IsEquipment)
        {
            return "not_equipment";
        }

        if (level < item.RequireLevel)
        {
            return "level_not_enough";
        }

        // A9：属性门槛。没写 require_attr 的装备这一段直接过，与 A9 之前逐字相同。
        foreach (var requirement in item.Requirements)
        {
            if (attributes.Get(requirement.Key) < requirement.Min)
            {
                return "attr_not_enough";
            }
        }

        if (!item.IsWeapon)
        {
            return "";
        }

        if (item.RequireLevel < branches.WeaponLockLevel)
        {
            return "";
        }

        return branches.ClassCanUseWeapon(classId, item.WeaponKind) ? "" : "class_mismatch";
    }
}
