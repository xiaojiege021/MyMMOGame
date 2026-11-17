using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// C7 数值台账：把角色服启动配置的现值打出来，不连库、不启动进程。
/// 台账表 E 的「改配置重启生效」靠它取证——改 config/character.json 再跑一次，
/// 这里的数值跟着变；缺字段 / 越界在这一步就报 config_error 并返回 2。
/// </summary>
internal static class ConfigCli
{
    public static int? TryReadOnly()
    {
        if (!configShow)
        {
            return null;
        }

        try
        {
            var config = CharacterConfig.Load(configPath);
            Console.WriteLine("character_config=ok");
            Console.WriteLine("listen=" + config.Listen);
            Console.WriteLine("db_pool_max=" + config.DbPoolMax);
            Console.WriteLine("bag_slots=" + config.BagSlots);
            Console.WriteLine("social_upstream=" + config.SocialUpstream);
            Console.WriteLine("dungeon_table=" + config.DungeonTable);
            Console.WriteLine("exp_table=" + config.ExpTable);
            Console.WriteLine("realm_table=" + config.RealmTable);
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
}
