using Npgsql;

namespace Social;

public sealed partial class SectRepository
{
    /// <summary>
    /// P3-3a3：只读查一个角色所属宗门的科技等级。归属复用 P2-1c 的 CharacterSectAsync
    /// （名册 ∪ 掌门本人），科技等级从 "社交".sect_tech 取；没宗门或还没写科技行都给 0。
    /// 这里只回等级事实值，加成系数由战斗服按 config/sect_tech.json 折算，社交服不算伤害。
    /// </summary>
    public sealed record SectTechState(bool Found, long SectId, int TechLevel);

    public async Task<SectTechState> SectTechAsync(long characterId, CancellationToken stopping)
    {
        var sect = await CharacterSectAsync(characterId, stopping);
        if (!sect.Found)
        {
            return new SectTechState(false, 0, 0);
        }

        await using var command = dataSource.CreateCommand(
            "select coalesce((select level from \"社交\".sect_tech where sect_id = $1), 0)");
        command.Parameters.AddWithValue(sect.SectId);
        var value = await command.ExecuteScalarAsync(stopping);
        var level = value is null ? 0 : Convert.ToInt32(value);
        return new SectTechState(true, sect.SectId, level);
    }

    /// <summary>
    /// 只给核对用：把某个宗门的科技等级写进事实表，验「有加成 / 无加成」两组对照。
    /// 方案 11.1 没写定科技的产出与升级规则，玩法代码不碰这个入口（见开发日志的欠项登记）。
    /// </summary>
    public async Task<bool> SetSectTechAsync(long sectId, int level, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "insert into \"社交\".sect_tech (sect_id, level) values ($1, $2) "
            + "on conflict (sect_id) do update set level = excluded.level, updated_at = now()");
        command.Parameters.AddWithValue(sectId);
        command.Parameters.AddWithValue(level);
        return await command.ExecuteNonQueryAsync(stopping) == 1;
    }

    /// <summary>
    /// 只给核对用：把某个宗门的 level 改到指定档，验「第 31 人被拒」这类上限切换，
    /// 不做升级逻辑（升级属 P2-1a4）。玩法代码不碰这个入口。
    /// </summary>
    public async Task<bool> SetLevelAsync(long sectId, int level, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "update \"社交\".sect set level = $2 where id = $1");
        command.Parameters.AddWithValue(sectId);
        command.Parameters.AddWithValue(level);
        return await command.ExecuteNonQueryAsync(stopping) == 1;
    }

    // 只读查询里的上限也从配置正本读；等级不在表里（只给核对用改坏了）就给 0。
    int CapOrZero(int level)
    {
        try
        {
            return sectTable.CapFor(level);
        }
        catch (ConfigException)
        {
            return 0;
        }
    }
}
