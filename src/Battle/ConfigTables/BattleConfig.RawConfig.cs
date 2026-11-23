using System.Text.Json;
using System.Text.Json.Serialization;

namespace Battle;

public sealed partial class BattleConfig
{

    sealed class RawConfig
    {
        public string Listen { get; init; } = "";
        public string SettlementDir { get; init; } = "";
        public string CharacterDir { get; init; } = "var/battle/characters";
        public string SkillTable { get; init; } = "skills.json";
        public string ClassTable { get; init; } = "classes.json";
        /// <summary>Q2 效果表；留空时按默认文件名找，找不到就降级为恒等伤害。</summary>
        public string EffectTable { get; init; } = "";
        /// <summary>K6 怪物表；留空时按默认文件名找，找不到就降级为没有怪物。</summary>
        public string MonsterTable { get; init; } = "";
        public string DungeonTable { get; init; } = "dungeons.json";
        public string DungeonDropTable { get; init; } = "dungeon_drops.json";
        public string ArenaMatchTable { get; init; } = "";
        public string BattlefieldTable { get; init; } = "";
        public string SiegeTable { get; init; } = "";
        public string SectTechTable { get; init; } = "";
        /// <summary>P3-3a4 领地表；留空时按默认文件名找，找不到就降级为不写回。</summary>
        public string SiegeTerritoryTable { get; init; } = "";
        /// <summary>P3-5a 世界 Boss 表；留空时按默认文件名找，找不到就降级为没有入口。</summary>
        public string WorldBossTable { get; init; } = "";
        public string CalendarTable { get; init; } = "";
        /// <summary>Q9 断线重连表；留空时按默认文件名找，找不到就降级为不重连、不惩罚。</summary>
        public string ReconnectTable { get; init; } = "";
        /// <summary>社交服地址；留空就不查宗门科技，老链路照跑。</summary>
        public string SocialUpstream { get; init; } = "";
        /// <summary>世界服地址；留空就不把入口事实值交回去。</summary>
        public string WorldUpstream { get; init; } = "";
        public string DefaultClass { get; init; } = "warrior";
        public string ExpUpstream { get; init; } = "";

        /// <summary>角色服地址；留空按 exp_upstream 走。</summary>
        public string CharacterUpstream { get; init; } = "";
        public CasterConfig Caster { get; init; } = new();
        public BossConfig Boss { get; init; } = new();
        public ArenaConfig Arena { get; init; } = new();
    }
}
