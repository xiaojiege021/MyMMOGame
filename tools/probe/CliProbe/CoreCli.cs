using Npgsql;

using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>角色本体与维护入口：迁移 / 删号 / 统计 / 报表 / 冷却重置。</summary>
internal static class CoreCli
{
    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        var dataSource = ctx.DataSource;
        var characters = ctx.Characters;
        if (migrate)
        {
            var sql = File.ReadAllText(sqlPath);
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("migrate=ok sql=" + sqlPath);
        }

        if (scrollResetOf != 0)
        {
            // 只给核对用：清掉全局冷却，让同一轮能连着验「用掉」「经验为零」等场景。
            await using var reset = dataSource.CreateCommand(
                "delete from \"角色\".revive_scroll_cooldown where character_id = $1");
            reset.Parameters.AddWithValue(scrollResetOf);
            var rows = await reset.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("scroll_cooldown_reset=ok id=" + scrollResetOf + " rows=" + rows);
        }

        if (boosterResetOf != 0)
        {
            // 只给核对用：删掉这一行，回到「从来没用过」，不碰背包与经验。
            await using var reset = dataSource.CreateCommand(
                "delete from \"角色\".exp_booster where character_id = $1");
            reset.Parameters.AddWithValue(boosterResetOf);
            var rows = await reset.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("exp_booster_reset=ok id=" + boosterResetOf + " rows=" + rows);
        }

        if (boosterExpireOf != 0)
        {
            // 只给核对用：把到期时刻挪到过去，验「到点后经验恢复原倍率」，
            // 不用真等配置里那一段时长。只动 expires_at，别的列一概不写。
            await using var expire = dataSource.CreateCommand(
                "update \"角色\".exp_booster set expires_at = now() - interval '1 second' "
                + "where character_id = $1");
            expire.Parameters.AddWithValue(boosterExpireOf);
            var rows = await expire.ExecuteNonQueryAsync(CancellationToken.None);
            Console.WriteLine("exp_booster_expire=ok id=" + boosterExpireOf + " rows=" + rows);
        }

        if (deleteName != null)
        {
            var removed = await characters.DeleteAsync(deleteName, CancellationToken.None);
            Console.WriteLine("delete=ok name=" + deleteName + " rows=" + removed);
        }

        if (stats)
        {
            Console.WriteLine("characters=" + await characters.CountAsync(CancellationToken.None));
        }

        if (reportName != null)
        {
            var report = await characters.ReportAsync(reportName, CancellationToken.None);
            if (report is null)
            {
                Console.WriteLine("report=missing name=" + reportName);
            }
            else
            {
                Console.WriteLine("report=ok name=" + reportName
                    + " id=" + report.Id
                    + " race=" + report.Race
                    + " class=" + report.ClassId
                    + " branch=" + report.Branch
                    + " level=" + report.Level
                    + " realm=" + report.Realm
                    + " main_stage_null=" + report.MainStageEmpty
                    + " alignment_null=" + report.AlignmentEmpty
                    + " appearance_len=" + report.AppearanceLength);
            }
        }

        return null;
    }

}