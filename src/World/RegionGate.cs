namespace World;

/// <summary>一块地表这一次能不能进：通过时 Reason 为空串，否则是给客户端的拒绝原因。</summary>
public sealed record RegionAccess(bool Ok, string Reason, int MainStage, string Alignment, int UnlockStage);

/// <summary>
/// 地表开放的唯一判定点（N1）。
///
/// 三种地表分开判：
///   1. 静态开放（config/region/*.json 的 open=true）：一直能进，连角色服都不问一句。
///   2. 剧情解锁（open=false 且 config/region_unlock.json 有它）：问角色服主线走到第几场、
///      第 10 场选的是立仙还是立魔；交完要求的那一场、并且走的是这一路，才放人进。
///   3. 只锁着（open=false 且没有解锁条目 / 缺整份解锁表）：维持 P1-3a1 的老口径 region_locked。
///
/// 世界服不存剧情进度、不改角色库、不替客户端猜自己是哪一路；判定需要的两个事实值
/// 都由角色服给。老卡的临时配置目录里没有解锁表，于是全部走第 3 种，老断言一个字不动。
/// </summary>
public sealed class RegionGate(RegionUnlockTable? unlocks, IProgressReader progress)
{
    /// <summary>这块地表要不要问角色服进度。静态开放的地表不问，省一次跨进程调用。</summary>
    public bool NeedsProgress(RegionDef region) =>
        !region.Open && unlocks?.Find(region.Id) is not null;

    /// <summary>
    /// 纯判定：进度与路线都是入参，不连任何进程。只读入口与自检用它，
    /// 运行期由 CheckAsync 取完事实值再调它——判定逻辑只有这一份。
    /// </summary>
    public RegionAccess Evaluate(RegionDef region, int mainStage, string alignment)
    {
        if (region.Open)
        {
            return new RegionAccess(true, "", mainStage, alignment, 0);
        }

        var def = unlocks?.Find(region.Id);
        if (def is null)
        {
            return new RegionAccess(false, "region_locked", mainStage, alignment, 0);
        }

        // 方案 2.8：两项都空着时两块都不开。主线没交到第 10 场的人也没有路线，
        // 这里按「剧情没到」拒，与「路线走错」分开报，方便看出是差哪一步。
        if (mainStage < def.UnlockStage || alignment.Length == 0)
        {
            return new RegionAccess(false, "region_locked", mainStage, alignment, def.UnlockStage);
        }

        // 方案 2.5：立仙走旧天城区，立魔走焦原。路线不匹配时那块地表对他就是关着的，
        // 不是「还没开」，所以另给一个原因，不让人以为再交几场就会开。
        if (!string.Equals(alignment, def.Alignment, StringComparison.Ordinal))
        {
            return new RegionAccess(false, "region_route_mismatch", mainStage, alignment, def.UnlockStage);
        }

        return new RegionAccess(true, "", mainStage, alignment, def.UnlockStage);
    }

    /// <summary>
    /// 运行期判定。只有「静态不开放且配了解锁条目」的地表才去问角色服；
    /// 问不到（角色服没起来、归属不对、读不出进度）一律按没解锁拒。
    /// </summary>
    public async Task<RegionAccess> CheckAsync(
        string account, ulong characterId, RegionDef region, CancellationToken stopping)
    {
        if (region.Open)
        {
            return new RegionAccess(true, "", 0, "", 0);
        }

        if (unlocks?.Find(region.Id) is null)
        {
            return new RegionAccess(false, "region_locked", 0, "", 0);
        }

        var progressResult = await progress.ReadAsync(account, characterId, stopping);
        if (!progressResult.Ok)
        {
            return new RegionAccess(
                false, progressResult.Reason, 0, "", unlocks.Find(region.Id)!.UnlockStage);
        }

        return Evaluate(region, progressResult.MainStage, progressResult.Alignment);
    }
}
