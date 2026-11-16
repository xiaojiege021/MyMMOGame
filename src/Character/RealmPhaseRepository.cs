using Npgsql;

namespace Character;

public sealed record RealmPhasePromoteOutcome(
    bool Found,
    string Reason,
    bool Executed,
    bool Duplicate,
    int FromPhase,
    int ToPhase,
    string PhaseName,
    int PassivePctBps,
    int Level,
    int LevelRequired);

public sealed record RealmPassiveSnapshot(
    bool Found,
    string Realm,
    int Phase,
    string PhaseName,
    int PassivePctBps);

/// <summary>
/// 段内阶的读写（N10，R1 起按境界分）。升阶与被动百分比都只在角色服、只读配置；
/// 突破下一大境界仍走 RealmAdvanceRepository，但须到该境界配置的
/// breakthrough_min_index 及以后（炼体第 9 阶、其余后期）。
/// </summary>
public sealed class RealmPhaseRepository(NpgsqlDataSource dataSource, RealmTable realms)
{
    public const int MaxTxnLength = 128;

    public async Task<RealmPassiveSnapshot> DescribePassiveAsync(
        long characterId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select realm, realm_phase from \"角色\".character where id = $1");
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return new RealmPassiveSnapshot(false, "", 0, "", 0);
        }

        var realm = reader.GetString(0);
        var phase = reader.GetInt16(1);
        var sub = realms.SubPhasesFor(realm);
        return new RealmPassiveSnapshot(
            true, realm, phase, sub.NameAt(phase), sub.PassiveBpsAt(phase));
    }

    public async Task<RealmPhasePromoteOutcome> PromoteAsync(
        long characterId,
        string? account,
        string txnId,
        CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        var owned = string.IsNullOrEmpty(account)
            ? "where id = $1 for update"
            : "where id = $1 and account = $2 for update";
        var found = false;
        var realm = "";
        var phase = 0;
        var level = 0;
        await using (var read = new NpgsqlCommand(
            "select realm, realm_phase, level from \"角色\".character " + owned,
            connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            if (!string.IsNullOrEmpty(account))
            {
                read.Parameters.AddWithValue(account);
            }

            await using var reader = await read.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                found = true;
                realm = reader.GetString(0);
                phase = reader.GetInt16(1);
                level = reader.GetInt32(2);
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new RealmPhasePromoteOutcome(
                false, "character_not_found", false, false, 0, 0, "", 0, 0, 0);
        }

        // R1：段内阶按当前境界取——炼体 9 阶、筑基及以后 4 阶。
        var sub = realms.SubPhasesFor(realm);

        await using (var dup = new NpgsqlCommand(
            "select from_phase, to_phase from \"角色\".realm_phase_promote where txn_id = $1",
            connection, transaction))
        {
            dup.Parameters.AddWithValue(txnId);
            await using var reader = await dup.ExecuteReaderAsync(stopping);
            if (await reader.ReadAsync(stopping))
            {
                var from = reader.GetInt16(0);
                var to = reader.GetInt16(1);
                await reader.DisposeAsync();
                await transaction.RollbackAsync(stopping);
                return new RealmPhasePromoteOutcome(
                    true, "", false, true, from, to, sub.NameAt(to), sub.PassiveBpsAt(to),
                    level, 0);
            }
        }

        if (phase >= sub.Count - 1)
        {
            await transaction.RollbackAsync(stopping);
            return new RealmPhasePromoteOutcome(
                true, "realm_phase_max", false, false, phase, phase,
                sub.NameAt(phase), sub.PassiveBpsAt(phase), level, 0);
        }

        var next = phase + 1;
        var requiredLevel = sub.PromoteLevelAt(next);
        if (level < requiredLevel)
        {
            await transaction.RollbackAsync(stopping);
            return new RealmPhasePromoteOutcome(
                true, "level_not_enough", false, false, phase, phase,
                sub.NameAt(phase), sub.PassiveBpsAt(phase), level, requiredLevel);
        }

        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".realm_phase_promote "
            + "(txn_id, character_id, from_phase, to_phase) values ($1, $2, $3, $4) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(txnId);
            claim.Parameters.AddWithValue(characterId);
            claim.Parameters.AddWithValue((short)phase);
            claim.Parameters.AddWithValue((short)next);
            if (await claim.ExecuteScalarAsync(stopping) is null)
            {
                await transaction.RollbackAsync(stopping);
                return new RealmPhasePromoteOutcome(
                    true, "", false, true, phase, phase,
                    sub.NameAt(phase), sub.PassiveBpsAt(phase), level, requiredLevel);
            }
        }

        await using (var write = new NpgsqlCommand(
            "update \"角色\".character set realm_phase = $2 where id = $1",
            connection, transaction))
        {
            write.Parameters.AddWithValue(characterId);
            write.Parameters.AddWithValue((short)next);
            await write.ExecuteNonQueryAsync(stopping);
        }

        await transaction.CommitAsync(stopping);
        return new RealmPhasePromoteOutcome(
            true, "", true, false, phase, next,
            sub.NameAt(next), sub.PassiveBpsAt(next), level, requiredLevel);
    }

    /// <summary>只给核对用：直写段内阶，验突破门槛与被动读口。</summary>
    public async Task RawWritePhaseAsync(long characterId, int phase, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"角色\".character set realm_phase = $2 where id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue((short)phase);
        await command.ExecuteNonQueryAsync(stopping);
    }
}