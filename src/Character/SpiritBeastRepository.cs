using Npgsql;

namespace Character;

/// <summary>灵兽表里的一只：编号、名字、档位、有没有收集、给多少生活协助。</summary>
public sealed record SpiritBeastRow(
    string ItemId, string Name, string Tier, bool Collected, int LifeAssistPctBps);

/// <summary>灵兽收集度与生活协助合计。不进伤害公式，只给屏与核对用。</summary>
public sealed record SpiritBeastSnapshot(
    bool Found,
    int CollectedCount,
    int EntryCount,
    int CollectionPctBps,
    int TotalLifeAssistPctBps,
    string PassiveKind,
    IReadOnlyList<SpiritBeastRow> Entries);

/// <summary>
/// B4 灵兽的只读视图（方案 9.4：只做收集、外观和生活协助，不进入伤害公式）。
///
/// 不建收集表：收集口径与外观收藏（P3-4b）一致——背包里现在持有就算收集。
/// 于是「卖出 / 丢掉」之后收集度会掉，这是本卡写定的口径，不是缺陷；
/// 做成「解锁过就永久算」需要一张账号级表并接到所有获取路径上，那是另一张卡。
///
/// 生活协助的合计与上限都在配置侧校验过（config/spirit_beast.json +
/// hero_codex.json 的那一个 max_total_passive_pct_bps），这里只按持有情况求和。
/// </summary>
public sealed class SpiritBeastRepository(
    NpgsqlDataSource dataSource, ItemTable items, SpiritBeastTable table)
{
    public async Task<SpiritBeastSnapshot> DescribeAsync(
        long characterId,
        string? account,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);

        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1"
            : "where id = $1 and account = $2";
        await using (var command = new NpgsqlCommand(
            "select 1 from \"角色\".character " + owned, connection))
        {
            command.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                command.Parameters.AddWithValue(account);
            }

            if (await command.ExecuteScalarAsync(stopping) is null)
            {
                return new SpiritBeastSnapshot(
                    false, 0, table.Entries.Count, 0, 0, table.PassiveKind, []);
            }
        }

        var held = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            "select item_id from \"角色\".inventory where character_id = $1", connection))
        {
            command.Parameters.AddWithValue(characterId);
            await using var reader = await command.ExecuteReaderAsync(stopping);
            while (await reader.ReadAsync(stopping))
            {
                held.Add(reader.GetString(0));
            }
        }

        var rows = new List<SpiritBeastRow>(table.Entries.Count);
        var collected = 0;
        var total = 0;
        foreach (var entry in table.Entries)
        {
            var isCollected = held.Contains(entry.ItemId);
            var passive = isCollected ? entry.LifeAssistPctBps : 0;
            if (isCollected)
            {
                collected++;
                total += passive;
            }

            var name = items.Find(entry.ItemId)?.Name ?? entry.Name;
            rows.Add(new SpiritBeastRow(entry.ItemId, name, entry.Tier, isCollected, passive));
        }

        var pct = table.Entries.Count == 0 ? 0 : collected * 10000 / table.Entries.Count;
        return new SpiritBeastSnapshot(
            true, collected, table.Entries.Count, pct, total, table.PassiveKind, rows);
    }
}
