namespace World;

public sealed record FlightDecision(
    bool Ok, string Reason, string Realm, string RequiredRealm,
    bool Flying, bool CanFly, float Y);

public sealed record RegionDecision(
    bool Ok, string Reason, string Realm, string RequiredRealm, LineState? Line);

/// <summary>
/// 飞行与跨区的唯一判定点。境界只在角色服，世界服每次判定都先问一次；
/// 不问就没有权限，不缓存一份可以过期的「有权限」结论给自己用。
/// 不通过就把人留在地面 / 原来那块地表，不在客户端上改权限，也不改角色库。
/// 相连关系、区域是否开放沿用 P1-3a1 的 WorldCatalog，本卡只加境界门槛。
/// </summary>
public sealed class FlightGate(
    FlightTable table, IRealmReader realms, LineRegistry registry, WorldCatalog catalog)
{
    public async Task<FlightDecision> FlyAsync(
        string account, ulong characterId, bool wantFly, CancellationToken stopping)
    {
        if (registry.StateOf(account, characterId) is null)
        {
            return new FlightDecision(false, "not_in_line", "", table.FlightRealm, false, false, 0);
        }

        var realm = await realms.ReadAsync(account, characterId, stopping);
        if (!realm.Ok)
        {
            // 角色服没起来、归属不对或境界读不出来：这次飞请求不成立，
            // 顺手把这个人留在地面，不回一个假的成功。
            registry.SetFlight(account, characterId, false, false, DateTimeOffset.UtcNow);
            return new FlightDecision(false, realm.Reason, "", table.FlightRealm, false, false, GroundY);
        }

        var index = table.IndexOf(realm.Realm);
        if (index < 0)
        {
            registry.SetFlight(account, characterId, false, false, DateTimeOffset.UtcNow);
            return new FlightDecision(false, "unknown_realm", realm.Realm, table.FlightRealm, false, false, GroundY);
        }

        var canFly = index >= table.IndexOf(table.FlightRealm);
        var allowed = !wantFly || canFly;
        var change = registry.SetFlight(
            account, characterId, allowed && wantFly, canFly, DateTimeOffset.UtcNow);
        if (!change.Found)
        {
            return new FlightDecision(false, "not_in_line", realm.Realm, table.FlightRealm, false, false, GroundY);
        }

        return new FlightDecision(
            allowed,
            allowed ? "" : "flight_forbidden",
            realm.Realm, table.FlightRealm,
            change.Flying, change.CanFly,
            change.State?.Y ?? GroundY);
    }

    public async Task<RegionDecision> CrossAsync(
        string account, ulong characterId, string targetRegion, CancellationToken stopping)
    {
        var current = registry.LineStateOf(account, characterId);
        if (current is null)
        {
            return Reject("not_in_line", "", "");
        }

        var target = catalog.Find(targetRegion);
        if (target is null)
        {
            return Reject("unknown_region", "", "", current);
        }

        if (string.Equals(current.RegionId, targetRegion, StringComparison.Ordinal))
        {
            return new RegionDecision(true, "same_region", "", "", current);
        }

        // 跨区只能走到相连的地表；相连关系由 P1-3a1 的配置校验过对称。
        if (!target.Neighbors.Contains(current.RegionId, StringComparer.Ordinal))
        {
            return Reject("not_adjacent", "", "", current);
        }

        // 旧天城区与焦原的「第 11 场才开」仍是 P1-3a1 那条，本卡不替剧情开门。
        if (!target.Open)
        {
            return Reject("region_locked", "", "", current);
        }

        var realm = await realms.ReadAsync(account, characterId, stopping);
        if (!realm.Ok)
        {
            return Reject(realm.Reason, "", "", current);
        }

        var required = table.RequiredFor(targetRegion);
        if (required is null)
        {
            return Reject("region_realm_missing", realm.Realm, "", current);
        }

        var index = table.IndexOf(realm.Realm);
        if (index < 0)
        {
            return Reject("unknown_realm", realm.Realm, required, current);
        }

        if (index < table.IndexOf(required))
        {
            return Reject("region_realm_locked", realm.Realm, required, current);
        }

        // 够门槛才真正换分线。Enter 会按新区域重建 Member，飞行状态落成地面。
        var displayName = registry.StateOf(account, characterId)?.DisplayName ?? "";
        var entered = registry.Enter(account, characterId, targetRegion, displayName);
        if (!entered.Entered)
        {
            return Reject(entered.Reason, realm.Realm, required, current);
        }

        return new RegionDecision(
            true, "", realm.Realm, required,
            new LineState(entered.LineId, entered.RegionId, entered.LineNo));
    }

    float GroundY => registry.GroundY;

    static RegionDecision Reject(
        string reason, string realm, string required, LineState? line = null) =>
        new(false, reason, realm, required, line);
}
