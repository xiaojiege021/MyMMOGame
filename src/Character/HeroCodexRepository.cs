using Npgsql;

namespace Character;

public sealed record HeroCodexEntrySnapshot(
    string Id,
    string Name,
    int UnlockMainStage,
    bool Collected,
    int PassivePctBps);

public sealed record HeroCodexSnapshot(
    bool Found,
    int MainStage,
    int CollectedCount,
    int EntryCount,
    int CollectionPctBps,
    int TotalPassivePctBps,
    string PassiveKind,
    IReadOnlyList<HeroCodexEntrySnapshot> Entries);

/// <summary>N12：收集度只看 main_stage 与配置解锁场次；被动只在服务端加总。</summary>
public sealed class HeroCodexRepository(NpgsqlDataSource dataSource, HeroCodexTable table)
{
    public HeroCodexSnapshot DescribeFromMainStage(int mainStage)
    {
        var collected = 0;
        var totalPassive = 0;
        var rows = new List<HeroCodexEntrySnapshot>(table.Entries.Count);
        foreach (var entry in table.Entries)
        {
            var isCollected = mainStage >= entry.UnlockMainStage;
            var passive = isCollected ? entry.PassivePctBps : 0;
            if (isCollected)
            {
                collected++;
                totalPassive += passive;
            }

            rows.Add(new HeroCodexEntrySnapshot(
                entry.Id, entry.Name, entry.UnlockMainStage, isCollected, passive));
        }

        if (totalPassive > table.MaxTotalPassivePctBps)
        {
            totalPassive = table.MaxTotalPassivePctBps;
        }

        var collectionPct = table.Entries.Count == 0
            ? 0
            : collected * 10000 / table.Entries.Count;

        return new HeroCodexSnapshot(
            true, mainStage, collected, table.Entries.Count, collectionPct,
            totalPassive, table.PassiveKind, rows);
    }

    public async Task<HeroCodexSnapshot> DescribeAsync(
        long characterId,
        string? account,
        CancellationToken stopping)
    {
        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1"
            : "where id = $1 and account = $2";
        await using var command = dataSource.CreateCommand(
            "select coalesce(main_stage, 0) from \"角色\".character " + owned);
        command.Parameters.AddWithValue(characterId);
        if (!string.IsNullOrEmpty(account))
        {
            command.Parameters.AddWithValue(account);
        }

        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return new HeroCodexSnapshot(
                false, 0, 0, table.Entries.Count, 0, 0, table.PassiveKind, []);
        }

        return DescribeFromMainStage(reader.GetInt32(0));
    }
}