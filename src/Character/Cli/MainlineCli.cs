using Npgsql;

using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>主线条目只读核对。</summary>
internal static class MainlineCli
{
    public static int? TryReadOnly()
    {
        // C1：主线十二场只读核对。与启动共用同一份 MainlineTable.Load（含与 dungeons.json
        // 的 main、realms.json 的首发三段交叉校验），不连库也不启动进程。
        if (mainlineList)
        {
            try
            {
                var mainlineConfig = CharacterConfig.Load(configPath);
                var table = LoadMainline(mainlineConfig, mainlinePath);
                Console.WriteLine("mainline_list_state=ok version=" + table.Version
                    + " pending=" + (table.Pending ? "true" : "false")
                    + " stages=" + table.Stages.Count
                    + " battle_dungeon=" + table.BattleDungeon
                    + " alignments=" + string.Join("+", table.AlignmentChoices)
                    + " places=" + table.Places.Count
                    + " factions=" + table.Factions.Count
                    + " speakers=" + table.Speakers.Count);
                foreach (var stage in table.Stages)
                {
                    Console.WriteLine("mainline_stage=" + stage.Stage
                        + " title=" + stage.Title
                        + " regions=" + string.Join("+", stage.Regions)
                        + " faction=" + stage.Faction
                        + " speakers=" + string.Join("+", stage.Speakers)
                        + " dialogue=" + string.Join("+", stage.Dialogue.Select(l => l.Speaker + "@" + l.Chapter))
                        + " level_min=" + stage.LevelMin
                        + " requires_breakthrough=" + stage.RequiresBreakthrough
                        + " alignment_choice=" + (stage.AlignmentChoice ? "true" : "false")
                        + " battle=" + stage.Battle);
                }
            }
            catch (ConfigException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }
            catch (IOException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }

            return 0;
        }

        return null;
    }

}