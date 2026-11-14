using Npgsql;

namespace Character;

/// <summary>收藏里的一件外观：什么物品、什么种类、现在在哪些角色身上。</summary>
public sealed record WardrobeItem(
    string ItemId, string Kind, string Name, IReadOnlyList<long> CharacterIds);

/// <summary>
/// 外观收藏的账号级只读视图（方案 12.1 第 810 行「外观收藏可以共享」）。
///
/// 只读、不新建表、不改背包归属：按账号把名下所有角色背包里的**外观类**物品取并集，
/// 于是第二个角色也能看到第一个角色买到的服装 / 幻形 / 碎片，而装备、强化、等级、
/// 境界仍然各是各的（那些按 character_id 各存一行，这条口一条都不返回）。
///
/// 口径两处交代清楚：
///   · 收藏按「现在持有」算，不记永久解锁。方案没写解锁规则与上限，也没有卡承接，
///     真要做成「解锁过就一直算」，得另建一张账号级表并接到所有获取路径上，
///     那属于另一张卡（见开发日志的欠项登记）。
///   · 称号不计入收藏：方案把它和外观并列为商城上架项，不是「外观」本身；
///     洞府摆件同理（那是家园展示，不是穿在身上的样子）。
/// </summary>
public sealed class WardrobeRepository(NpgsqlDataSource dataSource, ItemTable items)
{
    // 方案 12.1 里算「外观」的几种：服装、武器幻形、境界外观、飞行拖尾、坐骑外观、
    // 外观碎片（通行证免费轨发的就是碎片）。这是一条玩法口径，不是可以调的数值。
    public static readonly string[] AppearanceKinds =
    [
        "outfit", "weapon_skin", "realm_skin", "flight_trail", "mount_skin",
        "appearance_fragment",
    ];

    public static bool IsAppearanceKind(string kind) =>
        Array.IndexOf(AppearanceKinds, kind) >= 0;

    public async Task<(bool Found, IReadOnlyList<WardrobeItem> Items)> DescribeAsync(
        string account, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);

        var found = false;
        await using (var read = new NpgsqlCommand(
            "select 1 from \"角色\".account where account = $1", connection))
        {
            read.Parameters.AddWithValue(account);
            found = await read.ExecuteScalarAsync(stopping) is not null;
        }

        if (!found)
        {
            return (false, []);
        }

        // 一件一行（角色 + 物品），在内存里按物品归并：并集就是「收藏共享」的落法。
        var byItem = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        await using (var read = new NpgsqlCommand(
            "select c.id, i.item_id from \"角色\".character c "
            + "join \"角色\".inventory i on i.character_id = c.id "
            + "where c.account = $1 order by i.item_id, c.id",
            connection))
        {
            read.Parameters.AddWithValue(account);
            await using var reader = await read.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                var characterId = reader.GetInt64(0);
                var itemId = reader.GetString(1);
                var goods = items.Find(itemId);
                if (goods is null || !IsAppearanceKind(goods.Kind))
                {
                    continue;
                }

                if (!byItem.TryGetValue(itemId, out var owners))
                {
                    owners = [];
                    byItem[itemId] = owners;
                }

                if (!owners.Contains(characterId))
                {
                    owners.Add(characterId);
                }
            }
        }

        var list = new List<WardrobeItem>(byItem.Count);
        foreach (var pair in byItem.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var goods = items.Find(pair.Key)!;
            list.Add(new WardrobeItem(pair.Key, goods.Kind, goods.Name, pair.Value));
        }

        return (true, list);
    }
}
