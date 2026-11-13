namespace Character;

/// <summary>
/// P2-4a：武器锁的唯一判定出口。不到 weapon_lock_level 的武器五个职业都能装备；
/// 到了这个等级就只能用本职业兵器（职业可用兵器 = 该职业所有分支的本命兵器并集）。
/// 衣服、戒指这些没有武器种类的装备不受约束。RPC 与核对入口都走这里，不留第二套判定。
/// </summary>
public static class EquipRules
{
    public static string Check(ItemDef item, int level, string classId, BranchTable branches)
    {
        if (!item.IsWeapon)
        {
            return "";
        }

        if (level < item.RequireLevel)
        {
            return "level_not_enough";
        }

        if (item.RequireLevel < branches.WeaponLockLevel)
        {
            return "";
        }

        return branches.ClassCanUseWeapon(classId, item.WeaponKind) ? "" : "class_mismatch";
    }
}
