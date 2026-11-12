using Npgsql;

namespace Character;

/// <summary>
/// 一个人当前的卷轴状态，全部只读：等级、当前等级已有经验、背包张数与绑定态、
/// 角色库里的冷却到期时刻、数据库当前时刻。
/// 冷却到期是持久数据，所以这里读的是 `revive_scroll_cooldown` 那一行，
/// 不是任何进程里的计时器——换一场、战斗服重启都还是同一个答案。
/// </summary>
public sealed record ReviveScrollState(
    bool Found,
    int Level,
    long Exp,
    long Count,
    bool AllBound,
    long ReadyAtMs,
    long NowMs)
{
    public bool Cooling => Found && ReadyAtMs > NowMs;
}

/// <summary>
/// 卷轴状态的唯一读取点。只读入口 `--revive-scroll-state` 与战斗服的
/// `DescribeReviveScroll` 都走这里，两边看到的必须是同一个事实。
/// 真正扣卷轴、扣经验、写冷却那一笔在 `ExpSettlementRepository.UseReviveScrollAsync`
/// ——等级经验的写入仍集中在同一个 Repository，不另开第二条写 level/exp 的路。
/// </summary>
public sealed class ReviveScrollRepository(NpgsqlDataSource dataSource)
{
    public async Task<ReviveScrollState> StateAsync(
        long characterId, string itemId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select c.level, c.exp, "
            + "coalesce((select sum(i.count) from \"角色\".inventory i "
            + "          where i.character_id = c.id and i.item_id = $2), 0), "
            + "coalesce((select bool_and(i.bound) from \"角色\".inventory i "
            + "          where i.character_id = c.id and i.item_id = $2), false), "
            + "coalesce((select extract(epoch from r.ready_at) * 1000 "
            + "          from \"角色\".revive_scroll_cooldown r "
            + "          where r.character_id = c.id), 0), "
            + "extract(epoch from now()) * 1000 "
            + "from \"角色\".character c where c.id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(itemId);

        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return new ReviveScrollState(false, 0, 0, 0, false, 0, 0);
        }

        // extract(epoch ...) 是 numeric，不按 double 取，统一走 Convert。
        return new ReviveScrollState(
            true,
            reader.GetInt32(0),
            reader.GetInt64(1),
            Convert.ToInt64(reader.GetValue(2)),
            Convert.ToBoolean(reader.GetValue(3)),
            (long)Math.Round(Convert.ToDouble(reader.GetValue(4))),
            (long)Math.Round(Convert.ToDouble(reader.GetValue(5))));
    }
}
