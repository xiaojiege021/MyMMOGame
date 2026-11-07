using System.Text.Json;

namespace Battle;

/// <summary>
/// 一场通关只写一条结算记录。文件名就是事务号，同一个号再写一次直接返回原来的号，
/// 不会重复发奖。写的时候先落临时文件、刷盘，再 rename 成正式名；进程在中途挂掉
/// 只会留下一个 .tmp，不会出现半条记录。
///
/// 现在落在本地目录，等 P1-1 的角色服做起来，这一段换成角色库里的那一笔事务，
/// 接口和事务号的含义都不用改。
/// </summary>
public sealed class SettlementStore(string dir)
{
    public string Dir { get; } = Path.GetFullPath(dir);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>已经写下的结算条数。启动时打出来，重启前后比一下就知道有没有多写。</summary>
    public int Count()
    {
        if (!Directory.Exists(Dir))
        {
            return 0;
        }
        return Directory.EnumerateFiles(Dir, "*.json").Count();
    }

    /// <summary>写一条结算。已存在同一个事务号就不再写，返回同一个号。</summary>
    public string Write(SettlementRecord record)
    {
        Directory.CreateDirectory(Dir);
        var final = Path.Combine(Dir, record.SettlementId + ".json");
        if (File.Exists(final))
        {
            return record.SettlementId;
        }

        var tmp = final + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record, JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        // 同目录 rename 是原子的：要么看到完整的记录，要么看不到。
        File.Move(tmp, final, overwrite: false);
        return record.SettlementId;
    }
}

/// <summary>
/// 一次通关的结算记录。等级经验和掉落都在这里，等角色服接上以后整条搬进那一笔事务。
/// 这一版没有掉落，字段先不占位，掉落表在 P1-2。
/// </summary>
public sealed record SettlementRecord
{
    public string Schema { get; init; } = "battle-settlement/1";
    public required string SettlementId { get; init; }
    public required ulong SessionId { get; init; }
    public required ulong CasterId { get; init; }
    public required ulong BossId { get; init; }
    public required int SkillTableVersion { get; init; }
    public required long ExpGain { get; init; }
    public required long DamageTotal { get; init; }
    public required int CastCount { get; init; }
    public required DateTimeOffset ClosedAt { get; init; }
}
