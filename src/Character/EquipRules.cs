namespace Character;

/// <summary>
/// 穿着规则的唯一判定出口。两条按顺序判：
/// ① 所有装备都看等级门槛（不只武器）：require_level 到了才能穿；
/// ② 武器再看职业锁：不到 weapon_lock_level 的武器五个职业都能装备，
///    到了就只能用本职业兵器（职业可用兵器 = 该职业所有分支的本命兵器并集）。
/// 衣服、戒指这些没有武器种类的装备不受职业约束。
/// RPC（CanEquip / EquipItem）与核对入口都走这里，不留第二套判定；
/// 「同部位只能一件、脱下落在哪一格」属于落点规则，在 EquipRepository。
/// </summary>
public static class EquipRules
{
    public static string Check(ItemDef item, int level, string classId, BranchTable branches)
    {
        if (!item.IsEquipment)
        {
            return "not_equipment";
        }

        if (level < item.RequireLevel)
        {
            return "level_not_enough";
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
