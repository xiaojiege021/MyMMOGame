using System.Text.Json;
using System.Text.Json.Serialization;

namespace Battle;

public sealed partial class BattleConfig
{
    public string Listen { get; init; } = "";
    public string SettlementDir { get; init; } = "";
    public string CharacterDir { get; init; } = "";
    public string SkillTablePath { get; init; } = "";
    public string ClassTablePath { get; init; } = "";
    public string MonsterTablePath { get; init; } = "";
    public string ArenaMatchTablePath { get; init; } = "";
    public string BattlefieldTablePath { get; init; } = "";
    public string SiegeTablePath { get; init; } = "";
    public string DefaultClass { get; init; } = "";

    /// <summary>
    /// 等级经验结算要交给角色服（持久数据的唯一写入方）。留空就是 P0 的
    /// 本机结算口径：只写本地记录，不改角色库；老核对与练习场都走这一条。
    /// </summary>
    public string ExpUpstream { get; init; } = "";

    /// <summary>
    /// 角色服的地址。红符要从背包里扣一张，背包只有角色服能写（技术架构第 3 节）。
    /// 留空时按 exp_upstream 走——两者本来就是同一个进程；都没有就是本机口径，
    /// 这一场用不了红符（回 upstream_off），不从别的地方变出一张来。
    /// </summary>
    public string CharacterUpstream { get; init; } = "";

    public CasterConfig Caster { get; init; } = new();
    public BossConfig Boss { get; init; } = new();
    public ArenaConfig Arena { get; init; } = new();
    public SkillTable Skills { get; private set; } = null!;
    public ClassTable Classes { get; private set; } = null!;

    /// <summary>
    /// Q2：效果层正本（config/effects.json）。老卡临时配置目录没有这份表时是
    /// <see cref="EffectTable.Missing"/>：伤害走恒等、不施加任何效果，老链路一字不动。
    /// </summary>
    public EffectTable Effects { get; private set; } = EffectTable.Missing;

    public string EffectTablePath { get; private set; } = "";

    /// <summary>
    /// K6 怪物表（config/monsters.json）。可选表：老卡临时配置目录没有它时为 null，
    /// 副本 / 竞技场 / 战场 / 城战照跑；显式写了路径却缺文件照样起不来。
    /// </summary>
    public MonsterTable? Monsters { get; private set; }

    /// <summary>副本表：人数上限与进本时的扣减规则。与角色服读同一份配置。</summary>
    public DungeonTable Dungeons { get; private set; } = null!;

    /// <summary>N6 掉落表。缺表时不报任何掉落行。</summary>
    public DungeonDropTable DungeonDrops { get; private set; } = null!;

    /// <summary>
    /// 竞技场模式与统一属性模板（config/arena_match.json）。注意与上面那个
    /// Arena 区分：Arena 是场地半径和移速，这里是 1v1 / 3v3 与模板属性。
    /// </summary>
    public ArenaMatchTable ArenaMatch { get; private set; } = null!;

    /// <summary>
    /// 战场：10 对 10、同队 1.3 倍平衡、每级强化折算与战力权重（config/battlefield.json）。
    /// 与上面的 Arena 与 ArenaMatch 都不同——战场是「装备生效 + 同队压缩」那一层。
    /// </summary>
    public BattlefieldTable Battlefield { get; private set; } = null!;

    /// <summary>
    /// 城战：40 对 40、装备全额生效（config/siege.json）。与战场是两条独立的线：
    /// 战场要压同队战力，城战压根不读平衡系数，也不读竞技场模板。
    /// </summary>
    public SiegeTable Siege { get; private set; } = null!;

    /// <summary>
    /// 宗门科技加成折算（config/sect_tech.json）。只城战读，日常副本不读。
    /// 与 siege.json 分开：老卡把 siege.json 的顶层键口径钉死了，科技段不能塞进去。
    /// </summary>
    public SectTechTable SectTech { get; private set; } = null!;

    public string SectTechTablePath { get; private set; } = "";

    /// <summary>
    /// P3-3a4：城战胜负写回的领地表（config/siege_territory.json）。与 siege.json 分开：
    /// 老卡把 siege.json 的顶层键口径钉死了，领地段不能塞进去。独立可选表也让老卡
    /// 临时配置目录没有这张表时「不结束、不写回」，城战开场与技能结算一字不回退。
    /// </summary>
    public SiegeTerritoryTable SiegeTerritory { get; private set; } = null!;

    public string SiegeTerritoryTablePath { get; private set; } = "";

    /// <summary>
    /// P3-5a：世界 Boss 的地表入口与这一场的首领（config/world_boss.json）。
    /// 独立可选表：老卡的临时配置目录没有这份表时，带 worldboss 参数的连接一律拒，
    /// 副本 / 练习场 / 竞技场 / 战场 / 城战一字不回退。
    /// </summary>
    public WorldBossTable WorldBoss { get; private set; } = null!;

    public string WorldBossTablePath { get; private set; } = "";

    /// <summary>
    /// Q11：日历时区是「这一刻是哪一场世界 Boss」的唯一解释口径（config/calendar.json）。
    /// 世界服与战斗服读同一份，改时区后出现时刻整体平移。
    /// </summary>
    public CalendarTable Calendar { get; private set; } = null!;

    public string CalendarPath { get; private set; } = "";

    /// <summary>
    /// Q9：断线宽限、重连窗口与逃跑惩罚（config/reconnect.json）。可选表：老卡的
    /// 临时配置目录没有它时按「断线即离场、不重连、不惩罚」跑，老链路一字不变。
    /// </summary>
    public ReconnectTable Reconnect { get; private set; } = ReconnectTable.Missing;

    public string ReconnectTablePath { get; private set; } = "";

    /// <summary>世界服地址。留空就不把入口事实值交回去（技能与经验照常结算）。</summary>
    public string WorldUpstream { get; init; } = "";

    /// <summary>社交服地址。留空就不查科技（老链路照跑）；城战依赖社交服的科技（9 节）。</summary>
    public string SocialUpstream { get; init; } = "";

    /// <summary>配置文件的绝对路径，日志里要打出来才知道跑的是哪一份。</summary>
    public string SourcePath { get; private set; } = "";
}
